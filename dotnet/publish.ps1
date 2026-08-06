#Requires -Version 5.1

<#
.SYNOPSIS
    Publishes EarliestUnacknowledgedTimestampMetricFetcher as a self-contained, single-file win-x64 executable.

.DESCRIPTION
    Produces one portable .exe that the customer can run without installing the .NET runtime.
    Output is written to .\publish\win-x64\EarliestUnacknowledgedTimestampMetricFetcher.exe

.EXAMPLE
    .\publish.ps1
#>

[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Runtime = 'win-x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'src/EarliestUnacknowledgedTimestampMetricFetcher/EarliestUnacknowledgedTimestampMetricFetcher.csproj'
$outputPath = Join-Path (Join-Path $PSScriptRoot 'publish') $Runtime
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue

if ($null -eq $dotnet) {
    throw 'The .NET SDK is required. Install .NET 8 or later from https://dotnet.microsoft.com/download.'
}

Write-Host "Publishing $projectPath for $Runtime ($Configuration)..." -ForegroundColor Cyan

& $dotnet.Source publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Published to: $outputPath" -ForegroundColor Green
Write-Host "Executable:   $(Join-Path $outputPath 'EarliestUnacknowledgedTimestampMetricFetcher.exe')" -ForegroundColor Green
