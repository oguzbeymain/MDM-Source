# MuckDownloadManager setup uretici
#
#   .\Setup\Build-Setup.ps1                 -> Release yayin + tek dosya setup
#   .\Setup\Build-Setup.ps1 -Fast           -> mevcut Debug cikitisini paketler (hizli test)
#
param(
  [string]$Runtime = "win-x64",
  [string]$Configuration = "Release",
  [switch]$Fast,
  [string]$OutDir = ""
)
$ErrorActionPreference = "Stop"
$setupDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projDir = Split-Path -Parent $setupDir
if (-not $OutDir) { $OutDir = Join-Path $projDir "artifacts" }

$appProj = Join-Path $projDir "MDM.csproj"
$setupProj = Join-Path $setupDir "MDM.Setup.csproj"
$payloadDir = Join-Path $setupDir "Payload"
$payloadZip = Join-Path $payloadDir "payload.zip"
$stage = Join-Path $env:TEMP "mdm-setup-stage"

Write-Host "== 1/4 uygulama paketi hazirlaniyor =="
Get-Process MDM-Setup -ErrorAction SilentlyContinue | Stop-Process -Force
if (-not $Fast) { Get-Process MDM -ErrorAction SilentlyContinue | Stop-Process -Force }
Start-Sleep -Milliseconds 500

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage, $payloadDir, $OutDir | Out-Null

if ($Fast) {
  $source = Join-Path $projDir "bin\Debug\net10.0-windows"
  if (-not (Test-Path (Join-Path $source "MDM.exe"))) { throw "Debug cikitisi yok: $source" }
  Copy-Item (Join-Path $source "*") $stage -Recurse -Force
} else {
  & dotnet publish $appProj -c $Configuration -r $Runtime --self-contained true `
      -p:PublishSingleFile=false -p:DebugType=none -o $stage --nologo | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "uygulama yayini basarisiz" }

  # Otomatik guncelleme MDM.Updater.exe ile calisir; paketle birlikte gelmeli
  $updaterProj = Join-Path $projDir "MDM.Updater\MDM.Updater.csproj"
  if (Test-Path $updaterProj) {
    & dotnet publish $updaterProj -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -o $stage --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "updater yayini basarisiz" }
  }
}

# Gereksiz dosyalari paketten cikar. ffmpeg/ffprobe/yt-dlp/deno uygulama
# tarafindan ilk ihtiyacta indirilir; setup icinde tasinmaz (~390 MB tasarruf).
$exclude = @("ffmpeg.exe", "ffprobe.exe", "yt-dlp.exe", "deno.exe")
Get-ChildItem $stage -Include *.pdb, *.xml -Recurse -File -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($name in $exclude) {
  Get-ChildItem $stage -Filter $name -Recurse -File -ErrorAction SilentlyContinue | Remove-Item -Force
}
$fileCount = (Get-ChildItem $stage -Recurse -File).Count
Write-Host ("   paket icerigi: {0} dosya" -f $fileCount)

Write-Host "== 2/4 payload.zip olusturuluyor =="
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
  $stage, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$zipMb = [math]::Round((Get-Item $payloadZip).Length / 1MB, 1)
Write-Host ("   payload.zip: {0} MB" -f $zipMb)

Write-Host "== 3/4 setup derleniyor =="
$setupOut = Join-Path $env:TEMP "mdm-setup-out"
if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
& dotnet publish $setupProj -c Release -r $Runtime -o $setupOut --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "setup derlemesi basarisiz" }

Write-Host "== 4/4 cikti kopyalaniyor =="
$version = ([xml](Get-Content $setupProj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$target = Join-Path $OutDir ("MDM-Setup-{0}.exe" -f $version)
# Onceki calisma exe'yi hala kilitli tutabilir; kisa sure yeniden dene
for ($i = 1; $i -le 10; $i++) {
  try { Copy-Item (Join-Path $setupOut "MDM-Setup.exe") $target -Force; break }
  catch {
    if ($i -eq 10) { throw }
    Get-Process -Name "MDM-Setup*" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
  }
}
$setupMb = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host ("hazir: {0} ({1} MB)" -f $target, $setupMb)

# Uygulama ici guncelleme (Ayarlar > Guncelleme) yayindaki zip'i indirip dosyalari
# degistirir; setup exe'si kopyalanamaz. Her yayinda ikisi birlikte gonderilmeli.
$zipTarget = Join-Path $OutDir ("MDM-{0}-{1}.zip" -f $version, $Runtime)
Copy-Item $payloadZip $zipTarget -Force
$zipTargetMb = [math]::Round((Get-Item $zipTarget).Length / 1MB, 1)
Write-Host ("hazir: {0} ({1} MB)" -f $zipTarget, $zipTargetMb)
