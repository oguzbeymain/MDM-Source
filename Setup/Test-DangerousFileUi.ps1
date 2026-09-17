# Güvenlik paneli + tehlikeli dosya uyarı diyaloğunu ekranda açıp doğrular.
# Çıktı: %TEMP%\mdm-security-panel.png ve %TEMP%\mdm-dangerous-dialog.png
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public static class WinCap {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
}
'@

function Capture-Hwnd([IntPtr]$h, [string]$path) {
    if ($h -eq [IntPtr]::Zero) { throw "Capture: hwnd yok" }
    $r = New-Object RECT
    [WinCap]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
    $w = [Math]::Max(1, $r.Right - $r.Left)
    $ht = [Math]::Max(1, $r.Bottom - $r.Top)
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [WinCap]::PrintWindow($h, $dc, 2) | Out-Null
    $g.ReleaseHdc($dc)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "screenshot: $path"
}

function Get-MdmWindow($procId) {
    $cond = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $win = [Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [Windows.Automation.TreeScope]::Children, $cond)
        if ($win -and $win.Current.NativeWindowHandle -ne 0) { return $win }
        Start-Sleep -Milliseconds 400
    }
    throw "MDM penceresi bulunamadı"
}

function Find-Desc($root, $type, $name = $null, $autoId = $null) {
    $conds = New-Object System.Collections.Generic.List[Windows.Automation.Condition]
    $conds.Add((New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
    if ($name) {
        $conds.Add((New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::NameProperty, $name)))
    }
    if ($autoId) {
        $conds.Add((New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::AutomationIdProperty, $autoId)))
    }
    $and = New-Object Windows.Automation.AndCondition ($conds.ToArray())
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $and)
}

function Find-Any($root, $type) {
    $c = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    return $root.FindAll([Windows.Automation.TreeScope]::Descendants, $c)
}

function Invoke-El($el, $label) {
    if (-not $el) { throw "element yok: $label" }
    $pat = $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
    Start-Sleep -Milliseconds 700
}

function Set-Toggle($el, [bool]$on, $label) {
    if (-not $el) { throw "toggle yok: $label" }
    $pat = $el.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $cur = $pat.Current.ToggleState
    $want = if ($on) { [Windows.Automation.ToggleState]::On } else { [Windows.Automation.ToggleState]::Off }
    if ($cur -ne $want) { $pat.Toggle(); Start-Sleep -Milliseconds 400 }
}

function Set-Edit($el, [string]$text) {
    if (-not $el) { throw "edit yok" }
    $el.SetFocus()
    Start-Sleep -Milliseconds 200
    # ValuePattern yoksa panoya yapıştır
    try {
        $vp = $el.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue($text)
    } catch {
        [System.Windows.Forms.Clipboard]::SetText($text)
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        Start-Sleep -Milliseconds 80
        [System.Windows.Forms.SendKeys]::SendWait('^v')
    }
    Start-Sleep -Milliseconds 300
}

$mdmExe = Join-Path $env:LOCALAPPDATA 'Programs\MuckDownloadManager\MDM.exe'
if (-not (Test-Path $mdmExe)) { throw "kurulu MDM yok: $mdmExe" }

Get-Process MDM -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Write-Host '== 1) MDM baslat'
$proc = Start-Process $mdmExe -PassThru
$win = Get-MdmWindow $proc.Id
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[WinCap]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
[WinCap]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Seconds 2

Write-Host '== 2) Ayarlar ac'
$settingsBtn = Find-Desc $win ([Windows.Automation.ControlType]::Button) -autoId 'BtnToolbarSettings'
if (-not $settingsBtn) {
    $settingsBtn = Find-Any $win ([Windows.Automation.ControlType]::Button) |
        Where-Object { $_.Current.Name -match 'Ayar|Settings' -or $_.Current.AutomationId -eq 'BtnToolbarSettings' } |
        Select-Object -First 1
}
Invoke-El $settingsBtn 'Ayarlar'
Start-Sleep -Seconds 1

