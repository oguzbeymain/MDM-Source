# Tehlikeli dosya uyarisini tekrar acar, ekran goruntusu alir, 10 sn bekler, Izin verme'ye basar.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct RECT2 { public int Left, Top, Right, Bottom; }
public static class WC2 {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT2 r, int s);
}
'@

function Cap([IntPtr]$h, [string]$path) {
    $r = New-Object RECT2
    [WC2]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ([Math]::Max(1, $r.Right - $r.Left)), ([Math]::Max(1, $r.Bottom - $r.Top))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [WC2]::PrintWindow($h, $dc, 2) | Out-Null
    $g.ReleaseHdc($dc)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "shot: $path"
}

function Inv($el) {
    $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
}

$proc = Get-Process MDM -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    $proc = Start-Process "$env:LOCALAPPDATA\Programs\MuckDownloadManager\MDM.exe" -PassThru
    Start-Sleep -Seconds 4
}

$root = [Windows.Automation.AutomationElement]::RootElement
$pcond = New-Object Windows.Automation.PropertyCondition(
    [Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([Windows.Automation.TreeScope]::Children, $pcond)
[WC2]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null

$btns = $win.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Button)))

$new = $btns | Where-Object { $_.Current.AutomationId -eq 'BtnToolbarNew' } | Select-Object -First 1
Inv $new
Start-Sleep -Seconds 1

$edit = $win.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, 'TxtUrl')))
if (-not $edit) {
    $edit = $win.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Edit)))
}
try {
    $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue(
        'https://example.com/MDM-Security-Test.exe')
} catch {
    [System.Windows.Forms.Clipboard]::SetText('https://example.com/MDM-Security-Test.exe')
    $edit.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^a^v')
}

$ok = $win.FindAll([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Button))) |
    Where-Object {
        $_.Current.Name -match 'Tamam|OK|Indir|İndir|Ekle' -or
        $_.Current.AutomationId -match 'BtnOk|BtnConfirm|BtnDownload|BtnAdd'
    } | Select-Object -First 1
if (-not $ok) { throw 'Yeni indirme onay butonu yok' }
Inv $ok
Start-Sleep -Seconds 3

$start = $null
for ($i = 0; $i -lt 25; $i++) {
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, $pcond)) {
        $b = $w.FindAll([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::Button))) |
            Where-Object {
                $_.Current.AutomationId -eq 'BtnStart' -or
                $_.Current.Name -match 'Baslat|Başlat|Start'
            } | Select-Object -First 1
        if ($b) { $start = $b; break }
    }
    if ($start) { break }
    Start-Sleep -Milliseconds 400
}
if (-not $start) { throw 'Baslat yok' }

# ShowDialog UI thread'i kilitler; diyalogu baska process isi gibi tarayalim
$watcher = Start-Job -ScriptBlock {
    param($pid)
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = [Windows.Automation.AutomationElement]::RootElement
    $pcond = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $pid)
    for ($i = 0; $i -lt 40; $i++) {
        foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, $pcond)) {
            $texts = @($w.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition(
                    [Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [Windows.Automation.ControlType]::Text))) |
                ForEach-Object { $_.Current.Name })
            $joined = $texts -join ' | '
            if ($joined -match 'zarar|dangerous|Security-Test') {
                return [pscustomobject]@{
                    Hwnd  = $w.Current.NativeWindowHandle
                    Name  = $w.Current.Name
                    Texts = ($texts | Select-Object -First 8)
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
} -ArgumentList $proc.Id

Inv $start
$null = Wait-Job $watcher -Timeout 25
$info = Receive-Job $watcher
Remove-Job $watcher -Force -ErrorAction SilentlyContinue
if (-not $info) { throw 'Tehlikeli dosya diyaloğu açılmadı' }

Write-Host 'DIYALOG ACILDI:'
$info.Texts | ForEach-Object { "  $_" }

$out = Join-Path $env:TEMP 'mdm-dangerous-dialog.png'
Cap ([IntPtr]$info.Hwnd) $out

Write-Host 'Ekranda 10 sn gorunur kalacak...'
Start-Sleep -Seconds 10

# Izin verme
foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, $pcond)) {
    foreach ($b in $w.FindAll([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button)))) {
        if ($b.Current.Name -match 'verme' -or $b.Current.AutomationId -eq 'BtnCancel') {
            try {
                $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                Write-Host 'Izin verme tiklandi'
                return
            } catch { }
        }
    }
}
Write-Host 'Izin verme bulunamadi — diyalog ekranda kalmis olabilir'
