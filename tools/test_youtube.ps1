# Does playing a YouTube link actually work from inside the app?
#
# Drives the real UI path — open the URL panel, type into the box, press play —
# rather than shortcutting via IPC, because the interesting failures live in
# that path (focus handling, the yt-dlp handoff, the "could not play" guard).
# The verdict comes from mpv's own properties.
param(
    [string]$Exe = "bin\Debug\net10.0-windows\NexusPlayer.exe",
    [string]$Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Set-Location "C:\Users\Sasank\nexus-player-v2"

$script:rid = 700
function Invoke-Mpv([object[]]$c) {
    $script:rid++; $id = $script:rid
    $script:wr.WriteLine((@{command=$c; request_id=$id} | ConvertTo-Json -Compress))
    $end = (Get-Date).AddSeconds(5)
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
Write-Output "pid $($p.Id)"

$script:pipe = New-Object System.IO.Pipes.NamedPipeClientStream '.', "nexus-player-$($p.Id)", 'InOut'
$script:pipe.Connect(5000)
$script:rd = New-Object System.IO.StreamReader $script:pipe
$script:wr = New-Object System.IO.StreamWriter $script:pipe
$script:wr.AutoFlush = $true
[void](Invoke-Mpv @('set_property','mute',$true))
[void](Invoke-Mpv @('stop'))
Start-Sleep -Milliseconds 800

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
function Buttons { $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button))) }
function Find-Btn($pattern) {
    foreach ($b in Buttons) { if ("$($b.Current.HelpText)$($b.Current.Name)" -match $pattern) { return $b } }
    return $null
}

$open = Find-Btn 'Open URL'
if (-not $open) {
    Write-Output "FAIL: no 'Open URL' button found - is the sidebar collapsed?"
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(5000)) { $p.Kill() } }
    exit 1
}
$open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 900
Write-Output "URL panel opened"

$box = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)))
if (-not $box) {
    Write-Output "FAIL: URL text box not found"
} else {
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Url)
    Write-Output "typed: $Url"
    Start-Sleep -Milliseconds 500

    $play = Find-Btn 'Play URL'
    if (-not $play) {
        Write-Output "FAIL: 'Play URL' button not found"
    } else {
        $play.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output "pressed Play URL; waiting for yt-dlp to resolve..."

        $ok = $false
        for ($i = 0; $i -lt 30; $i++) {
            Start-Sleep -Milliseconds 1000
            $p.Refresh()
            if ($p.HasExited) { Write-Output "  process DIED while resolving"; break }
            $dur = Invoke-Mpv @('get_property','duration')
            if ($dur) {
                $title  = Invoke-Mpv @('get_property','media-title')
                $path   = Invoke-Mpv @('get_property','path')
                $w      = Invoke-Mpv @('get_property','width')
                $h      = Invoke-Mpv @('get_property','height')
                $pos    = Invoke-Mpv @('get_property','time-pos')
                Write-Output ""
                Write-Output "  RESOLVED after ~$($i+1)s"
                Write-Output "  title    : $title"
                Write-Output "  duration : $dur s"
                Write-Output "  video    : ${w}x${h}"
                Write-Output "  time-pos : $pos"
                Write-Output "  path     : $path"
                $ok = $true
                break
            }
        }
        # Resolving is not playing. A 403 on the media stream still lets duration
        # come back from metadata while no frames ever arrive, which is exactly
        # how "YouTube is broken" looks from the outside. Demand real progress.
        $played = $false
        if ($ok) {
            [void](Invoke-Mpv @('set_property','pause',$false))
            Start-Sleep -Milliseconds 1500
            $p1 = [double](Invoke-Mpv @('get_property','time-pos'))
            Start-Sleep -Milliseconds 4000
            $p2 = [double](Invoke-Mpv @('get_property','time-pos'))
            $cache = Invoke-Mpv @('get_property','demuxer-cache-time')
            $drop  = Invoke-Mpv @('get_property','frame-drop-count')
            Write-Output ""
            Write-Output ("  playback  : {0:N2}s -> {1:N2}s over 4s wall clock" -f $p1, $p2)
            Write-Output "  cache     : $cache"
            Write-Output "  drops     : $drop"
            $played = ($p2 - $p1) -gt 1.5
        }
        Write-Output ""
        Write-Output "  resolved  : $(if ($ok) {'yes'} else {'no'})"
        Write-Output "  PLAYING   : $(if ($played) {'yes'} else {'NO - stream resolved but no frames advanced'})"
        Write-Output "YOUTUBE: $(if ($played) {'WORKS'} else {'BROKEN'})"
    }
}

Write-Output "`n--- mpv output mentioning ytdl/youtube/error ---"
$mpvlog = "$env:APPDATA\NexusPlayerV2\mpv.log"
if (Test-Path -LiteralPath $mpvlog) {
    Get-Content -LiteralPath $mpvlog | Select-String -Pattern 'ytdl|youtube|error|failed' |
        Select-Object -Last 8 | ForEach-Object { Write-Output "  $_" }
}

$p.Refresh()
if (-not $p.HasExited) { $script:wr.Dispose(); $script:pipe.Dispose(); [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(6000)) { $p.Kill() } }
Write-Output "done"
