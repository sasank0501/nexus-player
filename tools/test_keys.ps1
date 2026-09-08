# Verifies keyboard shortcuts end to end: send a real key, then ask mpv what
# changed. This project's history has synthetic input "disproving" working code
# twice, so every result here is checked against an mpv property, and the
# foreground window is confirmed before any key is sent.
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public class K {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct IN { public uint type; public KI ki; public int p1, p2; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, IN[] a, int cb);
  public static void Key(ushort vk) {
    var a = new IN[2];
    a[0].type = 1; a[0].ki = new KI { vk = vk };
    a[1].type = 1; a[1].ki = new KI { vk = vk, flags = 2 }; // KEYEVENTF_KEYUP
    SendInput(2, a, Marshal.SizeOf(typeof(IN)));
  }
}
'@

$script:pipe = $null
$script:rd = $null
$script:wr = $null
$script:rid = 100

function Connect-Mpv($pipeName) {
    $script:pipe = New-Object System.IO.Pipes.NamedPipeClientStream '.', $pipeName, 'InOut'
    $script:pipe.Connect(5000)
    $script:rd = New-Object System.IO.StreamReader $script:pipe
    $script:wr = New-Object System.IO.StreamWriter $script:pipe
    $script:wr.AutoFlush = $true
}

# writes a command and drains lines until the matching reply comes back,
# ignoring the property-change events flowing on the same pipe
function Invoke-Mpv([object[]] $cmd) {
    $script:rid++
    $id = $script:rid
    $script:wr.WriteLine((@{ command = $cmd; request_id = $id } | ConvertTo-Json -Compress))
    $deadline = (Get-Date).AddSeconds(4)
    while ((Get-Date) -lt $deadline) {
        $line = $script:rd.ReadLine()
        if (-not $line) { continue }
        try { $o = $line | ConvertFrom-Json } catch { continue }
        if ($o.request_id -eq $id) { return $o.data }
    }
    return $null
}

function Get-Prop($name) { Invoke-Mpv @('get_property', $name) }
function Set-Prop($name, $value) { [void](Invoke-Mpv @('set_property', $name, $value)) }

$results = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    [void]$results.Add([pscustomobject]@{
        Shortcut = $name
        Result   = $(if ($ok) { 'PASS' } else { 'FAIL' })
        Detail   = $detail
    })
}

# ---------------------------------------------------------------- launch
Set-Location "C:\Users\Sasank\nexus-player-v2"
$proc = Start-Process -FilePath "bin\Debug\net10.0-windows\NexusPlayer.exe" -PassThru
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { break }
}
Start-Sleep -Milliseconds 3500
$hwnd = $proc.MainWindowHandle
Write-Output "v2 pid $($proc.Id) hwnd $hwnd"

Connect-Mpv "nexus-player-$($proc.Id)"
Set-Prop 'mute' $true

$dir = 'C:\Users\Sasank\Downloads\[FLE] Re ZERO Starting Life in Another World - S03 (WEB 1080p H.264 E-AC-3) [Dual Audio]'
$ep = (Get-ChildItem -LiteralPath $dir -Filter *.mkv | Sort-Object Name)[0].FullName
[void](Invoke-Mpv @('loadfile', $ep, 'replace'))
Start-Sleep -Milliseconds 3000
Set-Prop 'pause' $false
Set-Prop 'time-pos' 300.0
Start-Sleep -Milliseconds 1200

# ------------------------------------------------------- confirm foreground
[void][K]::ShowWindow($hwnd, 5)
[void][K]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 700
$fg = [K]::GetForegroundWindow()
if ($fg -ne $hwnd) {
    Write-Output "ABORT: player is not foreground (fg=$fg want=$hwnd). Key results would be meaningless."
    exit 2
}
Write-Output "foreground confirmed"
Write-Output ''

# ------------------------------------------------------------------- tests
Set-Prop 'pause' $false; Start-Sleep -Milliseconds 400
$before = Get-Prop 'pause'
[K]::Key(0x20); Start-Sleep -Milliseconds 700          # Space
$after = Get-Prop 'pause'
Check 'Space (play/pause)' ($before -ne $after) "pause $before -> $after"

