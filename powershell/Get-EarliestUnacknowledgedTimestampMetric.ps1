#Requires -Version 5.1

<#
.SYNOPSIS
    Retrieves the EarliestUnacknowledgedTimestampMetricView metric for an Azure Cosmos DB account.

.DESCRIPTION
    Uses Azure PowerShell to discover the metric's dimensions, query one-minute minimum values,
    and write JSON and CSV output. Results are filtered by source region, target region, and
    collection name. Error-start-time mode also reports the minimum non-zero value in a lookback
    window.

.PARAMETER SubscriptionId
    Azure subscription that contains the Cosmos DB account.

.PARAMETER ResourceGroupName
    Resource group that contains the Cosmos DB account.

.PARAMETER AccountName
    Name of the Cosmos DB account.

.PARAMETER SourceRegion
    Source region dimension value, for example "West US 2".

.PARAMETER TargetRegion
    Target region dimension value, for example "West Central US".

.PARAMETER CollectionName
    Collection (container) name dimension value. Pass an empty string ("") to query the series
    whose collection name is empty.

.PARAMETER StartTime
    Start of the query range. Values without an offset are interpreted as UTC.

.PARAMETER EndTime
    End of the query range. Values without an offset are interpreted as UTC.

.PARAMETER ErrorStartTime
    End of the error lookback window. Values without an offset are interpreted as UTC.

.PARAMETER LookbackMinutes
    Positive number of minutes immediately before ErrorStartTime to query.

.PARAMETER OutputPath
    Directory for JSON, CSV, and result files. Defaults to an output directory beside this script.

.EXAMPLE
    .\Get-EarliestUnacknowledgedTimestampMetric.ps1 -SubscriptionId <id> `
        -ResourceGroupName <group> -AccountName <account> -SourceRegion "West US 2" `
        -TargetRegion "West Central US" -CollectionName orders

