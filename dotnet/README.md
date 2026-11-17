# .NET sample

This sample builds a self-contained Windows command-line tool that retrieves
`EarliestUnacknowledgedTimestampMetricView` from Azure Monitor for a Cosmos DB account. Results are
filtered by source region, target region, and collection name.

## Prerequisites

To build the tool:

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

To run the published tool:

- 64-bit Windows.
- An Azure account with at least the **Monitoring Reader** role on the Cosmos DB account.

## Build

From this directory:

```powershell
.\publish.ps1
```

The executable is written to:

```text
publish\win-x64\EarliestUnacknowledgedTimestampMetricFetcher.exe
```

The executable includes the .NET runtime.

The publish script also accepts `-Runtime` and `-Configuration`:

```powershell
.\publish.ps1 -Runtime win-x64 -Configuration Release
```

## Parameters

| Option | Alias | Description |
| --- | --- | --- |
| `--subscription <id>` | `-s` | Azure subscription ID. |
| `--resource-group <name>` | `-g` | Resource group name. |
| `--account <name>` | `-a` | Cosmos DB account name. |
| `--source-region <name>` | `--source` | **Required.** Source region dimension value, for example `West US 2`. |
| `--target-region <name>` | | **Required.** Target region dimension value, for example `West Central US`. |
| `--collection <name>` | `-c`, `--collection-name` | **Required.** Collection (container) name dimension value. Use `--collection=""` to select the series whose collection name is empty. |
| `--start <iso8601>` | | Start of the UTC query range. |
| `--end <iso8601>` | | End of the UTC query range. |
| `--error-start-time <iso8601>` | | End of the error lookback window. |
| `--lookback-minutes <n>` | | Number of minutes before the error start time to query. |
| `--help` | `-h` | Display command help. |

Long options also accept `--name=value`. Quote values that contain spaces.

## Interactive mode

`--source-region`, `--target-region`, and `--collection` must be supplied on the command line. The
subscription, resource group, and account name are prompted for when omitted:

```powershell
.\EarliestUnacknowledgedTimestampMetricFetcher.exe `
    --source-region "West US 2" `
    --target-region "West Central US" `
    --collection "<collection-name>"
```

## Query the last 24 hours

Supply the resource details and omit the time options. Press Enter at both time prompts.

```powershell
.\EarliestUnacknowledgedTimestampMetricFetcher.exe `
    -s "<subscription-id>" `
    -g "<resource-group>" `
    -a "<account-name>" `
    --source-region "West US 2" `
    --target-region "West Central US" `
    --collection "<collection-name>"
```

## Query a specific time range

```powershell
.\EarliestUnacknowledgedTimestampMetricFetcher.exe `
    -s "<subscription-id>" `
    -g "<resource-group>" `
    -a "<account-name>" `
    --source-region "West US 2" `
    --target-region "West Central US" `
    --collection "<collection-name>" `
    --start "2026-07-19T00:00:00Z" `
    --end "2026-07-20T00:00:00Z"
```

If only one time option is supplied, the tool prompts for the other. Press Enter to derive a
24-hour range from the supplied value.

## Query the period before an error

Use `--error-start-time` and `--lookback-minutes` together:

```powershell
.\EarliestUnacknowledgedTimestampMetricFetcher.exe `
    -s "<subscription-id>" `
    -g "<resource-group>" `
    -a "<account-name>" `
    --source-region "West US 2" `
    --target-region "West Central US" `
    --collection "<collection-name>" `
    --error-start-time "2026-07-24T20:30:00Z" `
    --lookback-minutes 60
```

This example queries `2026-07-24T19:30:00Z` through `2026-07-24T20:30:00Z`. The tool reports the
first occurrence of the smallest non-zero value in the window. If the window contains no non-zero
values, the result is zero.

`--error-start-time` cannot be combined with `--start` or `--end`.

## Query an empty collection name

```powershell
.\EarliestUnacknowledgedTimestampMetricFetcher.exe `
    -a "<account-name>" `
    --source-region "West US 2" `
    --target-region "West Central US" `
    --collection=""
```

## Authentication

The tool opens a browser for Azure sign-in. The signed-in account must have permission to read
metrics from the Cosmos DB account.

## Output

Files are written to an `output` directory beside the executable:

- **JSON**: the raw Azure Monitor REST response.
- **CSV**: one row per data point with `timestampUtc`, `sourceRegion`, `targetRegion`,
  `collectionName`, and `minimum` columns.
- **`_result.txt`**: the minimum non-zero result in error-start-time mode.

## Troubleshooting

- **Sign-in failed or was canceled**: run the tool again and complete browser authentication.
- **403 Forbidden**: grant the signed-in account at least the **Monitoring Reader** role.
- **404 Not Found**: verify the subscription, resource group, and account name.
- **No data points**: verify the source region, target region, collection name, and UTC time range.
