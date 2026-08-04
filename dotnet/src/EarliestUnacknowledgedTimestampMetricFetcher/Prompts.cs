using System.Globalization;

namespace EarliestUnacknowledgedTimestampMetricFetcher;

/// <summary>
/// Small helpers for reading and validating interactive console input.
/// </summary>
internal static class Prompts
{
    /// <summary>
    /// Returns <paramref name="provided"/> if it has a value; otherwise prompts the user.
    /// When an inline value is used, it is echoed so the run is self-documenting.
    /// </summary>
    public static string ResolveRequired(string label, string? provided)
    {
        if (!string.IsNullOrWhiteSpace(provided))
        {
            Console.WriteLine($"{label}: {provided} (from command line)");
            return provided.Trim();
        }

        return ReadRequired(label);
    }

    public static string ReadRequired(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine();
            if (value is null)
            {
                throw new ArgumentException($"No input received for '{label}' (end of input).");
            }

            value = value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            Console.WriteLine("  A value is required. Please try again.");
        }
    }

    /// <summary>
    /// Resolves the time range from optional inline values, prompting only for what is missing.
    /// If neither bound is available (no inline value and blank prompt), defaults to the last
    /// 24 hours. Accepts ISO 8601 timestamps. Times are normalized to UTC.
    /// </summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ResolveTimeRange(
        string? startArg,
        string? endArg)
    {
        var needPrompt = string.IsNullOrWhiteSpace(startArg) || string.IsNullOrWhiteSpace(endArg);
        if (needPrompt)
        {
            Console.WriteLine();
            Console.WriteLine("Time range (UTC). Leave blank to use the last 24 hours.");
            Console.WriteLine("Accepted format: ISO 8601, e.g. 2026-07-19T00:00:00Z");
        }

        var start = ResolveOptionalTimestamp("Start time (UTC)", startArg);
        var end = ResolveOptionalTimestamp("End time (UTC)", endArg);

        if (start is null && end is null)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            Console.WriteLine("  No time range provided - defaulting to the last 24 hours.");
            return (nowUtc.AddHours(-24), nowUtc);
        }

        // If only one bound is supplied, derive a sensible 24-hour window around it.
        var startUtc = start ?? (end!.Value.AddHours(-24));
        var endUtc = end ?? (start!.Value.AddHours(24));

        if (endUtc <= startUtc)
        {
            throw new ArgumentException("End time must be later than start time.");
        }

        return (startUtc, endUtc);
    }

    /// <summary>
    /// Parses an inline timestamp if provided; otherwise prompts for it (blank allowed).
    /// </summary>
    private static DateTimeOffset? ResolveOptionalTimestamp(string label, string? provided)
    {
        if (!string.IsNullOrWhiteSpace(provided))
        {
            var parsed = ParseTimestamp(provided, label);
            Console.WriteLine($"  {label}: {provided} (from command line)");
            return parsed;
        }

        return ReadOptionalTimestamp($"  {label}");
    }

    /// <summary>
    /// Parses an inline (command-line) ISO 8601 timestamp, throwing a clear error if invalid.
    /// </summary>
    public static DateTimeOffset ParseTimestampArg(string raw, string label) =>
        ParseTimestamp(raw, label);

    /// <summary>
    /// Parses the --lookback-minutes value: a positive whole number of minutes.
    /// </summary>
    public static int ParseLookbackMinutes(string raw)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) &&
            minutes > 0)
        {
            return minutes;
        }

        throw new ArgumentException(
            $"Could not parse --lookback-minutes value '{raw}'. " +
            "Provide a positive whole number of minutes, e.g. 60.");
    }

    private static DateTimeOffset ParseTimestamp(string raw, string label)
    {
        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"Could not parse {label} value '{raw}'. Use ISO 8601, e.g. 2026-07-19T00:00:00Z.");
    }

    private static DateTimeOffset? ReadOptionalTimestamp(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var raw = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }

            Console.WriteLine("  Could not parse the timestamp. Use ISO 8601, e.g. 2026-07-19T00:00:00Z.");
        }
    }
}
