# Earliest Unacknowledged Timestamp Metric Fetcher

This repository contains two examples that demonstrate how to retrieve the Azure Cosmos DB metric
`EarliestUnacknowledgedTimestampMetricView` for a target region.

## Examples

| Example | Location | Description |
| --- | --- | --- |
| Azure PowerShell | [`powershell/`](powershell/) | Run the query with Azure PowerShell and `Get-AzMetric`. |
| .NET | [`dotnet/`](dotnet/) | Build a self-contained Windows command-line tool. |

Both examples:

- Discover the metric's source region, target region, and collection name dimensions.
- Filter the metric by source region, target region, and collection name.
- Query one-minute data points using the `Minimum` aggregation.
- Support a UTC time range or a lookback window before an error.
- Write metric data to JSON and CSV.
- Report the minimum non-zero value in error-start-time mode.

## Prerequisites

- An Azure account with at least the **Monitoring Reader** role on the Cosmos DB account.
- The subscription ID, resource group name, Cosmos DB account name, source region, target region,
  and collection name.
- Azure PowerShell for the PowerShell example, or the .NET 8 SDK to build the .NET example.

Use ISO 8601 timestamps such as `2026-07-19T00:00:00Z`. All output timestamps are UTC.

## PowerShell quick start

```powershell
Install-Module Az -Scope CurrentUser

.\powershell\Get-EarliestUnacknowledgedTimestampMetric.ps1 `
    -SubscriptionId "<subscription-id>" `
    -ResourceGroupName "<resource-group>" `
    -AccountName "<account-name>" `
    -SourceRegion "West US 2" `
    -TargetRegion "West Central US" `
    -CollectionName "<collection-name>"
```

See the [PowerShell README](powershell/README.md) for all parameters and examples.

## .NET quick start

```powershell
cd .\dotnet
.\publish.ps1
.\publish\win-x64\EarliestUnacknowledgedTimestampMetricFetcher.exe `
    -s "<subscription-id>" `
    -g "<resource-group>" `
    -a "<account-name>" `
    --source-region "West US 2" `
    --target-region "West Central US" `
    --collection "<collection-name>"
```

See the [.NET README](dotnet/README.md) for build and command-line instructions.

## Output

Both examples create:

- A JSON file containing the Azure Monitor result.
- A CSV file with `timestampUtc`, `sourceRegion`, `targetRegion`, `collectionName`, and `minimum`
  columns.
- A `_result.txt` file in error-start-time mode.

The .NET example stores the raw Azure Monitor REST response in JSON. The PowerShell example stores
the objects returned by `Get-AzMetric`. Use the CSV or `_result.txt` file when comparing results.

Generated files can contain Azure resource and metric data. Output directories are excluded by
`.gitignore`.