.EXAMPLE
    .\Get-EarliestUnacknowledgedTimestampMetric.ps1 -SubscriptionId <id> `
        -ResourceGroupName <group> -AccountName <account> -SourceRegion "West US 2" `
        -TargetRegion "West Central US" -CollectionName orders `
        -ErrorStartTime "2026-07-24T20:30:00Z" -LookbackMinutes 60

.EXAMPLE
    .\Get-EarliestUnacknowledgedTimestampMetric.ps1 -SubscriptionId <id> `
        -ResourceGroupName <group> -AccountName <account> -SourceRegion "West US 2" `
        -TargetRegion "West Central US" -CollectionName ""

    Queries the series whose collection name is empty.

.LINK
    https://learn.microsoft.com/powershell/module/az.monitor/get-azmetric
#>

[CmdletBinding(DefaultParameterSetName = 'TimeRange')]
param(
    [Parameter(Mandatory = $true)]
    [Alias('Subscription', 's')]
    [ValidateNotNullOrEmpty()]
    [string]$SubscriptionId,

    [Parameter(Mandatory = $true)]
    [Alias('ResourceGroup', 'g')]
    [ValidateNotNullOrEmpty()]
    [string]$ResourceGroupName,

    [Parameter(Mandatory = $true)]
    [Alias('Account', 'a')]
    [ValidateNotNullOrEmpty()]
    [string]$AccountName,

    [Parameter(Mandatory = $true)]
    [Alias('Source')]
    [ValidateNotNullOrEmpty()]
    [string]$SourceRegion,

    [Parameter(Mandatory = $true)]
    [Alias('Region', 'r')]
    [ValidateNotNullOrEmpty()]
    [string]$TargetRegion,

    [Parameter(Mandatory = $true)]
    [Alias('Collection', 'c')]
    [AllowEmptyString()]
    [string]$CollectionName,

    [Parameter(ParameterSetName = 'TimeRange')]
    [ValidateNotNullOrEmpty()]
    [string]$StartTime,

    [Parameter(ParameterSetName = 'TimeRange')]
    [ValidateNotNullOrEmpty()]
    [string]$EndTime,

    [Parameter(Mandatory = $true, ParameterSetName = 'ErrorWindow')]
    [ValidateNotNullOrEmpty()]
    [string]$ErrorStartTime,

    [Parameter(Mandatory = $true, ParameterSetName = 'ErrorWindow')]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$LookbackMinutes,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath = (Join-Path $PSScriptRoot 'output')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$metricName = 'EarliestUnacknowledgedTimestampMetricView'
$metricNamespace = 'Microsoft.DocumentDB/databaseAccounts'
$aggregation = 'Minimum'
$timeGrain = [TimeSpan]::FromMinutes(1)

function Get-ObjectPropertyValue {
    param(
        [AllowNull()]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $property = $InputObject.PSObject.Properties |
        Where-Object { $_.Name -ieq $Name } |
        Select-Object -First 1

    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-LocalizableStringValue {
    param(
        [AllowNull()]
        [object]$InputObject
    )

    if ($null -eq $InputObject) {
        return $null
    }

    if ($InputObject -is [string]) {
        return $InputObject
    }

    $value = Get-ObjectPropertyValue -InputObject $InputObject -Name 'Value'
    if ($null -ne $value) {
        return [string]$value
    }

    return [string]$InputObject
}

function Get-MetricDimensionNames {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Definitions,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMetricName
    )

    $definition = $Definitions |
        Where-Object {
            $name = Get-LocalizableStringValue (
                Get-ObjectPropertyValue -InputObject $_ -Name 'Name'
            )
            $name -ieq $ExpectedMetricName
        } |
        Select-Object -First 1

    if ($null -eq $definition) {
        throw "Metric '$ExpectedMetricName' is not available for this resource."
    }

    $dimensions = Get-ObjectPropertyValue -InputObject $definition -Name 'Dimensions'
    return , @(
        foreach ($dimension in @($dimensions)) {
            $name = Get-LocalizableStringValue -InputObject $dimension
            if (-not [string]::IsNullOrWhiteSpace($name)) {
                $name
            }
        }
    )
}

function Find-DimensionName {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$DimensionNames,

        [Parameter(Mandatory = $true)]
        [string[]]$PreferredNames,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [string]$ExcludePattern
    )

    foreach ($preferred in $PreferredNames) {
        $match = $DimensionNames | Where-Object { $_ -ieq $preferred } | Select-Object -First 1
        if ($null -ne $match) {
            return $match
        }
    }

    return $DimensionNames |
        Where-Object {
            $_ -match $Pattern -and
            ([string]::IsNullOrEmpty($ExcludePattern) -or $_ -notmatch $ExcludePattern)
        } |
        Select-Object -First 1
}

function Format-DimensionList {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$DimensionNames
    )

    if ($DimensionNames.Count -eq 0) {
        return 'none'
    }

    return $DimensionNames -join ', '
}

function Get-SeriesDimensionValue {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Series,

        [Parameter(Mandatory = $true)]
        [string]$DimensionName,

        [AllowEmptyString()]
        [string]$FallbackValue = ''
    )

    $metadataValues = Get-ObjectPropertyValue -InputObject $Series -Name 'Metadatavalues'
    foreach ($metadata in @($metadataValues)) {
        $name = Get-LocalizableStringValue (
            Get-ObjectPropertyValue -InputObject $metadata -Name 'Name'
        )

        if ($name -ieq $DimensionName) {
            $value = Get-ObjectPropertyValue -InputObject $metadata -Name 'Value'
            if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
                return [string]$value
            }
        }
    }

    return $FallbackValue
}

function Format-UtcTimestamp {
    param(
        [Parameter(Mandatory = $true)]
        [DateTimeOffset]$Value
    )

    return $Value.ToUniversalTime().ToString(
        'yyyy-MM-ddTHH:mm:ssZ',
        [Globalization.CultureInfo]::InvariantCulture
    )
}

function ConvertTo-UtcTimestamp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$ParameterName
    )

    $styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
        [Globalization.DateTimeStyles]::AdjustToUniversal
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture,
            $styles,
            [ref]$parsed
        )) {
        throw "Could not parse -$ParameterName value '$Value'. Use ISO 8601, for example 2026-07-19T00:00:00Z."
    }

    return $parsed.ToUniversalTime()
}

function ConvertTo-SafeFileNamePart {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    return $Value -replace '[^A-Za-z0-9._-]', '_'
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Content
    )

    $utf8WithoutBom = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($Path, $Content, $utf8WithoutBom)
}

$requiredCommands = @(
    'Connect-AzAccount',
    'Get-AzContext',
    'Set-AzContext',
    'Get-AzMetricDefinition',
    'New-AzMetricFilter',
    'Get-AzMetric'
)

$missingCommands = @(
    $requiredCommands |
        Where-Object { $null -eq (Get-Command -Name $_ -ErrorAction SilentlyContinue) }
)

if ($missingCommands.Count -gt 0) {
    throw "Required Azure PowerShell commands are unavailable: $($missingCommands -join ', '). Install them with 'Install-Module Az -Scope CurrentUser'."
}

if ($PSCmdlet.ParameterSetName -eq 'ErrorWindow') {
    $endUtc = ConvertTo-UtcTimestamp `
        -Value $ErrorStartTime `
        -ParameterName 'ErrorStartTime'
    $startUtc = $endUtc.AddMinutes(-$LookbackMinutes)
    $errorMode = $true
}
else {
    $hasStart = $PSBoundParameters.ContainsKey('StartTime')
    $hasEnd = $PSBoundParameters.ContainsKey('EndTime')

    if (-not $hasStart -and -not $hasEnd) {
        $endUtc = [DateTimeOffset]::UtcNow
        $startUtc = $endUtc.AddHours(-24)
    }
    elseif ($hasStart -and $hasEnd) {
        $startUtc = ConvertTo-UtcTimestamp -Value $StartTime -ParameterName 'StartTime'
        $endUtc = ConvertTo-UtcTimestamp -Value $EndTime -ParameterName 'EndTime'
    }
    elseif ($hasStart) {
        $startUtc = ConvertTo-UtcTimestamp -Value $StartTime -ParameterName 'StartTime'
        $endUtc = $startUtc.AddHours(24)
    }
    else {
        $endUtc = ConvertTo-UtcTimestamp -Value $EndTime -ParameterName 'EndTime'
        $startUtc = $endUtc.AddHours(-24)
    }

    if ($endUtc -le $startUtc) {
        throw 'EndTime must be later than StartTime.'
    }

    $errorMode = $false
}

$resourceId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.DocumentDB/databaseAccounts/$AccountName"

$context = Get-AzContext -ErrorAction SilentlyContinue
$contextAccount = Get-ObjectPropertyValue -InputObject $context -Name 'Account'
if ($null -eq $context -or $null -eq $contextAccount) {
    Write-Host 'No active Azure context was found. Sign in to Azure...'
    $null = Connect-AzAccount -Scope Process
}

$azureContext = Set-AzContext -SubscriptionId $SubscriptionId -Scope Process

Write-Host "Retrieving the '$metricName' metric definition..."
$definitions = @(
    Get-AzMetricDefinition `
        -ResourceId $resourceId `
        -MetricName $metricName `
        -MetricNamespace $metricNamespace `
        -DefaultProfile $azureContext
)

$dimensionNames = Get-MetricDimensionNames `
    -Definitions $definitions `
    -ExpectedMetricName $metricName
$availableDimensions = Format-DimensionList -DimensionNames $dimensionNames

$sourceRegionDimension = Find-DimensionName `
    -DimensionNames $dimensionNames `
    -PreferredNames @('SourceRegion') `
    -Pattern 'source'
