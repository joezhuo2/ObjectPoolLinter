<#
.SYNOPSIS
    Fails when any project in the solution depends on a package with a known vulnerability.

.DESCRIPTION
    Runs `dotnet list package --vulnerable --include-transitive --format json` over the solution, which
    checks every resolved package, direct and transitive, against the vulnerability data of the
    configured NuGet sources (the GitHub Advisory Database, through nuget.org). `dotnet list package`
    exits 0 whether or not it finds anything, so this script reads the JSON and exits non-zero itself.

    NuGet audit (Directory.Build.props) already fails a CI restore on the same advisories. This script
    is the second, explicit check: it lists each vulnerable package with the project that pulls it in,
    its severity and advisory link, and on GitHub Actions appends that table to the job summary.

    The solution must be restored first.

.PARAMETER Solution
    The solution or project to check. Defaults to ObjectPoolLinter.slnx.
#>
[CmdletBinding()]
param(
    [string]$Solution = 'ObjectPoolLinter.slnx'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$output = & dotnet list $Solution package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) {
    $output | ForEach-Object { Write-Host $_ }
    throw "dotnet list package failed with exit code $LASTEXITCODE. Restore the solution first."
}
$report = ($output -join "`n") | ConvertFrom-Json

function Get-Items($object, [string]$name) {
    $property = $object.PSObject.Properties[$name]
    if ($property -and $property.Value) { @($property.Value) } else { @() }
}

$findings = @()
foreach ($project in Get-Items $report 'projects') {
    $projectName = [IO.Path]::GetFileNameWithoutExtension($project.path)
    foreach ($framework in Get-Items $project 'frameworks') {
        foreach ($kind in 'topLevelPackages', 'transitivePackages') {
            foreach ($package in Get-Items $framework $kind) {
                foreach ($vulnerability in Get-Items $package 'vulnerabilities') {
                    $findings += [pscustomobject]@{
                        Project   = $projectName
                        Framework = $framework.framework
                        Package   = "$($package.id) $($package.resolvedVersion)"
                        Direct    = $kind -eq 'topLevelPackages'
                        Severity  = $vulnerability.severity
                        Advisory  = $vulnerability.advisoryurl
                    }
                }
            }
        }
    }
}

foreach ($problem in Get-Items $report 'problems') {
    Write-Warning $problem.text
}

$findings = @($findings | Sort-Object Package, Advisory, Project -Unique)
if ($findings.Count -eq 0) {
    $summary = 'No known vulnerabilities in any direct or transitive package.'
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) {
        @('## Vulnerable packages', '', $summary) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
    exit 0
}

$lines = @(
    '| Package | Project | Direct | Severity | Advisory |'
    '| --- | --- | --- | --- | --- |'
)
foreach ($finding in $findings) {
    $direct = if ($finding.Direct) { 'yes' } else { 'transitive' }
    $lines += "| $($finding.Package) | $($finding.Project) ($($finding.Framework)) | $direct | $($finding.Severity) | $($finding.Advisory) |"
}
$summary = "$($findings.Count) known vulnerabilit$(if ($findings.Count -eq 1) { 'y' } else { 'ies' }) in the solution's packages."

$lines | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) {
    @('## Vulnerable packages', '') + $lines + @('', $summary) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}

Write-Error $summary
exit 1
