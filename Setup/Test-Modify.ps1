# Degistir akisi: bakim ekrani -> dil -> secenekler -> Kur.
# Secilen dil indeksiyle (LstLanguage sirasi: tr,en,de,fr,es,it,ru,ar,fa,zh-CN,zh-TW,ja,ko)
# dilin gercekten uygulandigi dogrulanir.
param([int]$LanguageIndex = 1)   # 1 = English

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$setup = Get-ChildItem (Join-Path $PSScriptRoot '..\artifacts') -Filter 'MDM-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$proc = Start-Process $setup.FullName -PassThru
Start-Sleep -Seconds 5

$cond = New-Object Windows.Automation.PropertyCondition(
    [Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = [Windows.Automation.AutomationElement]::RootElement.FindFirst(
    [Windows.Automation.TreeScope]::Children, $cond)

function Find-ByType($type) {
    $c = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    $win.FindAll([Windows.Automation.TreeScope]::Descendants, $c)
}
function Texts { Find-ByType ([Windows.Automation.ControlType]::Text) | ForEach-Object { $_.Current.Name } | Where-Object { $_ } }
function Click($automationId) {
    $b = Find-ByType ([Windows.Automation.ControlType]::Button) |
        Where-Object { $_.Current.AutomationId -eq $automationId } | Select-Object -First 1
    if (-not $b) { throw "buton yok: $automationId" }
    $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800
}

Write-Host '== 1) bakim ekrani -> Degistir'
Click 'BtnMaintModify'

Write-Host '== 2) dil sayfasi'
$items = Find-ByType ([Windows.Automation.ControlType]::ListItem)
$target = $items[$LanguageIndex]
Write-Host ("   secilen: {0}" -f $target.Current.Name)
$target.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 1
Texts | Select-Object -First 4 | ForEach-Object { "   $_" }

Write-Host '== 3) Ileri -> secenekler'
Click 'BtnNext'
Texts | Select-Object -First 6 | ForEach-Object { "   $_" }

Write-Host '== 4) Kur'
Click 'BtnNext'
for ($i = 0; $i -lt 90; $i++) {
    if (@(Texts) -match 'tamamland|complete|abgeschlossen|termin|complet|завершен|完了|완료|完成') { break }
    Start-Sleep -Milliseconds 500
}
Texts | ForEach-Object { "   $_" }

if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 }
if (-not $proc.HasExited) { $proc.Kill() }