if ($null -eq $sourceRegionDimension) {
    throw "Could not identify a source region dimension for metric '$metricName'. Available dimensions: $availableDimensions."
}
Write-Host "Using source region dimension: $sourceRegionDimension"

$regionDimension = Find-DimensionName `
    -DimensionNames $dimensionNames `
    -PreferredNames @('TargetRegion', 'Region') `
    -Pattern 'region' `
    -ExcludePattern 'source'
if ($null -eq $regionDimension) {
    throw "Could not identify a target region dimension for metric '$metricName'. Available dimensions: $availableDimensions."
}
Write-Host "Using target region dimension: $regionDimension"

$collectionDimension = Find-DimensionName `
    -DimensionNames $dimensionNames `
    -PreferredNames @('CollectionName') `
    -Pattern 'collection|container'
if ($null -eq $collectionDimension) {
    throw "Could not identify a collection name dimension for metric '$metricName'. Available dimensions: $availableDimensions."
}
Write-Host "Using collection name dimension: $collectionDimension"

$isCollectionNameEmpty = [string]::IsNullOrEmpty($CollectionName)

# New-AzMetricFilter rejects empty values, so the empty-value clause is built directly.
$collectionFilter = if ($isCollectionNameEmpty) {
    "$collectionDimension eq ''"
}
else {
    "$(New-AzMetricFilter -Dimension $collectionDimension -Operator eq -Value $CollectionName)"
}

$metricFilter = @(
    "$(New-AzMetricFilter -Dimension $sourceRegionDimension -Operator eq -Value $SourceRegion)"
    "$(New-AzMetricFilter -Dimension $regionDimension -Operator eq -Value $TargetRegion)"
    $collectionFilter
) -join ' and '

$collectionNameText = if ($isCollectionNameEmpty) { '(empty)' } else { $CollectionName }
$queryDescription = "source region '$SourceRegion', target region '$TargetRegion', collection '$collectionNameText'"

Write-Host "Using time grain (interval): $timeGrain"
Write-Host "Querying metric values for $queryDescription..."
$metrics = @(
    Get-AzMetric `
        -ResourceId $resourceId `
        -MetricName $metricName `
        -MetricNamespace $metricNamespace `
        -AggregationType $aggregation `
        -TimeGrain $timeGrain `
        -StartTime $startUtc.UtcDateTime `
        -EndTime $endUtc.UtcDateTime `
        -MetricFilter $metricFilter `
        -DefaultProfile $azureContext
)

