# Bakim ekraninin goruntusunu alir. Ekran yerine pencerenin kendisi cizdirilir
# (PrintWindow): onde baska pencere olsa da yalnizca setup penceresi kaydedilir.
param([string]$Out = "$env:TEMP\mdm-maintenance.png")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class Win {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
}
'@

$setup = Get-ChildItem (Join-Path $PSScriptRoot '..\artifacts') -Filter 'MDM-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$proc = Start-Process -FilePath $setup.FullName -PassThru
Start-Sleep -Seconds 6
$proc.Refresh()

$h = $proc.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { throw 'setup penceresi bulunamadi' }

$r = New-Object RECT
[Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null   # 9 = EXTENDED_FRAME_BOUNDS
$bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
[Win]::PrintWindow($h, $dc, 2) | Out-Null                     # 2 = PW_RENDERFULLCONTENT
$g.ReleaseHdc($dc)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 }
if (-not $proc.HasExited) { $proc.Kill() }
Write-Host $Out
