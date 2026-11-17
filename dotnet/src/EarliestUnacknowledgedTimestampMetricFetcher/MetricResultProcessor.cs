using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EarliestUnacknowledgedTimestampMetricFetcher;

/// <summary>
/// Parses Azure Monitor REST responses: discovers dimension names from the metric definition,
/// and converts a metric-values response into CSV rows.
/// </summary>
internal static class MetricResultProcessor
{
    /// <summary>
    /// Returns the dimension names defined on the given metric, or null if the metric is not
    /// present in the metric definitions response.
    /// </summary>
    public static IReadOnlyList<string>? GetDimensionNames(string metricDefinitionsJson, string metricName)
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

            var dimensionNames = new List<string>();
            if (def.TryGetProperty("dimensions", out var dimensions) &&
                dimensions.ValueKind == JsonValueKind.Array)
            {
                foreach (var dim in dimensions.EnumerateArray())
                {
                    var dimName = dim.ValueKind == JsonValueKind.String
                        ? dim.GetString()
                        : dim.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(dimName))
                    {
                        dimensionNames.Add(dimName!);
                    }
                }
            }

            return dimensionNames;
        }

        return null;
    }

    /// <summary>
    /// Finds a dimension by exact (case-insensitive) preferred name first; otherwise returns the
    /// first dimension whose name matches <paramref name="pattern"/> and does not match
    /// <paramref name="excludePattern"/>. Returns null when nothing matches.
    /// </summary>
    public static string? FindDimension(
        IReadOnlyList<string> dimensionNames,
        IEnumerable<string> preferredNames,
        string pattern,
        string? excludePattern = null)
    {
        foreach (var preferred in preferredNames)
        {
            var match = dimensionNames.FirstOrDefault(
                d => string.Equals(d, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return dimensionNames.FirstOrDefault(
            d => Regex.IsMatch(d, pattern, RegexOptions.IgnoreCase) &&
                 (string.IsNullOrEmpty(excludePattern) ||
                  !Regex.IsMatch(d, excludePattern, RegexOptions.IgnoreCase)));
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
    /// Dimension name used in the query together with the value used when a time series does
    /// not report that dimension in its metadata.
    /// </summary>
    public readonly record struct DimensionColumn(string DimensionName, string FallbackValue);

    /// <summary>
    /// Builds a CSV (timestampUtc, sourceRegion, targetRegion, collectionName, &lt;aggregation&gt;)
    /// from a metric-values response. Returns the CSV text and the number of data points written.
    /// </summary>
    public static (string Csv, int PointCount) ToCsv(
        string metricValuesJson,
        string aggregation,
        DimensionColumn sourceRegion,
        DimensionColumn targetRegion,
        DimensionColumn collectionName)
    {
        var aggProperty = aggregation.ToLowerInvariant();
        var sb = new StringBuilder();
        sb.AppendLine($"timestampUtc,sourceRegion,targetRegion,collectionName,{aggProperty}");

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
                var seriesSource = ReadDimensionFromMetadata(ts, sourceRegion);
                var seriesTarget = ReadDimensionFromMetadata(ts, targetRegion);
                var seriesCollection = ReadDimensionFromMetadata(ts, collectionName);

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
                      .Append(CsvEscape(seriesSource)).Append(',')
                      .Append(CsvEscape(seriesTarget)).Append(',')
                      .Append(CsvEscape(seriesCollection)).Append(',')
                      .Append(CsvEscape(valueText)).AppendLine();
                    points++;
                }
            }
        }

        return (sb.ToString(), points);
    }

    private static string ReadDimensionFromMetadata(JsonElement timeseries, DimensionColumn column)
    {
        if (!timeseries.TryGetProperty("metadatavalues", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Array)
        {
            return column.FallbackValue;
        }

        foreach (var meta in metadata.EnumerateArray())
        {
            var name = meta.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.Object &&
                       n.TryGetProperty("value", out var nv)
                ? nv.GetString()
                : null;

            if (string.Equals(name, column.DimensionName, StringComparison.OrdinalIgnoreCase) &&
                meta.TryGetProperty("value", out var mv) &&
                mv.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(mv.GetString()))
            {
                return mv.GetString()!;
            }
        }

        return column.FallbackValue;
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