$rows = @(
    foreach ($metric in $metrics) {
        $timeSeries = Get-ObjectPropertyValue -InputObject $metric -Name 'Timeseries'
        foreach ($series in @($timeSeries)) {
            $seriesSourceRegion = Get-SeriesDimensionValue `
                -Series $series `
                -DimensionName $sourceRegionDimension `
                -FallbackValue $SourceRegion
            $seriesRegion = Get-SeriesDimensionValue `
                -Series $series `
                -DimensionName $regionDimension `
                -FallbackValue $TargetRegion
            $seriesCollectionName = Get-SeriesDimensionValue `
                -Series $series `
                -DimensionName $collectionDimension `
                -FallbackValue $CollectionName
            $dataPoints = Get-ObjectPropertyValue -InputObject $series -Name 'Data'

            foreach ($point in @($dataPoints)) {
                $timestamp = Get-ObjectPropertyValue -InputObject $point -Name 'TimeStamp'
                $minimum = Get-ObjectPropertyValue -InputObject $point -Name 'Minimum'

                [PSCustomObject][ordered]@{
                    timestampUtc = if ($null -eq $timestamp) {
                        ''
                    }
                    else {
                        Format-UtcTimestamp -Value ([DateTimeOffset]$timestamp)
                    }
                    sourceRegion = $seriesSourceRegion
                    targetRegion = $seriesRegion
                    collectionName = $seriesCollectionName
                    minimum = if ($null -eq $minimum) { $null } else { [double]$minimum }
                }
            }
        }
    }
)

$resolvedOutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
    $OutputPath
)
$null = New-Item -ItemType Directory -Path $resolvedOutputPath -Force

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss')
$baseNameParts = @(
    ConvertTo-SafeFileNamePart -Value $AccountName
    ConvertTo-SafeFileNamePart -Value $SourceRegion
    ConvertTo-SafeFileNamePart -Value $TargetRegion
    if (-not $isCollectionNameEmpty) { ConvertTo-SafeFileNamePart -Value $CollectionName }
    $stamp
)
$baseName = $baseNameParts -join '_'
$jsonPath = Join-Path $resolvedOutputPath "$baseName.json"
$csvPath = Join-Path $resolvedOutputPath "$baseName.csv"

$json = ConvertTo-Json -InputObject $metrics -Depth 100
Write-Utf8File -Path $jsonPath -Content $json

$csvRows = @(
    foreach ($row in $rows) {
        [PSCustomObject][ordered]@{
            timestampUtc = $row.timestampUtc
            sourceRegion = $row.sourceRegion
            targetRegion = $row.targetRegion
            collectionName = $row.collectionName
            minimum = if ($null -eq $row.minimum) {
                ''
            }
            else {
                ([double]$row.minimum).ToString(
                    'R',
                    [Globalization.CultureInfo]::InvariantCulture
                )
            }
        }
    }
)

if ($csvRows.Count -gt 0) {
    $csvLines = @($csvRows | ConvertTo-Csv -NoTypeInformation)
}
else {
    $csvLines = @('"timestampUtc","sourceRegion","targetRegion","collectionName","minimum"')
}
$csv = ($csvLines -join [Environment]::NewLine) + [Environment]::NewLine
Write-Utf8File -Path $csvPath -Content $csv

Write-Host ''
Write-Host 'Done.'
Write-Host "  Metric:      $metricName"
Write-Host "  Source:      $SourceRegion"
Write-Host "  Target:      $TargetRegion"
Write-Host "  Collection:  $collectionNameText"
Write-Host "  Time range:  $(Format-UtcTimestamp -Value $startUtc) -> $(Format-UtcTimestamp -Value $endUtc) (UTC)"
Write-Host "  Aggregation: $aggregation, interval $timeGrain"
Write-Host "  Data points: $($rows.Count)"
Write-Host "  JSON file:   $jsonPath"
Write-Host "  CSV file:    $csvPath"
if ($rows.Count -eq 0) {
    Write-Host ''
    Write-Host '  Note: no data points were returned for the selected dimensions and time range.'
}

if ($errorMode) {
    $nonZeroRows = @(
        $rows |
            Where-Object { $null -ne $_.minimum -and [double]$_.minimum -ne 0 }
    )
    $minimumRow = $null
    foreach ($row in $nonZeroRows) {
        if ($null -eq $minimumRow -or
            [double]$row.minimum -lt [double]$minimumRow.minimum) {
            $minimumRow = $row
        }
    }

    $resultPath = Join-Path $resolvedOutputPath "${baseName}_result.txt"
    $resultLines = @(
        'Earliest Unacknowledged Timestamp Metric Fetcher - minimum non-zero result'
        '=========================================================================='
        "Metric:              $metricName"
        "Account:             $AccountName"
        "Source region:       $SourceRegion"
        "Target region:       $TargetRegion"
        "Collection name:     $collectionNameText"
        "Aggregation:         $aggregation"
        "Error start time:    $(Format-UtcTimestamp -Value $endUtc) (UTC)"
        "Lookback minutes:    $LookbackMinutes"
        "Window:              $(Format-UtcTimestamp -Value $startUtc) -> $(Format-UtcTimestamp -Value $endUtc) (UTC)"
        "Total data points:   $($rows.Count)"
        "Non-zero points:     $($nonZeroRows.Count)"
        ''
    )

    if ($null -ne $minimumRow) {
        $minimumText = ([double]$minimumRow.minimum).ToString(
            'R',
            [Globalization.CultureInfo]::InvariantCulture
        )
        $resultLines += "Minimum non-zero value: $minimumText"
        $resultLines += "Emitted at (UTC):       $($minimumRow.timestampUtc)"

        Write-Host ''
        Write-Host "  Minimum non-zero value: $minimumText"
        Write-Host "  Emitted at (UTC):       $($minimumRow.timestampUtc)"
    }
    else {
        $windowStartText = Format-UtcTimestamp -Value $startUtc
        $resultLines += 'Minimum non-zero value: 0'
        $resultLines += 'Emitted at (UTC):       N/A (all values in the window were zero or no data)'
        $resultLines += ''
        $resultLines += "The earliest unacknowledged timestamp in the $LookbackMinutes-minute period before the error start time is zero. Consider using $windowStartText UTC for replaying operations before failing over to a new region."

        Write-Host ''
        Write-Host '  Minimum non-zero value: 0'
    }

    $resultText = ($resultLines -join [Environment]::NewLine) + [Environment]::NewLine
    Write-Utf8File -Path $resultPath -Content $resultText
    Write-Host "  Result file: $resultPath"
}