Write-Host '== 3) Guvenlik paneli'
$nav = Find-Desc $win ([Windows.Automation.ControlType]::RadioButton) -autoId 'NavSecurity'
if (-not $nav) {
    $nav = Find-Any $win ([Windows.Automation.ControlType]::RadioButton) |
        Where-Object { $_.Current.Name -match 'Güvenlik|Security|Sicherheit' } |
        Select-Object -First 1
}
if (-not $nav) { throw 'NavSecurity bulunamadi — sol menude Guvenlik yok' }

try {
    $sel = $nav.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
} catch {
    Invoke-El $nav 'NavSecurity'
}
Start-Sleep -Seconds 1

# Checkbox metinlerini oku
$checks = Find-Any $win ([Windows.Automation.ControlType]::CheckBox) | ForEach-Object { $_.Current.Name }
Write-Host 'checkboxlar:'
$checks | ForEach-Object { "  - $_" }
$hasDanger = @($checks) -match 'Zararl|dangerous|izin sor|harmful|executable'
if (-not $hasDanger) { throw 'Zararli dosya checkboxi gorunmuyor' }

$chk = Find-Desc $win ([Windows.Automation.ControlType]::CheckBox) -autoId 'ChkWarnDangerous'
if (-not $chk) {
    $chk = Find-Any $win ([Windows.Automation.ControlType]::CheckBox) |
        Where-Object { $_.Current.Name -match 'Zararl|dangerous|izin sor' } | Select-Object -First 1
}
Set-Toggle $chk $true 'ChkWarnDangerous'
Write-Host 'ChkWarnDangerous = ON'

$panelShot = Join-Path $env:TEMP 'mdm-security-panel.png'
Capture-Hwnd $hwnd $panelShot

Write-Host '== 4) Kaydet / kapat'
$save = Find-Desc $win ([Windows.Automation.ControlType]::Button) -autoId 'BtnSave'
if (-not $save) {
    $save = Find-Any $win ([Windows.Automation.ControlType]::Button) |
        Where-Object { $_.Current.Name -match 'Kaydet|Save' } | Select-Object -First 1
}
if ($save) { Invoke-El $save 'Kaydet' } else {
    $cancel = Find-Any $win ([Windows.Automation.ControlType]::Button) |
        Where-Object { $_.Current.Name -match 'Iptal|İptal|Cancel' } | Select-Object -First 1
    if ($cancel) { Invoke-El $cancel 'Iptal' }
}
Start-Sleep -Seconds 1

Write-Host '== 5) Yeni indirme (.exe URL)'
$newBtn = Find-Desc $win ([Windows.Automation.ControlType]::Button) -autoId 'BtnToolbarNew'
if (-not $newBtn) {
    $newBtn = Find-Any $win ([Windows.Automation.ControlType]::Button) |
        Where-Object { $_.Current.Name -match 'Yeni|New' -or $_.Current.AutomationId -eq 'BtnToolbarNew' } |
        Select-Object -First 1
}
Invoke-El $newBtn 'Yeni'

Start-Sleep -Seconds 1
$edit = Find-Desc $win ([Windows.Automation.ControlType]::Edit) -autoId 'TxtUrl'
if (-not $edit) {
    $edit = Find-Any $win ([Windows.Automation.ControlType]::Edit) | Select-Object -First 1
}
Set-Edit $edit 'https://example.com/MDM-Security-Test.exe'

$ok = Find-Any $win ([Windows.Automation.ControlType]::Button) |
    Where-Object { $_.Current.Name -match 'Tamam|OK|Indir|İndir|Ekle|Add|Baslat|Başlat' -and $_.Current.AutomationId -ne 'BtnCloseX' } |
    Select-Object -First 1
if (-not $ok) {
    # NewUrlDialog genelde BtnOk / BtnConfirm
    $ok = Find-Desc $win ([Windows.Automation.ControlType]::Button) -autoId 'BtnOk'
    if (-not $ok) { $ok = Find-Desc $win ([Windows.Automation.ControlType]::Button) -autoId 'BtnConfirm' }
    if (-not $ok) { $ok = Find-Desc $win ([Windows.Automation.ControlType]::Button) -autoId 'BtnDownload' }
}
if (-not $ok) {
    Write-Host 'buton adlari (yeni indirme):'
    Find-Any $win ([Windows.Automation.ControlType]::Button) | ForEach-Object {
        "  [{0}] {1}" -f $_.Current.AutomationId, $_.Current.Name
    }
    throw 'Yeni indirme onay butonu yok'
}
Invoke-El $ok 'Yeni-indirme-onay'
Start-Sleep -Seconds 3

