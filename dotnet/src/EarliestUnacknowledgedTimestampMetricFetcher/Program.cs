using System.Text;
using Azure.Identity;

namespace EarliestUnacknowledgedTimestampMetricFetcher;

/// <summary>
/// Standalone console tool that fetches Azure Monitor metric values for a Cosmos DB
/// (Microsoft.DocumentDB) account and saves the result to JSON and CSV files.
///
/// Everything the tool needs is supplied interactively at runtime - no account, subscription,
/// resource group, or region information is embedded in this application.
/// </summary>
internal static class Program
{
    // Fixed metric this tool is designed to fetch.
    private const string MetricName = "EarliestUnacknowledgedTimestampMetricView";
    private const string MetricNamespace = "Microsoft.DocumentDB/databaseAccounts";
    private const string ResourceProvider = "Microsoft.DocumentDB";
    private const string ResourceType = "databaseAccounts";

    // Query defaults.
    private const string Aggregation = "Minimum";
    // Time grain (interval) for the metric query. This metric supports a one-minute grain.
    private const string Interval = "PT1M";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        CommandLineArgs cli;
        try
        {
            cli = CommandLineArgs.Parse(args);
        }
        catch (ArgumentException argEx)
        {
            Console.Error.WriteLine($"ERROR: {argEx.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLineArgs.UsageText);
            return 2;
        }

        if (cli.HelpRequested)
        {
            Console.WriteLine(CommandLineArgs.UsageText);
            return 0;
        }

        PrintBanner();

        try
        {
            // 1. Collect input - use inline arguments where provided, prompt for the rest.
            var subscriptionId = Prompts.ResolveRequired("Subscription ID", cli.Get("subscription"));
            var resourceGroup = Prompts.ResolveRequired("Resource Group name", cli.Get("resource-group"));
            var accountName = Prompts.ResolveRequired("Account name", cli.Get("account"));

            // Source region, target region, and collection name are mandatory command-line
            // arguments (enforced by the argument parser). The collection name may be empty.
            var sourceRegion = cli.Get("source-region")!;
            var targetRegion = cli.Get("target-region")!;
            var collectionName = cli.GetAllowEmpty("collection")!;
            var collectionNameText = collectionName.Length == 0 ? "(empty)" : collectionName;
            Console.WriteLine($"Source Region: {sourceRegion} (from command line)");
            Console.WriteLine($"Target Region: {targetRegion} (from command line)");
            Console.WriteLine($"Collection name: {collectionNameText} (from command line)");

            // Determine the time window. Two modes:
            //   * error-start-time mode: fetch the period immediately before the error start
            //     timestamp (its length is given by --lookback-minutes) and report the minimum
            //     non-zero value in that window.
            //   * normal mode: use --start/--end (or prompt; default last 24h).
            var errorStartArg = cli.Get("error-start-time");
            var errorMode = errorStartArg is not null;
            DateTimeOffset startUtc, endUtc, errorStartUtc = default;
            var lookbackMinutes = 0;

            if (errorMode)
            {
                // --lookback-minutes is required in this mode (enforced by the argument parser).
                lookbackMinutes = Prompts.ParseLookbackMinutes(cli.Get("lookback-minutes")!);
                errorStartUtc = Prompts.ParseTimestampArg(errorStartArg!, "error-start-time");
                endUtc = errorStartUtc;
                startUtc = errorStartUtc.AddMinutes(-lookbackMinutes);
                PrintErrorModeExplanation(errorStartUtc, startUtc, lookbackMinutes);
            }
            else
            {
                (startUtc, endUtc) = Prompts.ResolveTimeRange(cli.Get("start"), cli.Get("end"));
                PrintNormalModeExplanation(startUtc, endUtc);
            }

            var resourceUri =
                $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}" +
                $"/providers/{ResourceProvider}/{ResourceType}/{accountName}";

            Console.WriteLine();
            Console.WriteLine("A browser window will open for sign-in...");

            var credential = new InteractiveBrowserCredential(
                new InteractiveBrowserCredentialOptions
                {
                    TokenCachePersistenceOptions = new TokenCachePersistenceOptions()
                });

            using var client = new MetricsRestClient(credential);

            // 2. Discover the dimension names from the metric definition.
            Console.WriteLine("Retrieving metric definitions...");
            var definitionsJson = await client
                .GetMetricDefinitionsAsync(resourceUri, MetricNamespace)
                .ConfigureAwait(false);

            var dimensionNames = MetricResultProcessor.GetDimensionNames(definitionsJson, MetricName);
            if (dimensionNames is null)
            {
                Console.Error.WriteLine(
                    $"ERROR: Metric '{MetricName}' is not available for this resource. " +
                    "Verify the account name and that the metric is available.");
                return 3;
            }

            var availableDimensions = dimensionNames.Count == 0 ? "none" : string.Join(", ", dimensionNames);

            var sourceRegionDimension = MetricResultProcessor.FindDimension(
                dimensionNames, new[] { "SourceRegion" }, "source");
            if (sourceRegionDimension is null)
            {
                Console.Error.WriteLine(
                    $"ERROR: Could not identify a source region dimension for metric '{MetricName}'. " +
                    $"Available dimensions: {availableDimensions}.");
                return 3;
            }
            Console.WriteLine($"Using source region dimension: {sourceRegionDimension}");

            var targetRegionDimension = MetricResultProcessor.FindDimension(
                dimensionNames, new[] { "TargetRegion", "Region" }, "region", "source");
            if (targetRegionDimension is null)
            {
                Console.Error.WriteLine(
                    $"ERROR: Could not identify a target region dimension for metric '{MetricName}'. " +
                    $"Available dimensions: {availableDimensions}.");
                return 3;
            }
            Console.WriteLine($"Using target region dimension: {targetRegionDimension}");

            var collectionDimension = MetricResultProcessor.FindDimension(
                dimensionNames, new[] { "CollectionName" }, "collection|container");
            if (collectionDimension is null)
            {
                Console.Error.WriteLine(
                    $"ERROR: Could not identify a collection name dimension for metric '{MetricName}'. " +
                    $"Available dimensions: {availableDimensions}.");
                return 3;
            }
            Console.WriteLine($"Using collection name dimension: {collectionDimension}");

            var filter = string.Join(
                " and ",
                FilterClause(sourceRegionDimension, sourceRegion),
                FilterClause(targetRegionDimension, targetRegion),
                FilterClause(collectionDimension, collectionName));

            var interval = Interval;
            Console.WriteLine($"Using time grain (interval): {interval}");

            // 3. Query metric values.
            Console.WriteLine(
                $"Querying metric values for source region '{sourceRegion}', target region " +
                $"'{targetRegion}', collection '{collectionNameText}'...");
            var valuesJson = await client
                .GetMetricValuesAsync(
                    resourceUri,
                    MetricNamespace,
                    MetricName,
                    Aggregation,
                    interval,
                    startUtc,
                    endUtc,
                    filter)
                .ConfigureAwait(false);

            // 4. Write outputs.
            var (csv, pointCount) = MetricResultProcessor.ToCsv(
                valuesJson,
                Aggregation,
                new MetricResultProcessor.DimensionColumn(sourceRegionDimension, sourceRegion),
                new MetricResultProcessor.DimensionColumn(targetRegionDimension, targetRegion),
                new MetricResultProcessor.DimensionColumn(collectionDimension, collectionName));
            var (jsonPath, csvPath, outputDir, baseName) =
                await WriteOutputsAsync(accountName, sourceRegion, targetRegion, collectionName, valuesJson, csv)
                    .ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("Done.");
            Console.WriteLine($"  Metric:      {MetricName}");
            Console.WriteLine($"  Source:      {sourceRegion}");
            Console.WriteLine($"  Target:      {targetRegion}");
            Console.WriteLine($"  Collection:  {collectionNameText}");
            Console.WriteLine($"  Time range:  {Iso(startUtc)} -> {Iso(endUtc)} (UTC)");
            Console.WriteLine($"  Aggregation: {Aggregation}, interval {interval}");
            Console.WriteLine($"  Data points: {pointCount}");
            Console.WriteLine($"  JSON file:   {jsonPath}");
            Console.WriteLine($"  CSV file:    {csvPath}");
            if (pointCount == 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Note: no data points were returned for the selected dimensions and time range.");
            }

            // 5. In error-start-time mode, compute and report the minimum non-zero value.
            if (errorMode)
            {
                var result = MetricResultProcessor.FindMinimumNonZero(valuesJson, Aggregation);
                var resultPath = await WriteResultFileAsync(
                        outputDir,
                        baseName,
                        accountName,
                        sourceRegion,
                        targetRegion,
                        collectionNameText,
                        errorStartUtc,
                        startUtc,
                        endUtc,
                        lookbackMinutes,
                        result)
                    .ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine(
                    $"Minimum non-zero result ({lookbackMinutes} minutes before error start):");
                if (result.Found)
                {
                    Console.WriteLine($"  Minimum non-zero value: {Num(result.Value)}");
                    Console.WriteLine($"  Emitted at (UTC):       {result.TimestampUtc}");
                }
                else
                {
                    Console.WriteLine($"  Minimum non-zero value: 0");
                    Console.WriteLine();
                    Console.WriteLine(ZeroResultMessage(startUtc, lookbackMinutes));
                }
                Console.WriteLine($"  Result file: {resultPath}");
            }

            return 0;
        }
        catch (MetricsApiException apiEx)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {apiEx.Message}");
            return 4;
        }
        catch (AuthenticationFailedException authEx)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: Sign-in failed or was cancelled. {authEx.Message}");
            return 5;
        }
        catch (ArgumentException argEx)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {argEx.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: Unexpected failure. {ex.Message}");
            return 1;
        }
    }

    private static async Task<(string JsonPath, string CsvPath, string OutputDir, string BaseName)>
        WriteOutputsAsync(
            string accountName,
            string sourceRegion,
            string targetRegion,
            string collectionName,
            string json,
            string csv)
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "output");
        Directory.CreateDirectory(outputDir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var parts = new List<string> { Sanitize(accountName), Sanitize(sourceRegion), Sanitize(targetRegion) };
        if (collectionName.Length > 0)
        {
            parts.Add(Sanitize(collectionName));
        }
        parts.Add(stamp);
        var baseName = string.Join("_", parts);
        var jsonPath = Path.Combine(outputDir, baseName + ".json");
        var csvPath = Path.Combine(outputDir, baseName + ".csv");

        await File.WriteAllTextAsync(jsonPath, json, new UTF8Encoding(false)).ConfigureAwait(false);
        await File.WriteAllTextAsync(csvPath, csv, new UTF8Encoding(false)).ConfigureAwait(false);

        return (jsonPath, csvPath, outputDir, baseName);
    }

    private static async Task<string> WriteResultFileAsync(
        string outputDir,
        string baseName,
        string accountName,
        string sourceRegion,
        string targetRegion,
        string collectionNameText,
        DateTimeOffset errorStartUtc,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        int lookbackMinutes,
        MetricResultProcessor.MinimumNonZeroResult result)
    {
        var resultPath = Path.Combine(outputDir, baseName + "_result.txt");

        var sb = new StringBuilder();
        sb.AppendLine("Earliest Unacknowledged Timestamp Metric Fetcher - minimum non-zero result");
        sb.AppendLine("======================================================");
        sb.AppendLine($"Metric:              {MetricName}");
        sb.AppendLine($"Account:             {accountName}");
        sb.AppendLine($"Source region:       {sourceRegion}");
        sb.AppendLine($"Target region:       {targetRegion}");
        sb.AppendLine($"Collection name:     {collectionNameText}");
        sb.AppendLine($"Aggregation:         {Aggregation}");
        sb.AppendLine($"Error start time:    {Iso(errorStartUtc)} (UTC)");
        sb.AppendLine($"Lookback minutes:    {lookbackMinutes}");
        sb.AppendLine($"Window ({lookbackMinutes} min before): {Iso(windowStartUtc)} -> {Iso(windowEndUtc)} (UTC)");
        sb.AppendLine($"Total data points:   {result.TotalPoints}");
        sb.AppendLine($"Non-zero points:     {result.NonZeroPoints}");
        sb.AppendLine();
        if (result.Found)
        {
            sb.AppendLine($"Minimum non-zero value: {Num(result.Value)}");
            sb.AppendLine($"Emitted at (UTC):       {result.TimestampUtc}");
        }
        else
        {
            sb.AppendLine("Minimum non-zero value: 0");
            sb.AppendLine("Emitted at (UTC):       N/A (all values in the window were zero or no data)");
            sb.AppendLine();
            sb.AppendLine(
                $"The earliest unacknowledged timestamp in the {lookbackMinutes}-minute period before " +
                $"the error start time is zero. Consider using the timestamp of {lookbackMinutes} minutes " +
                $"before error start time ({Iso(windowStartUtc)} UTC) for replaying the operations before " +
                "failing over to a new region.");
        }

        await File.WriteAllTextAsync(resultPath, sb.ToString(), new UTF8Encoding(false))
            .ConfigureAwait(false);
        return resultPath;
    }

    /// <summary>
    /// Builds an OData equality clause for a metric dimension, escaping single quotes.
    /// </summary>
    private static string FilterClause(string dimension, string value) =>
        $"{dimension} eq '{value.Replace("'", "''")}'";

    private static string Num(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static void PrintBanner()
    {
        Console.WriteLine("==================================================================");
        Console.WriteLine(" Earliest Unacknowledged Timestamp Metric Fetcher");
        Console.WriteLine($" Metric: {MetricName}");
        Console.WriteLine("==================================================================");
        Console.WriteLine();
    }

    /// <summary>
    /// Explains, in normal (start/end) mode, what the tool does.
    /// </summary>
    private static void PrintNormalModeExplanation(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        Console.WriteLine("------------------------------------------------------------------");
        Console.WriteLine("Mode: time-range (start/end)");
        Console.WriteLine($"Time range (UTC):  {Iso(startUtc)} -> {Iso(endUtc)}");
        Console.WriteLine();
        Console.WriteLine("What this tool does:");
        Console.WriteLine($"  * It queries the metric '{MetricName}' for the");
        Console.WriteLine("    specified time range, source region, target region, and");
        Console.WriteLine("    collection name, using the");
        Console.WriteLine($"    '{Aggregation}' aggregation.");
        Console.WriteLine("  * It saves the complete result to a JSON file (raw API response)");
        Console.WriteLine("    and a CSV file (one row per data point: timestamp, source region,");
        Console.WriteLine("    target region, collection name, value).");
        Console.WriteLine("  * No further calculation is performed in this mode. To find the");
        Console.WriteLine("    minimum non-zero value in a period before an error, use the");
        Console.WriteLine("    --error-start-time option instead.");
        Console.WriteLine("------------------------------------------------------------------");
        Console.WriteLine();
    }

    /// <summary>
    /// Explains, in error-start-time mode, what the tool does so the user understands the
    /// internal calculation behind the reported value.
    /// </summary>
    private static void PrintErrorModeExplanation(
        DateTimeOffset errorStartUtc,
        DateTimeOffset windowStartUtc,
        int lookbackMinutes)
    {
        Console.WriteLine("------------------------------------------------------------------");
        Console.WriteLine("Mode: error-start-time");
        Console.WriteLine($"Error start time (UTC):        {Iso(errorStartUtc)} (from command line)");
        Console.WriteLine($"Lookback minutes:              {lookbackMinutes} (from command line)");
        Console.WriteLine($"Window start (UTC):            {Iso(windowStartUtc)}");
        Console.WriteLine();
        Console.WriteLine("What this tool does:");
        Console.WriteLine($"  * It queries the metric '{MetricName}' for the");
        Console.WriteLine($"    {lookbackMinutes}-MINUTE period immediately before the error start time,");
        Console.WriteLine($"    i.e. the window");
        Console.WriteLine($"    [{Iso(windowStartUtc)} -> {Iso(errorStartUtc)}] (UTC), using the");
        Console.WriteLine($"    '{Aggregation}' aggregation for the source region, target region,");
        Console.WriteLine("    and collection name.");
        Console.WriteLine("  * It then scans every data point in that period and finds the");
        Console.WriteLine("    SMALLEST value that is NOT zero, along with the timestamp it was");
        Console.WriteLine("    emitted. That value is the reported result.");
        Console.WriteLine("  * If every value in the period is zero (or there is no data), the");
        Console.WriteLine("    result is reported as zero.");
        Console.WriteLine("  * The full metric data is also saved to JSON and CSV files.");
        Console.WriteLine("------------------------------------------------------------------");
        Console.WriteLine();
    }

    /// <summary>
    /// Message shown when the minimum non-zero value in the lookback window is zero.
    /// </summary>
    private static string ZeroResultMessage(DateTimeOffset windowStartUtc, int lookbackMinutes) =>
        $"  The earliest unacknowledged timestamp in the {lookbackMinutes}-minute period before the " +
        $"error start time is zero. Consider using the timestamp of {lookbackMinutes} minutes before " +
        $"error start time ({Iso(windowStartUtc)} UTC) for replaying the operations before failing over " +
        "to a new region.";
}
