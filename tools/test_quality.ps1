# The Quality row must appear only for streams, and must list the renditions the
# link actually offers rather than a fixed ladder. Checks both, plus that the row
# stays hidden for a local file.
param([string]$Exe = "bin\Debug\net10.0-windows\NexusPlayer.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Set-Location "C:\Users\Sasank\nexus-player-v2"

$script:rid = 300
function Invoke-Mpv([object[]]$c) {
    $script:rid++; $id = $script:rid
    $script:wr.WriteLine((@{command=$c; request_id=$id} | ConvertTo-Json -Compress))
    $end = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $end) {
        $l = $script:rd.ReadLine(); if (-not $l) { continue }
        try { $o = $l | ConvertFrom-Json } catch { continue }
        if ($o.request_id -eq $id) { return $o.data }
    }
    return $null
}

$p = Start-Process -FilePath $Exe -PassThru
for ($i=0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
Start-Sleep -Milliseconds 3000

$script:pipe = New-Object System.IO.Pipes.NamedPipeClientStream '.', "nexus-player-$($p.Id)", 'InOut'
$script:pipe.Connect(5000)
$script:rd = New-Object System.IO.StreamReader $script:pipe
$script:wr = New-Object System.IO.StreamWriter $script:pipe
$script:wr.AutoFlush = $true
[void](Invoke-Mpv @('set_property','mute',$true))

$desktop = [System.Windows.Automation.AutomationElement]::RootElement
function All-Elements {
    $wins = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
    $out = New-Object System.Collections.ArrayList
    foreach ($w in $wins) {
        $es = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)))
        foreach ($e in $es) { [void]$out.Add($e) }
    }
    return $out
}
function Find-Text($pattern) {
    foreach ($e in All-Elements) {
        $t = "$($e.Current.Name) $($e.Current.HelpText)"
        if ($t -match $pattern) { return $e }
    }
    return $null
}

# ---------- 1. local file: Quality must stay hidden ----------
$dir = 'C:\Users\Sasank\Downloads\[FLE] Re ZERO Starting Life in Another World - S03 (WEB 1080p H.264 E-AC-3) [Dual Audio]'
$ep = (Get-ChildItem -LiteralPath $dir -Filter *.mkv | Sort-Object Name)[0].FullName
[void](Invoke-Mpv @('loadfile', $ep, 'replace'))
Start-Sleep -Milliseconds 3500
$gear = Find-Text 'Settings'
$gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1200
$qLocal = Find-Text 'Quality'
Write-Output "local file  -> Quality row present: $($null -ne $qLocal)   (expected: False)"
$gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 700

# ---------- 2. stream: Quality must appear and list real renditions ----------
[void](Invoke-Mpv @('loadfile','https://www.youtube.com/watch?v=aqz-KE-bpKQ','replace'))
Start-Sleep -Milliseconds 7000
$gear = Find-Text 'Settings'
$gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1200
$qStream = Find-Text 'Quality'
Write-Output "stream      -> Quality row present: $($null -ne $qStream)   (expected: True)"

if ($qStream) {
    $qStream.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "opened Quality; waiting for yt-dlp..."
    Start-Sleep -Milliseconds 9000
    Write-Output "rows offered:"
    foreach ($e in All-Elements) {
        $n = $e.Current.Name
        if ($n -match '^\s*(Auto|\d{3,4}p|Checking|Could not|yt-dlp not|Not playing)') {
            Write-Output "   $n"
        }
    }
}

if (-not $p.HasExited) { $script:wr.Dispose(); $script:pipe.Dispose(); [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(6000)) { $p.Kill() } }
Write-Output "done"
