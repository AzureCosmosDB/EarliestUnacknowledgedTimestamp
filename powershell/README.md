# Azure PowerShell sample

`Get-EarliestUnacknowledgedTimestampMetric.ps1` retrieves
`EarliestUnacknowledgedTimestampMetricView` from Azure Monitor for a Cosmos DB account, filtered
by source region, target region, and collection.

## Prerequisites

- Windows PowerShell 5.1 or PowerShell 7.
- The `Az.Accounts` and `Az.Monitor` modules.
- An Azure account with at least the **Monitoring Reader** role on the Cosmos DB account.

Install Azure PowerShell if needed:

```powershell
Install-Module Az -Scope CurrentUser
```

## Parameters

| Parameter | Alias | Description |
| --- | --- | --- |
| `-SubscriptionId` | `-Subscription`, `-s` | Azure subscription ID. |
| `-ResourceGroupName` | `-ResourceGroup`, `-g` | Resource group name. |
| `-AccountName` | `-Account`, `-a` | Cosmos DB account name. |
| `-SourceRegion` | `-Source` | Source region, for example `West US 2`. |
| `-TargetRegion` | `-Region`, `-r` | Target region, for example `West Central US`. |
| `-CollectionName` | `-Collection`, `-c` | Collection (container) name. Can be empty (`""`). |
| `-StartTime` | | Start of the UTC query range. |
| `-EndTime` | | End of the UTC query range. |
| `-ErrorStartTime` | | End of the error lookback window. |
| `-LookbackMinutes` | | Number of minutes before `ErrorStartTime` to query. |
| `-OutputPath` | | Output directory. Defaults to `output` beside the script. |

The subscription, resource group, account, source region, target region, and collection name
parameters are always required. `CollectionName` accepts an empty string (`-CollectionName ""`)
to query the series whose collection name is empty.
Timestamp parameters accept ISO 8601 values. A timestamp without an offset is interpreted as UTC.

To display built-in help:

```powershell
Get-Help .\Get-EarliestUnacknowledgedTimestampMetric.ps1 -Full
```

## Query the last 24 hours

When no time parameters are supplied, the script queries the last 24 hours.

```powershell
.\Get-EarliestUnacknowledgedTimestampMetric.ps1 `
    -SubscriptionId "<subscription-id>" `
    -ResourceGroupName "<resource-group>" `
    -AccountName "<account-name>" `
    -SourceRegion "West US 2" `
    -TargetRegion "West Central US" `
    -CollectionName "<collection-name>"
```

## Query a specific time range

```powershell
.\Get-EarliestUnacknowledgedTimestampMetric.ps1 `
    -SubscriptionId "<subscription-id>" `
    -ResourceGroupName "<resource-group>" `
    -AccountName "<account-name>" `
    -SourceRegion "West US 2" `
    -TargetRegion "West Central US" `
    -CollectionName "<collection-name>" `
    -StartTime "2026-07-19T00:00:00Z" `
    -EndTime "2026-07-20T00:00:00Z"
```

If only `StartTime` is supplied, the script uses an end time 24 hours later. If only `EndTime` is
supplied, it uses a start time 24 hours earlier.## Query a series with an empty collection name

Pass an empty string to `CollectionName` to query the series whose collection name is empty:

```powershell
.\Get-EarliestUnacknowledgedTimestampMetric.ps1 `
    -SubscriptionId "<subscription-id>" `
    -ResourceGroupName "<resource-group>" `
    -AccountName "<account-name>" `
    -SourceRegion "West US 2" `
    -TargetRegion "West Central US" `
    -CollectionName ""
```

The collection name is omitted from the output file names in this case.



## Query the period before an error

Use `ErrorStartTime` and `LookbackMinutes` together:

```powershell
.\Get-EarliestUnacknowledgedTimestampMetric.ps1 `
    -SubscriptionId "<subscription-id>" `
    -ResourceGroupName "<resource-group>" `
    -AccountName "<account-name>" `
    -SourceRegion "West US 2" `
    -TargetRegion "West Central US" `
    -CollectionName "<collection-name>" `
    -ErrorStartTime "2026-07-24T20:30:00Z" `
    -LookbackMinutes 60
```

This example queries `2026-07-24T19:30:00Z` through `2026-07-24T20:30:00Z`. The script reports
the first occurrence of the smallest non-zero value in the window. If the window contains no
non-zero values, the result is zero.

`ErrorStartTime` cannot be combined with `StartTime` or `EndTime`.

## Authentication

The script uses the current Azure PowerShell sign-in. If no active sign-in is available, it opens
the Azure sign-in flow. The requested subscription is selected for the current PowerShell process.

## Output

The script writes:

- **JSON**: the result objects returned by `Get-AzMetric`.
- **CSV**: one row per data point with `timestampUtc`, `sourceRegion`, `targetRegion`,
  `collectionName`, and `minimum` columns.
- **`_result.txt`**: the minimum non-zero result in error-start-time mode.

Numeric CSV values use culture-independent formatting.

## Troubleshooting

- **Azure PowerShell command not found**: install or update the `Az` module.
- **Sign-in failed**: run `Connect-AzAccount`, then rerun the script.
- **403 Forbidden**: grant the signed-in account at least the **Monitoring Reader** role.
- **Resource not found**: verify the subscription, resource group, and account name.
- **No data points**: verify the source region, target region, collection name, and UTC time range.

## Cmdlet reference

- [`Get-AzMetricDefinition`](https://learn.microsoft.com/powershell/module/az.monitor/get-azmetricdefinition)
- [`New-AzMetricFilter`](https://learn.microsoft.com/powershell/module/az.monitor/new-azmetricfilter)
- [`Get-AzMetric`](https://learn.microsoft.com/powershell/module/az.monitor/get-azmetric)
