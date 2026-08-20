<#
.SYNOPSIS
  Builds DownloadMuck/MDM and uploads a GitHub Release to MDM-App.

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

$Csproj = Join-Path $ProjectRoot "DownloadMuck.csproj"
$PublishDir = Join-Path $ProjectRoot "artifacts\publish\$Runtime"
$ZipPath = Join-Path $ProjectRoot "artifacts\MDM-$Version-$Runtime.zip"

Write-Host "==> Version: $Tag" -ForegroundColor Cyan
Write-Host "==> Project: $Csproj"

[xml]$xml = Get-Content $Csproj
$pg = $xml.Project.PropertyGroup | Where-Object { $_.Version -or $_.TargetFramework } | Select-Object -First 1
if (-not $pg) { throw "csproj PropertyGroup not found." }

$pg.Version = $Version
if ($null -ne $pg.AssemblyVersion) { $pg.AssemblyVersion = "$Version.0" }
if ($null -ne $pg.FileVersion) { $pg.FileVersion = "$Version.0" }
if ($null -ne $pg.InformationalVersion) { $pg.InformationalVersion = $Version }

$xml.Save($Csproj)
Write-Host "==> csproj version set to $Version"

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $ZipPath -Parent) -Force | Out-Null

Write-Host "==> dotnet publish ($Runtime, self-contained, single-file)"
dotnet publish $Csproj `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Write-Host "==> Creating zip: $ZipPath"
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force

Write-Host "==> Creating GitHub release: $AppRepo $Tag"
$ErrorActionPreference = "Continue"
gh release view $Tag --repo $AppRepo 2>$null | Out-Null
$viewExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"

if ($viewExit -eq 0) {
    Write-Host "Release $Tag already exists - uploading asset..." -ForegroundColor Yellow
    gh release upload $Tag $ZipPath --repo $AppRepo --clobber
} else {
    gh release create $Tag $ZipPath `
        --repo $AppRepo `
        --title "MDM $Tag" `
        --notes $Notes
}

if ($LASTEXITCODE -ne 0) { throw "GitHub release failed. Check 'gh auth status'." }

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Tag:   $Tag"
Write-Host "  Zip:   $ZipPath"
Write-Host "  URL:   https://github.com/$AppRepo/releases/tag/$Tag"