Set-Prop 'pause' $true; Start-Sleep -Milliseconds 300
Set-Prop 'time-pos' 300.0; Start-Sleep -Milliseconds 800
$t0 = Get-Prop 'time-pos'
[K]::Key(0x27); Start-Sleep -Milliseconds 900          # Right
$t1 = Get-Prop 'time-pos'
Check 'Right (seek +5s)' (($t1 - $t0) -gt 3 -and ($t1 - $t0) -lt 8) ("{0:N1} -> {1:N1}" -f $t0, $t1)

[K]::Key(0x25); Start-Sleep -Milliseconds 900          # Left
$t2 = Get-Prop 'time-pos'
Check 'Left (seek -5s)' (($t1 - $t2) -gt 3 -and ($t1 - $t2) -lt 8) ("{0:N1} -> {1:N1}" -f $t1, $t2)

Set-Prop 'volume' 40.0; Start-Sleep -Milliseconds 400
$v0 = Get-Prop 'volume'
[K]::Key(0x26); Start-Sleep -Milliseconds 700          # Up
$v1 = Get-Prop 'volume'
Check 'Up (volume +5)' (($v1 - $v0) -ge 4) "$v0 -> $v1"

[K]::Key(0x28); Start-Sleep -Milliseconds 700          # Down
$v2 = Get-Prop 'volume'
Check 'Down (volume -5)' (($v1 - $v2) -ge 4) "$v1 -> $v2"

$m0 = Get-Prop 'mute'
[K]::Key(0x4D); Start-Sleep -Milliseconds 700          # M
$m1 = Get-Prop 'mute'
Check 'M (mute)' ($m0 -ne $m1) "mute $m0 -> $m1"
Set-Prop 'mute' $true

Set-Prop 'speed' 1.0; Start-Sleep -Milliseconds 400
[K]::Key(0xDB); Start-Sleep -Milliseconds 700          # [  (forwarded to mpv)
$s1 = Get-Prop 'speed'
Check '[ (speed down, via mpv)' ($s1 -lt 0.99) ("speed 1.00 -> {0:N2}" -f $s1)

[K]::Key(0xDD); Start-Sleep -Milliseconds 700          # ]
$s2 = Get-Prop 'speed'
Check '] (speed up, via mpv)' ($s2 -gt $s1) ("{0:N2} -> {1:N2}" -f $s1, $s2)
Set-Prop 'speed' 1.0

# screenshot: proves both the forwarded key and the NexuSS folder wiring
$shotDir = Get-Prop 'screenshot-directory'
Write-Output "screenshot-directory reported by mpv: $shotDir"
$countBefore = 0
if ($shotDir -and (Test-Path -LiteralPath $shotDir)) {
    $countBefore = (Get-ChildItem -LiteralPath $shotDir -File -ErrorAction SilentlyContinue).Count
}
[K]::Key(0x53); Start-Sleep -Milliseconds 2000          # S
$countAfter = 0
if ($shotDir -and (Test-Path -LiteralPath $shotDir)) {
    $countAfter = (Get-ChildItem -LiteralPath $shotDir -File -ErrorAction SilentlyContinue).Count
}
$inNexuSS = $shotDir -and ($shotDir -replace '/', '\') -like '*\NexuSS\*'
Check 'S (screenshot written)' ($countAfter -gt $countBefore) "$countBefore -> $countAfter file(s)"
Check 'screenshot folder is NexuSS' $inNexuSS "$shotDir"

Write-Output ''
$results | Format-Table -AutoSize | Out-String | Write-Output
$fail = ($results | Where-Object Result -eq 'FAIL').Count
Write-Output "$($results.Count - $fail)/$($results.Count) passed"

$script:wr.Dispose(); $script:pipe.Dispose()
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(6000)) { $proc.Kill() }
Write-Output "player closed"
