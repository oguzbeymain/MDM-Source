<#
.SYNOPSIS
  Builds DownloadMuck + MDM.Updater and uploads a GitHub Release to MDM-App.

.EXAMPLE
  .\scripts\Publish-Release.ps1 -Version 1.0.1
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Notes = "MDM release package",

    [string]$AppRepo = "oguzbeymain/MDM-App",

    [ValidateSet("win-x86", "win-x64")]
    [string]$Runtime = "win-x86"
)

$ErrorActionPreference = "Stop"

$Version = $Version.Trim().TrimStart("v", "V")
$Tag = "v$Version"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $ProjectRoot "DownloadMuck.csproj"))) {
    $ProjectRoot = $PSScriptRoot
    if (-not (Test-Path (Join-Path $ProjectRoot "DownloadMuck.csproj"))) {
        throw "DownloadMuck.csproj not found."
    }
}

$MainCsproj = Join-Path $ProjectRoot "DownloadMuck.csproj"
$UpdaterCsproj = Join-Path $ProjectRoot "MDM.Updater\MDM.Updater.csproj"
$PublishDir = Join-Path $ProjectRoot "artifacts\publish\$Runtime"
$ZipPath = Join-Path $ProjectRoot "artifacts\MDM-$Version-$Runtime.zip"

function Set-ProjectVersion([string]$csprojPath, [string]$ver) {
    [xml]$xml = Get-Content $csprojPath
    $pg = $xml.Project.PropertyGroup | Where-Object { $_.Version -or $_.TargetFramework } | Select-Object -First 1
    if (-not $pg) { throw "PropertyGroup not found in $csprojPath" }
    $pg.Version = $ver
    if ($null -ne $pg.AssemblyVersion) { $pg.AssemblyVersion = "$ver.0" }
    if ($null -ne $pg.FileVersion) { $pg.FileVersion = "$ver.0" }
    if ($null -ne $pg.InformationalVersion) { $pg.InformationalVersion = $ver }
    $xml.Save($csprojPath)
}

Write-Host "==> Version: $Tag" -ForegroundColor Cyan
Set-ProjectVersion $MainCsproj $Version
Set-ProjectVersion $UpdaterCsproj $Version

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $ZipPath -Parent) -Force | Out-Null

$commonArgs = @(
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    "-o", $PublishDir
)

Write-Host "==> Publishing DownloadMuck..."
dotnet publish $MainCsproj @commonArgs
if ($LASTEXITCODE -ne 0) { throw "Main publish failed." }

Write-Host "==> Publishing MDM.Updater..."
dotnet publish $UpdaterCsproj @commonArgs
if ($LASTEXITCODE -ne 0) { throw "Updater publish failed." }

# Eklentiyi paketle
$ExtSrc = Join-Path $ProjectRoot "DownloadMuck_Eklenti"
$ExtDst = Join-Path $PublishDir "DownloadMuck_Eklenti"
if (Test-Path $ExtSrc) {
    if (Test-Path $ExtDst) { Remove-Item $ExtDst -Recurse -Force }
    Copy-Item $ExtSrc $ExtDst -Recurse -Force
    Write-Host "==> Extension copied into package"
}

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Write-Host "==> Creating zip: $ZipPath"
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force

Write-Host "==> GitHub release: $AppRepo $Tag"
$ErrorActionPreference = "Continue"
gh release view $Tag --repo $AppRepo 2>$null | Out-Null
$viewExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"

if ($viewExit -eq 0) {
    Write-Host "Release exists - uploading asset..." -ForegroundColor Yellow
    gh release upload $Tag $ZipPath --repo $AppRepo --clobber
} else {
    gh release create $Tag $ZipPath --repo $AppRepo --title "MDM $Tag" --notes $Notes
}

if ($LASTEXITCODE -ne 0) { throw "GitHub release failed." }

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Tag: $Tag"
Write-Host "  Zip: $ZipPath"
Write-Host "  URL: https://github.com/$AppRepo/releases/tag/$Tag"
Write-Host "  Run: MDM.Updater.exe  (or DownloadMuck.exe which redirects to updater)"
