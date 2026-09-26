<#
.SYNOPSIS
    Fails when line coverage of the shipped assemblies drops below a floor.

.DESCRIPTION
    Reads the Cobertura report that `dotnet test --collect:"XPlat Code Coverage"` writes (through
    coverlet.collector, with tests/ObjectPoolLinter.Tests/coverage.runsettings limiting it to the
    ObjectPoolLinter and ObjectPoolLinter.CodeFixes assemblies), prints line and branch coverage for
    each assembly and in total, and exits non-zero when total line coverage is below -Minimum.

    On GitHub Actions the same table is appended to the job summary.

.PARAMETER ResultsDirectory
    The --results-directory passed to dotnet test. The newest coverage.cobertura.xml under it is used.

.PARAMETER Minimum
    Minimum total line coverage, in percent. Defaults to 90.
#>
[CmdletBinding()]
param(
    [string]$ResultsDirectory = 'artifacts/coverage',
    [double]$Minimum = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$report = Get-ChildItem -Path $ResultsDirectory -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $report) {
    throw "No coverage.cobertura.xml under '$ResultsDirectory'. Run dotnet test with --collect:""XPlat Code Coverage""."
}

[xml]$xml = Get-Content -LiteralPath $report.FullName -Raw
$coverage = $xml.coverage

function Format-Percent([string]$rate) {
    '{0:N1}%' -f ([double]::Parse($rate, [Globalization.CultureInfo]::InvariantCulture) * 100)
}

$lines = @(
    '| Assembly | Line | Branch |'
    '| --- | ---: | ---: |'
)
foreach ($package in $coverage.packages.package) {
    $lines += "| $($package.name) | $(Format-Percent $package.'line-rate') | $(Format-Percent $package.'branch-rate') |"
}
$lines += "| **Total** | **$(Format-Percent $coverage.'line-rate')** | **$(Format-Percent $coverage.'branch-rate')** |"

$lineRate = [double]::Parse($coverage.'line-rate', [Globalization.CultureInfo]::InvariantCulture) * 100
$verdict = if ($lineRate -ge $Minimum) { 'passes' } else { 'fails' }
$summary = "Line coverage $('{0:N1}' -f $lineRate)% $verdict the $Minimum% floor ($($coverage.'lines-covered') of $($coverage.'lines-valid') lines)."

$lines | ForEach-Object { Write-Host $_ }
Write-Host $summary

if ($env:GITHUB_STEP_SUMMARY) {
    @('## Code coverage', '') + $lines + @('', $summary) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}

if ($lineRate -lt $Minimum) {
    Write-Error $summary
    exit 1
}
