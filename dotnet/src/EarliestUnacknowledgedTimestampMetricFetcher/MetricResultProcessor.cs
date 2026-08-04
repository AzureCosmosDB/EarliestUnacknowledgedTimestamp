using System.Text;
using System.Text.Json;

namespace EarliestUnacknowledgedTimestampMetricFetcher;

/// <summary>
/// Parses Azure Monitor REST responses: discovers the region dimension name from the metric
/// definition, and converts a metric-values response into CSV rows.
/// </summary>
internal static class MetricResultProcessor
{
    /// <summary>
    /// Finds the dimension whose name looks like a region on the given metric.
    /// Returns null if the metric or a region-like dimension cannot be found.
    /// </summary>
    public static string? DiscoverRegionDimension(string metricDefinitionsJson, string metricName)
    {
        using var doc = JsonDocument.Parse(metricDefinitionsJson);
        if (!doc.RootElement.TryGetProperty("value", out var definitions) ||
            definitions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var def in definitions.EnumerateArray())
        {
            var name = ReadMetricName(def);
            if (!string.Equals(name, metricName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!def.TryGetProperty("dimensions", out var dimensions) ||
                dimensions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var dimensionNames = new List<string>();
            foreach (var dim in dimensions.EnumerateArray())
            {
                var dimName = dim.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (!string.IsNullOrWhiteSpace(dimName))
                {
                    dimensionNames.Add(dimName!);
                }
            }

            // Prefer a dimension that mentions "region"; otherwise, if there is exactly one
            // dimension, use it.
            var regionDim = dimensionNames.FirstOrDefault(
                d => d.Contains("region", StringComparison.OrdinalIgnoreCase));
            if (regionDim is not null)
            {
                return regionDim;
            }

            return dimensionNames.Count == 1 ? dimensionNames[0] : null;
        }

        return null;
    }

    private static string? ReadMetricName(JsonElement definition)
    {
        if (definition.TryGetProperty("name", out var nameElement))
        {
            if (nameElement.ValueKind == JsonValueKind.Object &&
                nameElement.TryGetProperty("value", out var value))
            {
                return value.GetString();
            }

            if (nameElement.ValueKind == JsonValueKind.String)
            {
                return nameElement.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Result of scanning a metric-values response for the smallest non-zero data point.
    /// </summary>
    public readonly record struct MinimumNonZeroResult(
        bool Found,
        double Value,
        string? TimestampUtc,
        int TotalPoints,
        int NonZeroPoints);

    /// <summary>
    /// Scans every data point in a metric-values response and returns the smallest non-zero
    /// value (using the given aggregation, e.g. "Minimum") together with its timestamp.
    /// If no non-zero data point exists (all zero, or no data), <c>Found</c> is false and
    /// <c>Value</c> is 0.
    /// </summary>
    public static MinimumNonZeroResult FindMinimumNonZero(string metricValuesJson, string aggregation)
    {
        var aggProperty = aggregation.ToLowerInvariant();
        double? min = null;
        string? minTimestamp = null;
        var total = 0;
        var nonZero = 0;

        using var doc = JsonDocument.Parse(metricValuesJson);
        if (!doc.RootElement.TryGetProperty("value", out var metrics) ||
            metrics.ValueKind != JsonValueKind.Array)
        {
            return new MinimumNonZeroResult(false, 0, null, 0, 0);
        }

        foreach (var metric in metrics.EnumerateArray())
        {
            if (!metric.TryGetProperty("timeseries", out var series) ||
                series.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var ts in series.EnumerateArray())
            {
                if (!ts.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var point in data.EnumerateArray())
                {
                    total++;
                    if (!point.TryGetProperty(aggProperty, out var val) ||
                        val.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var value = val.GetDouble();
                    if (value == 0d)
                    {
                        continue;
                    }

                    nonZero++;
                    if (min is null || value < min.Value)
                    {
                        min = value;
                        minTimestamp = point.TryGetProperty("timeStamp", out var t) ? t.GetString() : null;
                    }
                }
            }
        }

        return min is null
            ? new MinimumNonZeroResult(false, 0, null, total, 0)
            : new MinimumNonZeroResult(true, min.Value, minTimestamp, total, nonZero);
    }

    /// <summary>
    /// Builds a CSV (timestampUtc, targetRegion, &lt;aggregation&gt;) from a metric-values response.
    /// Returns the CSV text and the number of data points written.
    /// </summary>
    public static (string Csv, int PointCount) ToCsv(
        string metricValuesJson,
        string aggregation,
        string fallbackRegion)
    {
        var aggProperty = aggregation.ToLowerInvariant();
        var sb = new StringBuilder();
        sb.AppendLine($"timestampUtc,targetRegion,{aggProperty}");

        var points = 0;
        using var doc = JsonDocument.Parse(metricValuesJson);
        if (!doc.RootElement.TryGetProperty("value", out var metrics) ||
            metrics.ValueKind != JsonValueKind.Array)
        {
            return (sb.ToString(), 0);
        }

        foreach (var metric in metrics.EnumerateArray())
        {
            if (!metric.TryGetProperty("timeseries", out var series) ||
                series.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var ts in series.EnumerateArray())
            {
                var region = ReadRegionFromMetadata(ts) ?? fallbackRegion;

                if (!ts.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var point in data.EnumerateArray())
                {
                    var timeStamp = point.TryGetProperty("timeStamp", out var t) ? t.GetString() : null;
                    string valueText = string.Empty;
                    if (point.TryGetProperty(aggProperty, out var val) &&
                        val.ValueKind == JsonValueKind.Number)
                    {
                        valueText = val.GetRawText();
                    }

                    // Include every timestamp bucket, even empty ones, for completeness.
                    sb.Append(CsvEscape(timeStamp ?? string.Empty)).Append(',')
                      .Append(CsvEscape(region)).Append(',')
                      .Append(CsvEscape(valueText)).AppendLine();
                    points++;
                }
            }
        }

        return (sb.ToString(), points);
    }

    private static string? ReadRegionFromMetadata(JsonElement timeseries)
    {
        if (!timeseries.TryGetProperty("metadatavalues", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var meta in metadata.EnumerateArray())
        {
            var name = meta.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.Object &&
                       n.TryGetProperty("value", out var nv)
                ? nv.GetString()
                : null;

            if (name is not null && name.Contains("region", StringComparison.OrdinalIgnoreCase) &&
                meta.TryGetProperty("value", out var mv))
            {
                return mv.GetString();
            }
        }

        return null;
    }

    private static string CsvEscape(string field)
    {
        if (field.Contains(',') || field.Contains('"') ||
            field.Contains('\r') || field.Contains('\n'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        return field;
    }
}
