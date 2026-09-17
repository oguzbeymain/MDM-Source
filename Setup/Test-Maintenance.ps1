# Bakim ekrani (onar / degistir / kaldir) dogrulamasi: setup'i acar, metinleri okur,
# istenen karta tiklar ve sonucu bildirir. Kullanim:
#   .\Test-Maintenance.ps1 -Action None|Repair|Modify|Remove
param(
    [ValidateSet('None', 'Repair', 'Modify', 'Remove')]
    [string]$Action = 'None'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$setup = Get-ChildItem (Join-Path $PSScriptRoot '..\artifacts') -Filter 'MDM-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw 'artifacts icinde setup bulunamadi; once Build-Setup.ps1 calistirin' }
$proc = Start-Process -FilePath $setup.FullName -PassThru
Start-Sleep -Seconds 4

function Get-Window {
    param($processId)
    $cond = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    for ($i = 0; $i -lt 20; $i++) {
        $w = [Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Get-Texts {
    param($window)
    $cond = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Text)
    $window.FindAll([Windows.Automation.TreeScope]::Descendants, $cond) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ }
}

function Get-Buttons {
    param($window)
    $cond = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Button)
    $window.FindAll([Windows.Automation.TreeScope]::Descendants, $cond)
}

$win = Get-Window $proc.Id
if (-not $win) { throw 'setup penceresi bulunamadi' }

Write-Host "== pencere: $($win.Current.Name)" -ForegroundColor Cyan
Write-Host '== ekrandaki metinler =='
Get-Texts $win | ForEach-Object { "   $_" }

$buttons = Get-Buttons $win
Write-Host "== butonlar ($($buttons.Count)) =="
foreach ($b in $buttons) {
    $label = if ($b.Current.Name) { $b.Current.Name } else { '(kart)' }
    Write-Host ("   {0} | id={1}" -f $label, $b.Current.AutomationId)
}

if ($Action -ne 'None') {
    $id = @{ Repair = 'BtnMaintRepair'; Modify = 'BtnMaintModify'; Remove = 'BtnMaintRemove' }[$Action]
    $card = $buttons | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1
    if (-not $card) { throw "kart bulunamadi: $id" }

    Write-Host "== tiklaniyor: $id" -ForegroundColor Yellow
    $card.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 3

    # Kaldirma kartindan sonra onay sayfasi gelir; islemi BtnNext baslatir
    if ($Action -eq 'Remove') {
        Start-Sleep -Seconds 1
        $next = Get-Buttons $win | Where-Object { $_.Current.AutomationId -eq 'BtnNext' } | Select-Object -First 1
        Write-Host "== onay sayfasi: $($next.Current.Name)" -ForegroundColor Yellow
        $next.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    if ($Action -ne 'Modify') {
        for ($i = 0; $i -lt 40; $i++) {
            $texts = @(Get-Texts $win)
            if ($texts -match 'tamamland|complete') { break }
            Start-Sleep -Milliseconds 500
        }
    }

    # Hata diyalogu ayri bir pencere: mesaji da yaz
    $cond = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $all = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $all) {
        if ($w.Current.NativeWindowHandle -eq $win.Current.NativeWindowHandle) { continue }
        Write-Host "== diyalog: $($w.Current.Name)" -ForegroundColor Red
        Get-Texts $w | ForEach-Object { "   $_" }
    }

    Write-Host '== islem sonrasi metinler =='
    Get-Texts $win | ForEach-Object { "   $_" }
}

if (-not $proc.HasExited) {
    Write-Host '== pencere kapatiliyor ==' -ForegroundColor DarkGray
    $proc.CloseMainWindow() | Out-Null
    Start-Sleep -Seconds 2
    if (-not $proc.HasExited) { $proc.Kill() }
}