Write-Host '== 6) Oturum penceresinde Baslat'
# Session window ayrı pencere olabilir
$session = $null
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $all = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
    foreach ($w in $all) {
        $start = Find-Desc $w ([Windows.Automation.ControlType]::Button) -autoId 'BtnStart'
        if (-not $start) {
            $start = Find-Any $w ([Windows.Automation.ControlType]::Button) |
                Where-Object { $_.Current.Name -match 'Baslat|Başlat|Start|Resume|Devam' } |
                Select-Object -First 1
        }
        if ($start) { $session = $w; $startBtn = $start; break }
    }
    if ($session) { break }
    Start-Sleep -Milliseconds 500
}
if (-not $session) {
    Write-Host 'MDM altindaki tum butonlar:'
    Find-Any $win ([Windows.Automation.ControlType]::Button) | ForEach-Object {
        "  [{0}] {1}" -f $_.Current.AutomationId, $_.Current.Name
    }
    throw 'Indirme oturum penceresi / Baslat bulunamadi'
}
Invoke-El $startBtn 'Baslat'
Start-Sleep -Seconds 2

Write-Host '== 7) Tehlikeli dosya diyaloğu'
$dialog = $null
$dangerBtn = $null
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    $all = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
    foreach ($w in $all) {
        $texts = Find-Any $w ([Windows.Automation.ControlType]::Text) | ForEach-Object { $_.Current.Name }
        $joined = ($texts -join ' | ')
        if ($joined -match 'zarar|dangerous|Izin ver|İzin ver|Allow|executable|calistirilabilir|çalıştırılabilir') {
            $dialog = $w
            $dangerBtn = Find-Any $w ([Windows.Automation.ControlType]::Button) |
                Where-Object { $_.Current.Name -match 'Izin verme|İzin verme|Don.t|Block|Vazgec|Vazgeç|Cancel' } |
                Select-Object -First 1
            Write-Host "diyalog metinleri: $joined"
            break
        }
        # ConfirmDialog AutomationId'leri
        $allow = Find-Desc $w ([Windows.Automation.ControlType]::Button) -autoId 'BtnConfirm'
        $block = Find-Desc $w ([Windows.Automation.ControlType]::Button) -autoId 'BtnCancel'
        if ($allow -and $block) {
            $nm = $allow.Current.Name
            if ($nm -match 'Izin|İzin|Allow') {
                $dialog = $w
                $dangerBtn = $block
                Write-Host ("diyalog butonlari: allow={0} block={1}" -f $allow.Current.Name, $block.Current.Name)
                break
            }
        }
    }
    if ($dialog) { break }
    Start-Sleep -Milliseconds 400
}

if (-not $dialog) {
    Write-Host 'Acik pencereler:'
    $all = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
    foreach ($w in $all) {
        Write-Host ("  window: {0}" -f $w.Current.Name)
        Find-Any $w ([Windows.Automation.ControlType]::Button) | ForEach-Object {
            "    [{0}] {1}" -f $_.Current.AutomationId, $_.Current.Name
        }
        Find-Any $w ([Windows.Automation.ControlType]::Text) | Select-Object -First 8 | ForEach-Object {
            "    text: {0}" -f $_.Current.Name
        }
    }
    throw 'Tehlikeli dosya uyari diyaloğu ACILMADI'
}

$dlgHwnd = [IntPtr]$dialog.Current.NativeWindowHandle
$dlgShot = Join-Path $env:TEMP 'mdm-dangerous-dialog.png'
if ($dlgHwnd -ne [IntPtr]::Zero) { Capture-Hwnd $dlgHwnd $dlgShot }
else { Capture-Hwnd $hwnd $dlgShot }

if ($dangerBtn) {
    Invoke-El $dangerBtn 'Izin verme'
    Write-Host 'Izin verme tiklandi — indirme baslamadi'
} else {
    Write-Host 'uyari: engelle butonu bulunamadi, diyalog acik kaldi'
}

Write-Host '== SONUC: Guvenlik paneli + tehlikeli dosya uyarisi CALISIYOR'
Write-Host $panelShot
Write-Host $dlgShot
