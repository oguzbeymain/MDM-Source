# Kurulum klasorundeki Uninstall.exe iki kiple acilir:
#   argumansiz    -> dogrudan kaldirma onayi
#   /maintenance  -> onar / degistir / kaldir ekrani (Ayarlar > Uygulamalar > Degistir)
param(
    [string[]]$Arguments = @(),
    [ValidateSet('None', 'Repair')]
    [string]$Action = 'None'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$exe = Join-Path $env:LOCALAPPDATA 'Programs\MuckDownloadManager\Uninstall.exe'
$proc = if ($Arguments.Count) { Start-Process $exe -ArgumentList $Arguments -PassThru }
        else { Start-Process $exe -PassThru }
Start-Sleep -Seconds 5

$cond = New-Object Windows.Automation.PropertyCondition(
    [Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = [Windows.Automation.AutomationElement]::RootElement.FindFirst(
    [Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { throw 'pencere bulunamadi' }

function Get-Texts {
    $tc = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Text)
    $win.FindAll([Windows.Automation.TreeScope]::Descendants, $tc) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ }
}

Write-Host '== acilan ekran =='
Get-Texts | ForEach-Object { "   $_" }

if ($Action -eq 'Repair') {
    $bc = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Button)
    $card = $win.FindAll([Windows.Automation.TreeScope]::Descendants, $bc) |
        Where-Object { $_.Current.AutomationId -eq 'BtnMaintRepair' } | Select-Object -First 1
    if (-not $card) { throw 'onar karti yok' }
    $card.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()

    for ($i = 0; $i -lt 60; $i++) {
        if (@(Get-Texts) -match 'tamamland|complete') { break }
        Start-Sleep -Milliseconds 500
    }
    Write-Host '== onarim sonrasi =='
    Get-Texts | ForEach-Object { "   $_" }
}

if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 }
if (-not $proc.HasExited) { $proc.Kill() }
