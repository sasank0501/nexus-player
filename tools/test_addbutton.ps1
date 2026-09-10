# Reproduces the crash on the queue's Add button and proves it is gone.
#
# Uses UI Automation to invoke the button directly, which separates "the handler
# threw" from "the click never landed" — the ambiguity that wasted time on this
# project before. Then it confirms the process is still alive and a file dialog
# actually appeared.
param([string]$Exe = "bin\Debug\net10.0-windows\NexusPlayer.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

# resolve the repo from this script location, so moving the repo does not
# break the tests the way a hardcoded path did
Set-Location (Split-Path -Parent $PSScriptRoot)
$log = "$env:APPDATA\NexusPlayerV2\nexus.log"
$logBefore = if (Test-Path -LiteralPath $log) { (Get-Item -LiteralPath $log).Length } else { 0 }

$p = Start-Process -FilePath $Exe -PassThru
for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
Start-Sleep -Milliseconds 3000
Write-Output "launched pid $($p.Id), alive=$(-not $p.HasExited)"

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$buttons = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)))
Write-Output "found $($buttons.Count) buttons in the main window"

$add = $null
foreach ($b in $buttons) {
    $help = $b.Current.HelpText
    $name = $b.Current.Name
    if ("$help$name" -match 'Add files') { $add = $b; break }
}
if (-not $add) {
    Write-Output "could not identify the Add button by tooltip; listing what is there:"
    foreach ($b in $buttons) { Write-Output "   name='$($b.Current.Name)' help='$($b.Current.HelpText)'" }
    [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(5000)) { $p.Kill() }
    exit 1
}

Write-Output "invoking Add ('$($add.Current.HelpText)')"
$invoke = $add.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$invoke.Invoke()
Start-Sleep -Milliseconds 2500

$p.Refresh()
$alive = -not $p.HasExited
Write-Output "process alive after invoke : $alive"

# a modal file dialog shows up as a new top-level window owned by the process
$dialog = $null
if ($alive) {
    $dialog = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)))
    if (-not $dialog) {
        $desktop = [System.Windows.Automation.AutomationElement]::RootElement
        $wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
        foreach ($w in $wins) {
            if ($w.Current.Name -match 'Open|Video|Select') { $dialog = $w; break }
        }
    }
}
Write-Output "file dialog opened         : $($null -ne $dialog)$(if ($dialog) { " ('" + $dialog.Current.Name + "')" })"

# close the dialog politely
if ($dialog) {
    try {
        $wp = $dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $wp.Close()
    } catch { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') }
    Start-Sleep -Milliseconds 1200
}

$p.Refresh()
Write-Output "process alive at the end   : $(-not $p.HasExited)"

Write-Output "`n--- log written during this run ---"
if (Test-Path -LiteralPath $log) {
    $all = Get-Content -LiteralPath $log
    $new = $all | Select-Object -Last 8
    $new | ForEach-Object { Write-Output "  $_" }
}

if (-not $p.HasExited) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(6000)) { $p.Kill() } }
Write-Output "done"
