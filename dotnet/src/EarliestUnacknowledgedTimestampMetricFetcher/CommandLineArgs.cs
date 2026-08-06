namespace EarliestUnacknowledgedTimestampMetricFetcher;

/// <summary>
/// Minimal command-line argument parser. Supports long and short flags in the forms
/// <c>--name value</c>, <c>--name=value</c>, and <c>-n value</c>. Unknown flags are rejected.
/// </summary>
internal sealed class CommandLineArgs
{
    private readonly Dictionary<string, string> _values =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HelpRequested { get; private set; }

    // Canonical option name -> accepted aliases (all lower-case, without leading dashes).
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["subscription"] = new[] { "subscription", "s" },
        ["resource-group"] = new[] { "resource-group", "resourcegroup", "g" },
        ["account"] = new[] { "account", "a" },
        ["region"] = new[] { "region", "r" },
        ["start"] = new[] { "start" },
        ["end"] = new[] { "end" },
        ["error-start-time"] = new[] { "error-start-time", "errorstarttime" },
        ["lookback-minutes"] = new[] { "lookback-minutes", "lookbackminutes" },
    };

    private CommandLineArgs()
    {
    }

    /// <summary>
    /// Parses the raw argument array. Throws <see cref="ArgumentException"/> on invalid input.
    /// </summary>
    public static CommandLineArgs Parse(string[] args)
    {
        var result = new CommandLineArgs();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (arg is "-h" or "--help" or "/?" or "-?")
            {
                result.HelpRequested = true;
                continue;
            }

            if (!arg.StartsWith('-'))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'. Use --help for usage.");
            }

            var trimmed = arg.TrimStart('-');
            string name;
            string? value = null;

            var eq = trimmed.IndexOf('=');
            if (eq >= 0)
            {
                name = trimmed[..eq];
                value = trimmed[(eq + 1)..];
            }
            else
            {
                name = trimmed;
            }

            var canonical = ResolveCanonical(name)
                ?? throw new ArgumentException($"Unknown option '--{name}'. Use --help for usage.");

            if (value is null)
            {
                if (i + 1 >= args.Length || (args[i + 1].StartsWith('-') && args[i + 1].Length > 1 &&
                                             !char.IsDigit(args[i + 1][1])))
                {
                    throw new ArgumentException($"Option '--{canonical}' requires a value.");
                }

                value = args[++i];
            }

            if (!result._values.TryAdd(canonical, value.Trim()))
            {
                throw new ArgumentException(
                    $"Option '--{canonical}' was specified more than once.");
            }
        }

        if (result._values.ContainsKey("error-start-time") &&
            (result._values.ContainsKey("start") || result._values.ContainsKey("end")))
        {
            throw new ArgumentException(
                "--error-start-time cannot be combined with --start or --end.");
        }

        if (result._values.ContainsKey("error-start-time") &&
            !result._values.ContainsKey("lookback-minutes"))
        {
            throw new ArgumentException(
                "--lookback-minutes is required when --error-start-time is used.");
        }

        if (result._values.ContainsKey("lookback-minutes") &&
            !result._values.ContainsKey("error-start-time"))
        {
            throw new ArgumentException(
                "--lookback-minutes can only be used together with --error-start-time.");
        }

        return result;
    }

    private static string? ResolveCanonical(string name)
    {
        foreach (var pair in Aliases)
        {
            if (pair.Value.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>Returns the value for a canonical option, or null if it was not supplied.</summary>
    public string? Get(string canonicalName) =>
        _values.TryGetValue(canonicalName, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public static string UsageText =>
        """
        Usage: EarliestUnacknowledgedTimestampMetricFetcher [options]

        All options are optional. Any value not supplied on the command line will be
        prompted for interactively.

          -s, --subscription <id>       Azure Subscription ID
          -g, --resource-group <name>   Resource Group name
          -a, --account <name>          Cosmos DB account name
          -r, --region <name>           Target Region (e.g. westus2)
              --start <iso8601>         Start time in UTC (e.g. 2026-07-19T00:00:00Z)
              --end <iso8601>           End time in UTC (e.g. 2026-07-20T00:00:00Z)
              --error-start-time <iso8601>
                                        Error start timestamp (UTC). Fetches the period
                                        immediately BEFORE this time (its length is set by
                                        --lookback-minutes) and reports the minimum non-zero
                                        metric value in that window. Mutually exclusive with
                                        --start/--end. Requires --lookback-minutes.
              --lookback-minutes <n>    Length, in whole minutes, of the window fetched before
                                        --error-start-time (e.g. 60). Required with, and only
                                        valid together with, --error-start-time.
          -h, --help                    Show this help and exit

        Notes:
          * If neither --start nor --end is provided, the last 24 hours is used.
          * If only one of --start/--end is provided, you will be prompted for the other.
          * --error-start-time cannot be combined with --start or --end.
          * --error-start-time requires --lookback-minutes (a positive whole number).
          * Values with spaces must be quoted, e.g. --resource-group "my rg".

        Examples:
          EarliestUnacknowledgedTimestampMetricFetcher -s 00000000-0000-0000-0000-000000000000 -g my-rg -a my-account -r westus2
          EarliestUnacknowledgedTimestampMetricFetcher --account my-account --region westus2 --start 2026-07-19T00:00:00Z --end 2026-07-20T00:00:00Z
          EarliestUnacknowledgedTimestampMetricFetcher -a my-account -r westus2 --error-start-time 2026-07-24T20:30:00Z --lookback-minutes 60
        """;
}
