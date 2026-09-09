# Why do the arrow keys fail in real use when the key test passes?
#
# The test foregrounds a freshly launched window and immediately sends keys.
# Real use involves CLICKING first — on the video, on a control, on the queue —
# and WPF keyboard focus follows those clicks. This reproduces that, and reports
# where focus actually is at each step rather than assuming.
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public class D {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, char[] s, int n);
  [DllImport("user32.dll")] public static extern int GetClassNameW(IntPtr h, char[] s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref G g);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
  [StructLayout(LayoutKind.Sequential)] public struct G {
    public int cbSize; public uint flags;
    public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
    public R rcCaret;
  }
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint d, f, t; public IntPtr e; }
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, sc; public uint f, t; public IntPtr e; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] public struct IN { public uint type; public U u; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, IN[] a, int cb);

  static void Norm(int x, int y, out int nx, out int ny) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
    int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    nx = (int)((x - vx) * 65535.0 / (vw - 1));
    ny = (int)((y - vy) * 65535.0 / (vh - 1));
  }
  public static void Move(int x, int y) {
    int nx, ny; Norm(x, y, out nx, out ny);
    var a = new IN[1]; a[0].type = 0;
    a[0].u.mi = new MI { dx = nx, dy = ny, f = 0x0001 | 0x8000 | 0x4000 };
    SendInput(1, a, Marshal.SizeOf(typeof(IN)));
  }
  public static void Click(int x, int y) {
    Move(x, y);
    var a = new IN[2];
    a[0].type = 0; a[0].u.mi = new MI { f = 0x0002 };  // LEFTDOWN
    a[1].type = 0; a[1].u.mi = new MI { f = 0x0004 };  // LEFTUP
    SendInput(2, a, Marshal.SizeOf(typeof(IN)));
  }
  public static void Key(ushort vk) {
    var a = new IN[2];
    a[0].type = 1; a[0].u.ki = new KI { vk = vk };
    a[1].type = 1; a[1].u.ki = new KI { vk = vk, f = 2 };
    SendInput(2, a, Marshal.SizeOf(typeof(IN)));
  }
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "(none)";
    var t = new char[256]; int n = GetWindowTextW(h, t, 256);
    var c = new char[256]; int m = GetClassNameW(h, c, 256);
    uint pid; GetWindowThreadProcessId(h, out pid);
    return string.Format("hwnd={0} pid={1} class='{2}' title='{3}'", h, pid, new string(c,0,m), new string(t,0,n).Trim());
  }
  public static IntPtr FocusHwnd(IntPtr anyWindowOfThread) {
    uint pid; uint tid = GetWindowThreadProcessId(anyWindowOfThread, out pid);
    var g = new G(); g.cbSize = Marshal.SizeOf(typeof(G));
    return GetGUIThreadInfo(tid, ref g) ? g.hwndFocus : IntPtr.Zero;
  }
}
'@

# ---------- mpv IPC ----------
$script:rid = 500
function Connect-Mpv($n) {
    $script:pipe = New-Object System.IO.Pipes.NamedPipeClientStream '.', $n, 'InOut'
    $script:pipe.Connect(5000)
    $script:rd = New-Object System.IO.StreamReader $script:pipe
    $script:wr = New-Object System.IO.StreamWriter $script:pipe
    $script:wr.AutoFlush = $true
}
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

function Report($label, $mainHwnd) {
    $fg = [D]::GetForegroundWindow()
    $fc = [D]::FocusHwnd($mainHwnd)
    Write-Output "  $label"
    Write-Output "    foreground : $([D]::Describe($fg))"
    Write-Output "    kbd focus  : $([D]::Describe($fc))"
    Write-Output "    focus is main window: $($fc -eq $mainHwnd)"
}

function Try-Arrow($label, $mainHwnd) {
    $a = Pos
    [D]::Key(0x27)                 # Right
    Start-Sleep -Milliseconds 900
    $b = Pos
    $moved = [math]::Abs($b - $a) -gt 2
    Write-Output ("    Right arrow -> {0:N1} to {1:N1}  {2}" -f $a, $b, $(if ($moved) {'WORKS'} else {'DEAD'}))
    return $moved
}

# ---------- run ----------
Set-Location "C:\Users\Sasank\nexus-player-v2"
$p = Start-Process -FilePath "bin\Debug\net10.0-windows\NexusPlayer.exe" -PassThru
for ($i=0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
Start-Sleep -Milliseconds 3500
$main = $p.MainWindowHandle
Write-Output "player pid $($p.Id), main $([D]::Describe($main))`n"

Connect-Mpv "nexus-player-$($p.Id)"
[void](Invoke-Mpv @('set_property','mute',$true))
$dir = 'C:\Users\Sasank\Downloads\[FLE] Re ZERO Starting Life in Another World - S03 (WEB 1080p H.264 E-AC-3) [Dual Audio]'
$ep = (Get-ChildItem -LiteralPath $dir -Filter *.mkv | Sort-Object Name)[0].FullName
[void](Invoke-Mpv @('loadfile', $ep, 'replace'))
Start-Sleep -Milliseconds 3000
[void](Invoke-Mpv @('set_property','pause',$true))
[void](Invoke-Mpv @('set_property','time-pos',300.0))
Start-Sleep -Milliseconds 1000

$r = New-Object D+R
[void][D]::GetWindowRect($main, [ref]$r)
Write-Output "window rect L=$($r.L) T=$($r.T) R=$($r.Rr) B=$($r.B)"
$cx = [int](($r.L + $r.Rr) / 2)
$cy = [int]($r.T + ($r.B - $r.T) * 0.40)   # upper-middle of the video, away from the control bar

Write-Output "`n=== STEP 1: programmatic foreground, nothing clicked (what the passing test did) ==="
[void][D]::SetForegroundWindow($main)
Start-Sleep -Milliseconds 800
Report 'state' $main
$s1 = Try-Arrow 'step1' $main

Write-Output "`n=== STEP 2: after clicking the VIDEO (what you actually do) ==="
[void](Invoke-Mpv @('set_property','time-pos',300.0)); Start-Sleep -Milliseconds 800
[D]::Click($cx, $cy)
Start-Sleep -Milliseconds 1200
Report 'state' $main
$s2 = Try-Arrow 'step2' $main

Write-Output "`n=== STEP 3: after clicking a QUEUE item in the sidebar ==="
$sidebarX = [int]($r.Rr - 150)
$sidebarY = [int]($r.T + 120)
[void](Invoke-Mpv @('set_property','time-pos',300.0)); Start-Sleep -Milliseconds 800
[D]::Click($sidebarX, $sidebarY)
Start-Sleep -Milliseconds 1200
Report 'state' $main
$s3 = Try-Arrow 'step3' $main

Write-Output "`n================ SUMMARY ================"
Write-Output "  fresh foreground, no click : $(if ($s1) {'WORKS'} else {'DEAD'})"
Write-Output "  after clicking the video   : $(if ($s2) {'WORKS'} else {'DEAD'})"
Write-Output "  after clicking the sidebar : $(if ($s3) {'WORKS'} else {'DEAD'})"

$script:wr.Dispose(); $script:pipe.Dispose()
[void]$p.CloseMainWindow()
if (-not $p.WaitForExit(6000)) { $p.Kill() }
Write-Output "player closed"
