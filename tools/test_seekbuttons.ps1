# Do the on-screen back-10 / forward-10 buttons actually seek?
#
# Invoked through UI Automation so a negative result means the handler is wrong,
# not that a synthetic click missed. The effect is measured from mpv's own
# time-pos, not from the UI.
param([string]$Exe = "bin\Debug\net10.0-windows\NexusPlayer.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Set-Location "C:\Users\Sasank\nexus-player-v2"

$script:rid = 900
function Invoke-Mpv([object[]]$c) {
    $script:rid++; $id = $script:rid
    $script:wr.WriteLine((@{command=$c; request_id=$id} | ConvertTo-Json -Compress))
    $end = (Get-Date).AddSeconds(4)
    while ((Get-Date) -lt $end) {
        $l = $script:rd.ReadLine(); if (-not $l) { continue }
        try { $o = $l | ConvertFrom-Json } catch { continue }
        if ($o.request_id -eq $id) { return $o.data }
    }
    return $null
}
function Pos { [double](Invoke-Mpv @('get_property','time-pos')) }

$p = Start-Process -FilePath $Exe -PassThru
for ($i=0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
Start-Sleep -Milliseconds 3000

$script:pipe = New-Object System.IO.Pipes.NamedPipeClientStream '.', "nexus-player-$($p.Id)", 'InOut'
$script:pipe.Connect(5000)
$script:rd = New-Object System.IO.StreamReader $script:pipe
$script:wr = New-Object System.IO.StreamWriter $script:pipe
$script:wr.AutoFlush = $true

[void](Invoke-Mpv @('set_property','mute',$true))
$dir = 'C:\Users\Sasank\Downloads\[FLE] Re ZERO Starting Life in Another World - S03 (WEB 1080p H.264 E-AC-3) [Dual Audio]'
$ep = (Get-ChildItem -LiteralPath $dir -Filter *.mkv | Sort-Object Name)[0].FullName
[void](Invoke-Mpv @('loadfile', $ep, 'replace'))
Start-Sleep -Milliseconds 3000
[void](Invoke-Mpv @('set_property','pause',$true))
Start-Sleep -Milliseconds 500

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
# the control bar lives in the overlay: a separate top-level window of this process
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))

$all = New-Object System.Collections.ArrayList
foreach ($w in $wins) {
    $bs = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    foreach ($b in $bs) { [void]$all.Add($b) }
}
Write-Output "buttons across all windows of the process: $($all.Count)"

function Find-Btn($pattern) {
    foreach ($b in $all) { if ("$($b.Current.HelpText)$($b.Current.Name)" -match $pattern) { return $b } }
    return $null
}

$results = @()
foreach ($case in @(@{Name='Back 10';   Pattern='Back 10';    Sign=-1},
                    @{Name='Forward 10';Pattern='Forward 10'; Sign=1})) {
    $btn = Find-Btn $case.Pattern
    if (-not $btn) { $results += "  $($case.Name): BUTTON NOT FOUND"; continue }
    [void](Invoke-Mpv @('set_property','time-pos',400.0)); Start-Sleep -Milliseconds 900
    $a = Pos
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1200
    $b = Pos
    $delta = $b - $a
    $ok = ($case.Sign -lt 0 -and $delta -lt -5) -or ($case.Sign -gt 0 -and $delta -gt 5)
    $results += ("  {0}: {1}  ({2:N1} -> {3:N1}, delta {4:N1}s)" -f $case.Name, $(if($ok){'WORKS'}else{'DEAD'}), $a, $b, $delta)
}

Write-Output "`n=== seek buttons ==="
$results | ForEach-Object { Write-Output $_ }

if (-not $p.HasExited) { $script:wr.Dispose(); $script:pipe.Dispose(); [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(6000)) { $p.Kill() } }
Write-Output "done"
