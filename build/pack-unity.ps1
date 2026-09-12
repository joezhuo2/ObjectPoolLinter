<#
.SYNOPSIS
    Builds the Unity distribution artifacts for ObjectPoolLinter.

.DESCRIPTION
    Produces two files under the output directory:

      ObjectPoolLinter-<version>.unitypackage      drag-and-drop import into Assets/
      com.joezhuo.objectpoollinter-<version>.tgz   UPM tarball ("Install package from tarball")

    Both carry ObjectPoolLinter.dll and ObjectPoolLinter.CodeFixes.dll with .meta files that label
    them RoslynAnalyzer and disable every platform, which is what Unity requires of an analyzer
    plugin. Asset GUIDs are derived from the asset path, so re-running the script produces the same
    GUIDs and upgrading in place does not orphan the previous import.

.PARAMETER Configuration
    Build configuration to package. Defaults to Release.

.PARAMETER OutputDirectory
    Where to write the artifacts. Defaults to <repo>/artifacts/unity.

.PARAMETER SkipBuild
    Package whatever is already in bin/<Configuration> instead of building first.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageProject = Join-Path $repoRoot 'src/ObjectPoolLinter.Package/ObjectPoolLinter.Package.csproj'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts/unity' }

# The package project is the single source of the version number.
$versionNode = ([xml](Get-Content -Raw $packageProject)).SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode) { throw "No <Version> found in $packageProject." }
$version = $versionNode.InnerText.Trim()

if (-not $SkipBuild) {
    Write-Host "Building $Configuration..."
    & dotnet build $packageProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
}

$analyzerDll = Join-Path $repoRoot "src/ObjectPoolLinter/bin/$Configuration/netstandard2.0/ObjectPoolLinter.dll"
$codeFixDll = Join-Path $repoRoot "src/ObjectPoolLinter.CodeFixes/bin/$Configuration/netstandard2.0/ObjectPoolLinter.CodeFixes.dll"
foreach ($dll in @($analyzerDll, $codeFixDll)) {
    if (-not (Test-Path $dll)) { throw "Missing $dll. Build the solution, or drop -SkipBuild." }
}

# ---------------------------------------------------------------- meta files

# Unity keys every asset by the GUID in its .meta file. Hashing the asset path keeps those GUIDs
# stable across runs without checking generated .meta files into the repository.
function New-AssetGuid([string]$assetPath) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("ObjectPoolLinter:$assetPath"))
    }
    finally { $md5.Dispose() }
    -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

function New-PluginMeta([string]$assetPath) {
    @"
fileFormatVersion: 2
guid: $(New-AssetGuid $assetPath)
labels:
- RoslynAnalyzer
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  userData:
  assetBundleName:
  assetBundleVariant:
"@
}

function New-TextMeta([string]$assetPath) {
    @"
fileFormatVersion: 2
guid: $(New-AssetGuid $assetPath)
TextScriptImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
"@
}

function New-FolderMeta([string]$assetPath) {
    @"
fileFormatVersion: 2
guid: $(New-AssetGuid $assetPath)
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
"@
}

# Unity's YAML is LF-terminated and carries no BOM.
function Write-TextFile([string]$path, [string]$content) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $normalized = $content -replace "`r`n", "`n"
    if (-not $normalized.EndsWith("`n")) { $normalized += "`n" }
    [System.IO.File]::WriteAllText($path, $normalized, (New-Object System.Text.UTF8Encoding($false)))
}

$manifest = (Get-Content -Raw (Join-Path $repoRoot 'unity/package.json.in')).Replace('__VERSION__', $version)
$packageReadme = Get-Content -Raw (Join-Path $repoRoot 'unity/README.md')
$license = Get-Content -Raw (Join-Path $repoRoot 'LICENSE')

$staging = Join-Path $OutputDirectory '.staging'
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# -------------------------------------------------------------- UPM tarball

# A UPM tarball is an npm tarball: every path sits under a single "package" root.
$upmRoot = Join-Path $staging 'upm/package'
$analyzerFolder = Join-Path $upmRoot 'RoslynAnalyzers'
New-Item -ItemType Directory -Force -Path $analyzerFolder | Out-Null

Write-TextFile (Join-Path $upmRoot 'package.json') $manifest
Write-TextFile (Join-Path $upmRoot 'package.json.meta') (New-TextMeta 'package.json')
Write-TextFile (Join-Path $upmRoot 'README.md') $packageReadme
Write-TextFile (Join-Path $upmRoot 'README.md.meta') (New-TextMeta 'README.md')
Write-TextFile (Join-Path $upmRoot 'LICENSE.md') $license
Write-TextFile (Join-Path $upmRoot 'LICENSE.md.meta') (New-TextMeta 'LICENSE.md')
Write-TextFile (Join-Path $upmRoot 'RoslynAnalyzers.meta') (New-FolderMeta 'RoslynAnalyzers')

foreach ($dll in @($analyzerDll, $codeFixDll)) {
    $name = Split-Path -Leaf $dll
    Copy-Item $dll (Join-Path $analyzerFolder $name)
    Write-TextFile (Join-Path $analyzerFolder "$name.meta") (New-PluginMeta "RoslynAnalyzers/$name")
}

$tgz = Join-Path $OutputDirectory "com.joezhuo.objectpoollinter-$version.tgz"
if (Test-Path $tgz) { Remove-Item -Force $tgz }
& tar -czf $tgz -C (Join-Path $staging 'upm') 'package'
if ($LASTEXITCODE -ne 0) { throw "tar failed while building $tgz." }

# ----------------------------------------------------------- .unitypackage

# A .unitypackage is a gzipped tar of one directory per asset, named by the asset's GUID and holding
# the asset bytes, its .meta, and the project-relative path the asset should land at.
$unityPackageRoot = Join-Path $staging 'unitypackage'
$assetsFolder = 'Assets/Plugins/ObjectPoolLinter'

function Add-PackageEntry([string]$pathName, [string]$metaContent, [string]$sourceFile) {
    $entry = Join-Path $unityPackageRoot (New-AssetGuid $pathName)
    New-Item -ItemType Directory -Force -Path $entry | Out-Null
    Write-TextFile (Join-Path $entry 'pathname') $pathName
    Write-TextFile (Join-Path $entry 'asset.meta') $metaContent
    if ($sourceFile) { Copy-Item $sourceFile (Join-Path $entry 'asset') }
}

Add-PackageEntry 'Assets/Plugins' (New-FolderMeta 'Assets/Plugins') $null
Add-PackageEntry $assetsFolder (New-FolderMeta $assetsFolder) $null
foreach ($dll in @($analyzerDll, $codeFixDll)) {
    $name = Split-Path -Leaf $dll
    Add-PackageEntry "$assetsFolder/$name" (New-PluginMeta "$assetsFolder/$name") $dll
}
Add-PackageEntry "$assetsFolder/README.md" (New-TextMeta "$assetsFolder/README.md") (Join-Path $repoRoot 'unity/README.md')

$unityPackage = Join-Path $OutputDirectory "ObjectPoolLinter-$version.unitypackage"
if (Test-Path $unityPackage) { Remove-Item -Force $unityPackage }
$entries = Get-ChildItem -Directory $unityPackageRoot | ForEach-Object { $_.Name }
& tar -czf $unityPackage -C $unityPackageRoot @entries
if ($LASTEXITCODE -ne 0) { throw "tar failed while building $unityPackage." }

Remove-Item -Recurse -Force $staging

Write-Host "ObjectPoolLinter $version"
Write-Host "  $unityPackage"
Write-Host "  $tgz"
