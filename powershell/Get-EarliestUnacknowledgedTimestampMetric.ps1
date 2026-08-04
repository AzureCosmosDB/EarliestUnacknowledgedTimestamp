#Requires -Version 5.1

<#
.SYNOPSIS
    Retrieves the EarliestUnacknowledgedTimestampMetricView metric for an Azure Cosmos DB account.

.DESCRIPTION
    Uses Azure PowerShell to discover the metric's region dimension, query one-minute minimum
    values, and write JSON and CSV output. Error-start-time mode also reports the minimum non-zero
    value in a lookback window.

.PARAMETER SubscriptionId
    Azure subscription that contains the Cosmos DB account.

.PARAMETER ResourceGroupName
    Resource group that contains the Cosmos DB account.

.PARAMETER AccountName
    Name of the Cosmos DB account.

.PARAMETER TargetRegion
    Target region dimension value, for example "West Central US".

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
        -ResourceGroupName <group> -AccountName <account> -TargetRegion westus2

.EXAMPLE
    .\Get-EarliestUnacknowledgedTimestampMetric.ps1 -SubscriptionId <id> `
        -ResourceGroupName <group> -AccountName <account> -TargetRegion "West Central US" `
        -ErrorStartTime "2026-07-24T20:30:00Z" -LookbackMinutes 60

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
    [Alias('Region', 'r')]
    [ValidateNotNullOrEmpty()]
    [string]$TargetRegion,

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

function Get-RegionDimensionName {
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

    $dimensionNames = @(
        $dimensions = Get-ObjectPropertyValue -InputObject $definition -Name 'Dimensions'
        foreach ($dimension in @($dimensions)) {
            $name = Get-LocalizableStringValue -InputObject $dimension
            if (-not [string]::IsNullOrWhiteSpace($name)) {
                $name
            }
        }
    )

    $regionDimension = $dimensionNames |
        Where-Object { $_ -match 'region' } |
        Select-Object -First 1

    if ($null -ne $regionDimension) {
        return $regionDimension
    }

    if ($dimensionNames.Count -eq 1) {
        return $dimensionNames[0]
    }

    $available = if ($dimensionNames.Count -eq 0) {
        'none'
    }
    else {
        $dimensionNames -join ', '
    }

    throw "Could not identify a region dimension for metric '$ExpectedMetricName'. Available dimensions: $available."
}

function Get-SeriesRegion {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Series,

        [Parameter(Mandatory = $true)]
        [string]$RegionDimension,

        [Parameter(Mandatory = $true)]
        [string]$FallbackRegion
    )

    $metadataValues = Get-ObjectPropertyValue -InputObject $Series -Name 'Metadatavalues'
    foreach ($metadata in @($metadataValues)) {
        $name = Get-LocalizableStringValue (
            Get-ObjectPropertyValue -InputObject $metadata -Name 'Name'
        )

        if ($name -ieq $RegionDimension -or $name -match 'region') {
            $value = Get-ObjectPropertyValue -InputObject $metadata -Name 'Value'
            if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
                return [string]$value
            }
        }
    }

    return $FallbackRegion
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

$regionDimension = Get-RegionDimensionName `
    -Definitions $definitions `
    -ExpectedMetricName $metricName
Write-Host "Using region dimension: $regionDimension"

$metricFilter = New-AzMetricFilter `
    -Dimension $regionDimension `
    -Operator eq `
    -Value $TargetRegion

Write-Host "Using time grain (interval): $timeGrain"
Write-Host "Querying metric values for region '$TargetRegion'..."
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
            $seriesRegion = Get-SeriesRegion `
                -Series $series `
                -RegionDimension $regionDimension `
                -FallbackRegion $TargetRegion
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
                    targetRegion = $seriesRegion
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
$baseName = '{0}_{1}_{2}' -f (
    ConvertTo-SafeFileNamePart -Value $AccountName
), (
    ConvertTo-SafeFileNamePart -Value $TargetRegion
), $stamp
$jsonPath = Join-Path $resolvedOutputPath "$baseName.json"
$csvPath = Join-Path $resolvedOutputPath "$baseName.csv"

$json = ConvertTo-Json -InputObject $metrics -Depth 100
Write-Utf8File -Path $jsonPath -Content $json

$csvRows = @(
    foreach ($row in $rows) {
        [PSCustomObject][ordered]@{
            timestampUtc = $row.timestampUtc
            targetRegion = $row.targetRegion
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
    $csvLines = @('"timestampUtc","targetRegion","minimum"')
}
$csv = ($csvLines -join [Environment]::NewLine) + [Environment]::NewLine
Write-Utf8File -Path $csvPath -Content $csv

Write-Host ''
Write-Host 'Done.'
Write-Host "  Metric:      $metricName"
Write-Host "  Region:      $TargetRegion"
Write-Host "  Time range:  $(Format-UtcTimestamp -Value $startUtc) -> $(Format-UtcTimestamp -Value $endUtc) (UTC)"
Write-Host "  Aggregation: $aggregation, interval $timeGrain"
Write-Host "  Data points: $($rows.Count)"
Write-Host "  JSON file:   $jsonPath"
Write-Host "  CSV file:    $csvPath"
if ($rows.Count -eq 0) {
    Write-Host ''
    Write-Host '  Note: no data points were returned for this region and time range.'
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
        "Target region:       $TargetRegion"
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
