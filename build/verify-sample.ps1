<#
.SYNOPSIS
    Builds samples/SampleUnityCode and asserts it produces exactly the expected OPL warnings.

.DESCRIPTION
    The sample is the end-to-end check that the analyzers load from a ProjectReference and report
    through a real compilation, not just through the test harness. This script rebuilds it, collects
    every distinct OPL warning from the build output, and compares them against the list below.

    A diagnostic is identified by its rule ID, the allocation and the enclosing method, as they appear
    in the message, rather than by line and column, so reformatting the sample does not break the check.
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

# One entry per warning in samples/SampleUnityCode/SampleBehaviour.cs: '<rule>: <allocation> in <method>'.
$expected = @(
    'OPL001: new List<int> in Update'
    'OPL001: new List<string> in Update'
    'OPL001: Instantiate in Update'
    'OPL001: new Vector3 boxed to object in Update'
    'OPL001: new int[] in FixedUpdate'
    'OPL001: new List<float> in Tick'      # from samples/SampleUnityCode/.editorconfig
    'OPL002: string interpolation in Update' # raised to a warning in .editorconfig
    'OPL002: iterator state machine for Spawn() in Update'
    'OPL002: async state machine for SaveAsync() in Update'
    'OPL002: LINQ CountAlive() in Update'
    'OPL002: enumerator for foreach over IReadOnlyList<int> in Update'
    'OPL003: Camera.allCameras in Update'
    'OPL006: string field Label in LabelJob'
    'OPL007: NativeArray<int> TempJob not disposed on every path in Update'
    'OPL008: Resources.Load in Update'       # raised to a warning in .editorconfig
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$sampleProject = Join-Path $repoRoot 'samples/SampleUnityCode/SampleUnityCode.csproj'

# --no-incremental forces the compiler to run, so warnings are reported even when the sample is
# already up to date. -warnaserror matches CI; the sample exempts the OPL rules in its project file.
$buildArgs = @('build', $sampleProject, '-c', $Configuration, '--nologo', '--no-incremental', '-warnaserror', '-clp:NoSummary')
if ($NoDependencies) { $buildArgs += '--no-dependencies' }

Write-Host "dotnet $($buildArgs -join ' ')"
$output = & dotnet @buildArgs 2>&1 | ForEach-Object { "$_" }
$exitCode = $LASTEXITCODE
$output | Write-Host
if ($exitCode -ne 0) { throw "Sample build failed with exit code $exitCode." }

# MSBuild can echo a warning more than once; key on rule and file(line,col) to count each diagnostic once.
# OPL001 and OPL002 read "'<allocation>' allocates inside ..."; OPL003 reads "'<api>' returns a new '<type>' on every call inside ...";
# OPL008 reads "'<api>' loads an asset on every call inside ...".
$pattern = '^(?<location>.+?\(\d+,\d+\)): warning (?<rule>OPL\d{3}): ''(?<allocation>.+?)'' (?:allocates|returns a new ''.+?'' on every call|loads an asset on every call) inside the frequently-called method ''(?<method>.+?)''\.'
# OPL006 reads "Field '<field>' of job struct '<job>' has the reference type '<type>'.".
$jobPattern = '^(?<location>.+?\(\d+,\d+\)): warning OPL006: Field ''(?<field>.+?)'' of job struct ''(?<job>.+?)'' has the reference type ''(?<type>.+?)''\.'
# OPL007 reads "'<type>' allocated with Allocator.<allocator> in '<method>' is never disposed." or "... is not disposed on every path ...".
$disposePattern = '^(?<location>.+?\(\d+,\d+\)): warning OPL007: ''(?<type>.+?)'' allocated with Allocator\.(?<allocator>\w+) in ''(?<method>.+?)'' is (?<problem>never disposed|not disposed on every path)'
$byLocation = [ordered]@{}
foreach ($line in $output) {
    $match = [regex]::Match($line, $pattern)
    if ($match.Success) {
        $key = "$($match.Groups['rule'].Value) $($match.Groups['location'].Value.Trim())"
        $byLocation[$key] = "$($match.Groups['rule'].Value): $($match.Groups['allocation'].Value) in $($match.Groups['method'].Value)"
        continue
    }
    $match = [regex]::Match($line, $jobPattern)
    if ($match.Success) {
        $key = "OPL006 $($match.Groups['location'].Value.Trim())"
        $byLocation[$key] = "OPL006: $($match.Groups['type'].Value) field $($match.Groups['field'].Value) in $($match.Groups['job'].Value)"
        continue
    }
    $match = [regex]::Match($line, $disposePattern)
    if ($match.Success) {
        $key = "OPL007 $($match.Groups['location'].Value.Trim())"
        $byLocation[$key] = "OPL007: $($match.Groups['type'].Value) $($match.Groups['allocator'].Value) $($match.Groups['problem'].Value) in $($match.Groups['method'].Value)"
    }
}
$actual = @($byLocation.Values)

$missing = @($expected | Where-Object { $actual -notcontains $_ })
$unexpected = @($actual | Where-Object { $expected -notcontains $_ })

if ($missing.Count -gt 0 -or $unexpected.Count -gt 0 -or $actual.Count -ne $expected.Count) {
    $message = "Sample OPL warnings do not match. Expected $($expected.Count), found $($actual.Count)."
    if ($missing.Count -gt 0) { $message += "`n  Missing:`n    " + ($missing -join "`n    ") }
    if ($unexpected.Count -gt 0) { $message += "`n  Unexpected:`n    " + ($unexpected -join "`n    ") }
    throw $message
}

Write-Host "Sample produced the $($expected.Count) expected OPL warnings:"
$expected | ForEach-Object { Write-Host "  $_" }
