<#
.SYNOPSIS
    Builds samples/SampleUnityCode and asserts it produces exactly the expected OPL001 warnings.

.DESCRIPTION
    The sample is the end-to-end check that the analyzer loads from a ProjectReference and reports
    through a real compilation, not just through the test harness. This script rebuilds it, collects
    every distinct OPL001 warning from the build output, and compares them against the list below.

    A diagnostic is identified by the allocated expression and the enclosing method, as they appear in
    the message, rather than by line and column, so reformatting the sample does not break the check.
    The comparison is exact: a missing warning (regression) and an unexpected one (false positive, for
    example in Start or in a class that is not a MonoBehaviour) both fail.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER NoDependencies
    Rebuild only the sample and reuse the analyzer assemblies already built. CI passes this after the
    solution build; locally, leave it off so the analyzer is built first.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoDependencies
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# One entry per OPL001 warning in samples/SampleUnityCode/SampleBehaviour.cs: '<allocation> in <method>'.
$expected = @(
    'System.Collections.Generic.List<int> in Update'
    'List<string> in Update'
    'Instantiate in Update'
    'int[10] in FixedUpdate'
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$sampleProject = Join-Path $repoRoot 'samples/SampleUnityCode/SampleUnityCode.csproj'

# --no-incremental forces the compiler to run, so warnings are reported even when the sample is
# already up to date. -warnaserror matches CI; the sample exempts OPL001 in its project file.
$buildArgs = @('build', $sampleProject, '-c', $Configuration, '--nologo', '--no-incremental', '-warnaserror', '-clp:NoSummary')
if ($NoDependencies) { $buildArgs += '--no-dependencies' }

Write-Host "dotnet $($buildArgs -join ' ')"
$output = & dotnet @buildArgs 2>&1 | ForEach-Object { "$_" }
$exitCode = $LASTEXITCODE
$output | Write-Host
if ($exitCode -ne 0) { throw "Sample build failed with exit code $exitCode." }

# MSBuild can echo a warning more than once; key on file(line,col) to count each diagnostic once.
$pattern = '^(?<location>.+?\(\d+,\d+\)): warning OPL001: ''(?<allocation>.+?)'' is allocated inside the frequently-called method ''(?<method>.+?)''\.'
$byLocation = [ordered]@{}
foreach ($line in $output) {
    $match = [regex]::Match($line, $pattern)
    if ($match.Success) {
        $byLocation[$match.Groups['location'].Value.Trim()] = "$($match.Groups['allocation'].Value) in $($match.Groups['method'].Value)"
    }
}
$actual = @($byLocation.Values)

$missing = @($expected | Where-Object { $actual -notcontains $_ })
$unexpected = @($actual | Where-Object { $expected -notcontains $_ })

if ($missing.Count -gt 0 -or $unexpected.Count -gt 0 -or $actual.Count -ne $expected.Count) {
    $message = "Sample OPL001 warnings do not match. Expected $($expected.Count), found $($actual.Count)."
    if ($missing.Count -gt 0) { $message += "`n  Missing:`n    " + ($missing -join "`n    ") }
    if ($unexpected.Count -gt 0) { $message += "`n  Unexpected:`n    " + ($unexpected -join "`n    ") }
    throw $message
}

Write-Host "Sample produced the $($expected.Count) expected OPL001 warnings:"
$expected | ForEach-Object { Write-Host "  $_" }
