# Does asking for a rendition actually change the video that plays?
#
# The Quality menu closing is not evidence of anything — the bug it was hiding
# was that mpv came back at the same size. So this drives mpv directly and reads
# width/height back over IPC after the reload.
#
# It runs the same check twice, once with mpv auto-loading the user's scripts
# (how the app used to launch) and once with the pinned script set (how it
# launches now). Reporting one number in isolation would not distinguish a fix
# from a test that never exercised the thing it claims to.
#
#   powershell -NoProfile -File tools\test_rendition.ps1
param(
    [string]$Url = 'https://www.youtube.com/watch?v=aqz-KE-bpKQ',
    [int[]]$Heights = @(360, 720),
    # Seconds to dwell after a load before switching. quality-menu.lua
    # fetches the format list in its own yt-dlp subprocess on file-loaded,
    # and only then has the active ids its on_load hook pins. Reloading
    # sooner than that races it and the bug does not reproduce - which is
    # what a first version of this test did, passing in both branches.
    [int]$Dwell = 15,
    [string]$Mpv
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

if (-not $Mpv) {
    $Mpv = 'C:\Users\Sasank\NexusPlayer\mpv.exe'
    if (-not (Test-Path $Mpv)) { $Mpv = (Get-Command mpv.exe -ErrorAction SilentlyContinue).Source }
}
if (-not $Mpv -or -not (Test-Path $Mpv)) { throw "mpv.exe not found; pass -Mpv" }

# The set the app pins. Kept in step with Services/MpvScripts.cs by hand; the
# point of the test is the behaviour, not the list.
$wanted = @('autoload.lua', 'pause_after_chapter.lua', 'SmartCopyPaste.lua')
$scriptsDir = Join-Path $env:APPDATA 'mpv\scripts'

$script:rid = 0
function Send([object]$stream, [object[]]$cmd, [int]$timeoutSec = 20) {
    $script:rid++
    $id = $script:rid
    $stream.Writer.WriteLine((@{ command = $cmd; request_id = $id } | ConvertTo-Json -Compress))
    $end = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $end) {
        $line = $stream.Reader.ReadLine()
        if (-not $line) { continue }
        try { $o = $line | ConvertFrom-Json } catch { continue }
        if ($o.request_id -eq $id) { return $o }
    }
    return $null
}

function Get-Prop([object]$stream, [string]$name) {
    $r = Send $stream @('get_property', $name)
    if ($r -and $r.error -eq 'success') { return $r.data }
    return $null
}

# Wait for a decoded frame, not just for the load to be accepted: width is null
# between loadfile returning and the demuxer reporting the track.
function Wait-Video([object]$stream, [int]$seconds = 45) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        $w = Get-Prop $stream 'width'
        $h = Get-Prop $stream 'height'
        if ($w -and $h -and $w -gt 0) { return @{ W = $w; H = $h } }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Run-Case([string]$label, [bool]$pinScripts) {
    Write-Host ""
    Write-Host "=== $label ==="

    $pipe = "nexus-rendition-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    $args = @(
        "--input-ipc-server=\\.\pipe\$pipe",
        '--idle=yes', '--vo=null', '--ao=null',
        '--msg-level=all=error'
    )
    if ($pinScripts) {
        $args += '--load-scripts=no'
        $loaded = @()
        foreach ($n in $wanted) {
            $f = Join-Path $scriptsDir $n
            if (Test-Path $f) { $args += "--script=$f"; $loaded += $n }
        }
        Write-Host "scripts pinned: $($loaded -join ', ')"
    } else {
        Write-Host "scripts: mpv auto-loads everything in $scriptsDir"
    }

    $proc = Start-Process -FilePath $Mpv -ArgumentList $args -PassThru -WindowStyle Hidden
    try {
        $client = New-Object System.IO.Pipes.NamedPipeClientStream '.', $pipe, 'InOut'
        $client.Connect(10000)
        $stream = @{
            Reader = New-Object System.IO.StreamReader $client
            Writer = New-Object System.IO.StreamWriter $client
        }
        $stream.Writer.AutoFlush = $true

        $results = @()
        foreach ($h in $Heights) {
            $fmt = "bestvideo[height<=$h]+bestaudio/best[height<=$h]/best"
            [void](Send $stream @('set_property', 'ytdl-format', $fmt))
            [void](Send $stream @('loadfile', $Url, 'replace') 30)
            $v = Wait-Video $stream
            $got = if ($v) { "$($v.W)x$($v.H)" } else { 'no video' }
            # "at most the cap" is not the assertion: 360p satisfies a 720p cap,
            # so the failing case scored OK in a first version of this. What
            # matters is whether the format we set is still the one in force -
            # when quality-menu overrides it, this reads back as itags.
            $eff = Get-Prop $stream 'ytdl-format'
            $ok = if ($eff -eq $fmt) { 'ours' } else { 'OVERRIDDEN' }
            Write-Host ("  asked <= {0,4}p  ->  {1,-12} {2}" -f $h, $got, $ok)
            Write-Host ("     ytdl-format now: {0}" -f $eff)
            $results += [pscustomobject]@{ Asked = $h; Height = if ($v) { $v.H } else { 0 } }
            Start-Sleep -Seconds $Dwell
        }

        $distinct = ($results | Select-Object -ExpandProperty Height | Sort-Object -Unique).Count
        if ($distinct -gt 1) {
            Write-Host "  => the rendition changed"
        } else {
            Write-Host "  => the rendition did NOT change (every request returned the same size)"
        }
        return $results
    }
    finally {
        if ($client) { try { $client.Dispose() } catch {} }
        if (-not $proc.HasExited) { $proc.Kill(); [void]$proc.WaitForExit(5000) }
    }
}

Write-Output "mpv:  $Mpv"
Write-Output "url:  $Url"

$before = Run-Case 'auto-loading the user scripts (how the app used to launch)' $false
$after = Run-Case 'pinned script set, no quality-menu.lua (how it launches now)' $true

Write-Output ""
Write-Output "----------------------------------------------------------------"
foreach ($i in 0..($Heights.Count - 1)) {
    Write-Output ("asked <= {0,4}p   auto-load: {1,5}   pinned: {2,5}" -f `
        $Heights[$i], $before[$i].Height, $after[$i].Height)
}
Write-Output "done"
