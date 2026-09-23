# Verifies the auto display-detection feature (Services/DisplayProfile.cs,
# wired into MainWindow.StartMpv/ToggleFullscreen) end to end, on whatever
# machine this runs on - written so it can be handed to someone else's laptop
# via -Exe pointing at a copy of the published portable folder.
#
# Every check here is against ground truth, not against the app's own
# internal state: the expected d3d11-exclusive-fs/target-colorspace-hint
# values are computed independently via the same Win32 QueryDisplayConfig
# API the app uses (not by importing the app's C# logic), and the actual
# mpv command line is read from the live process via WMI. This project's
# history has synthetic input "disproving" working code before (see
# test_keys.ps1) - PostMessage-based key injection specifically proved
# unreliable multiple times debugging this exact feature, so fullscreen is
# toggled with SendInput (real OS-level input) and only after confirming
# this window is genuinely foreground, matching test_keys.ps1's convention.
#
#   powershell -File tools\test_display_profile.ps1
#   powershell -File tools\test_display_profile.ps1 -Exe "D:\NexusPlayer\NexusPlayer.exe"
param(
    [string]$Exe = "bin\Debug\net10.0-windows\NexusPlayer.exe",
    [string]$VideoPath = ""   # optional: a local file to load for the fullscreen check; idle mpv works too
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
# resolve the repo from this script location, so moving the repo does not
# break the tests the way a hardcoded path did
Set-Location (Split-Path -Parent $PSScriptRoot)

Add-Type @'
using System; using System.Runtime.InteropServices;
public class DP {
    // ---- ground-truth display detection (independent of the app) ----
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(int flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] public static extern int QueryDisplayConfig(int flags, ref uint pathCount, [Out] PATH_INFO[] paths, ref uint modeCount, [Out] MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME r);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME r);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO r);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // ---- geometry (DPI-aware) ----
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT val, int size);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo2(IntPtr hMonitor, ref MONITORINFO mi);

    // ---- real keyboard input, not PostMessage ----
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct IN { public uint type; public KI ki; public int p1, p2; }
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, IN[] a, int cb);
    public static void Key(ushort vk) {
        var a = new IN[2];
        a[0].type = 1; a[0].ki = new KI { vk = vk };
        a[1].type = 1; a[1].ki = new KI { vk = vk, flags = 2 };
        SendInput(2, a, Marshal.SizeOf(typeof(IN)));
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] public struct PATH_SOURCE { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET { public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling, refreshNum, refreshDen, scanLineOrdering; public int targetAvailable; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct PATH_INFO { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct MODE_INFO { [FieldOffset(0)] public uint infoType; [FieldOffset(4)] public uint id; [FieldOffset(8)] public LUID adapterId; }
    [StructLayout(LayoutKind.Sequential)] public struct HEADER { public int type, size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_NAME { public HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_NAME {
        public HEADER header; public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
    [StructLayout(LayoutKind.Sequential)] public struct COLOR_INFO { public HEADER header; public uint value, colorEncoding, bitsPerColorChannel; }
}
'@

[void][DP]::SetProcessDpiAwarenessContext([IntPtr](-4))  # PER_MONITOR_AWARE_V2; must be set before any geometry/monitor call

# ground truth: is the given monitor an internal/eDP panel, and is it HDR-capable+enabled?
# independent reimplementation of Services/DisplayProfile.cs's logic, not a call into it -
# the point is to check the app against the real OS state, not against itself.
function Get-DisplayGroundTruth([IntPtr]$hMonitor) {
    $mi = New-Object DP+MONITORINFOEX; $mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][DP+MONITORINFOEX])
    if (-not [DP]::GetMonitorInfo($hMonitor, [ref]$mi)) { return $null }

    $pathCount = 0; $modeCount = 0
    if ([DP]::GetDisplayConfigBufferSizes(2, [ref]$pathCount, [ref]$modeCount) -ne 0) { return $null }
    $paths = New-Object 'DP+PATH_INFO[]' $pathCount
    $modes = New-Object 'DP+MODE_INFO[]' $modeCount
    if ([DP]::QueryDisplayConfig(2, [ref]$pathCount, $paths, [ref]$modeCount, $modes, [IntPtr]::Zero) -ne 0) { return $null }

    for ($i = 0; $i -lt $pathCount; $i++) {
        $path = $paths[$i]

        $srcHeader = New-Object DP+HEADER
        $srcHeader.type = 1; $srcHeader.size = [Runtime.InteropServices.Marshal]::SizeOf([type][DP+SOURCE_NAME])
        $srcHeader.adapterId = $path.sourceInfo.adapterId; $srcHeader.id = $path.sourceInfo.id
        $src = New-Object DP+SOURCE_NAME
        $src.header = $srcHeader
        $srcRc = [DP]::DisplayConfigGetDeviceInfo([ref]$src)
        if ($srcRc -ne 0) { continue }
        if ($src.viewGdiDeviceName -ne $mi.szDevice) { continue }

        $tgtHeader = New-Object DP+HEADER
        $tgtHeader.type = 2; $tgtHeader.size = [Runtime.InteropServices.Marshal]::SizeOf([type][DP+TARGET_NAME])
        $tgtHeader.adapterId = $path.targetInfo.adapterId; $tgtHeader.id = $path.targetInfo.id
        $tgt = New-Object DP+TARGET_NAME
        $tgt.header = $tgtHeader
        $tgtRc = [DP]::DisplayConfigGetDeviceInfo([ref]$tgt)
        $isInternal = ($tgtRc -eq 0) -and ($tgt.outputTechnology -eq 0x80000000 -or $tgt.outputTechnology -eq 11)

        $colHeader = New-Object DP+HEADER
        $colHeader.type = 9; $colHeader.size = [Runtime.InteropServices.Marshal]::SizeOf([type][DP+COLOR_INFO])
        $colHeader.adapterId = $path.targetInfo.adapterId; $colHeader.id = $path.targetInfo.id
        $col = New-Object DP+COLOR_INFO
        $col.header = $colHeader
        $hdrCapable = $false; $hdrEnabled = $false
        if ([DP]::DisplayConfigGetDeviceInfo([ref]$col) -eq 0) {
            $hdrCapable = (($col.value -band 0x1) -ne 0)
            $hdrEnabled = (($col.value -band 0x2) -ne 0)
        }
        return [pscustomobject]@{ IsInternal = $isInternal; HdrCapable = $hdrCapable; HdrEnabled = $hdrEnabled; OutputTechnology = ('0x{0:X8}' -f $tgt.outputTechnology) }
    }
    return $null
}

$results = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    [void]$results.Add([pscustomobject]@{ Test = $name; Result = $(if ($ok) {'PASS'} else {'FAIL'}); Detail = $detail })
}

function Start-Player {
    $proc = Start-Process -FilePath $Exe -PassThru
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; $proc.Refresh(); if ($proc.MainWindowHandle -ne 0) { break } }
    Start-Sleep -Milliseconds 3000
    return $proc
}
function Stop-Player($proc) {
    try {
        [void]$proc.CloseMainWindow()
        if (-not $proc.WaitForExit(6000)) { $proc.Kill() }
    } catch { }
}
function Get-MpvCommandLine($proc) {
    for ($i = 0; $i -lt 20; $i++) {
        $mpv = Get-CimInstance Win32_Process -Filter "Name = 'mpv.exe'" |
            Where-Object { $_.CreationDate -ge $proc.StartTime } | Sort-Object CreationDate -Descending | Select-Object -First 1
        if ($mpv) { return $mpv.CommandLine }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

$settingsFile = Join-Path $env:APPDATA 'NexusPlayerV2\settings.json'
$hadSettings = Test-Path -LiteralPath $settingsFile
$originalSettings = if ($hadSettings) { Get-Content -LiteralPath $settingsFile -Raw } else { $null }
function Set-AutoSwitch([bool]$value) {
    $obj = if ($hadSettings -or (Test-Path -LiteralPath $settingsFile)) {
        Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    } else {
        [pscustomobject]@{}
    }
    $obj | Add-Member -NotePropertyName AutoSwitchDisplayProfile -NotePropertyValue $value -Force
    New-Item -ItemType Directory -Force -Path (Split-Path $settingsFile) | Out-Null
    ($obj | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $settingsFile
}
function Restore-Settings {
    if ($hadSettings) { Set-Content -LiteralPath $settingsFile -Value $originalSettings }
    elseif (Test-Path -LiteralPath $settingsFile) { Remove-Item -LiteralPath $settingsFile -Force }
}

Write-Output "=== ground truth (primary monitor) ==="
# resolve primary monitor the same way the app does: nearest monitor to a
# window at the origin, which for a first launch is the primary monitor
$truth = Get-DisplayGroundTruth ([DP]::MonitorFromWindow([IntPtr]::Zero, 1))  # MONITOR_DEFAULTTOPRIMARY
if (-not $truth) { Write-Output "FATAL: could not read ground truth via QueryDisplayConfig - aborting"; exit 1 }
Write-Output ("internal panel: {0}  (outputTechnology {1})" -f $truth.IsInternal, $truth.OutputTechnology)
Write-Output ("HDR capable: {0}, HDR enabled: {1}" -f $truth.HdrCapable, $truth.HdrEnabled)
Write-Output ""

try {
    # ---------------------------------------------------- AutoSwitch = true
    Set-AutoSwitch $true
    $proc = Start-Player
    Write-Output "player pid $($proc.Id)"
    $cmd = Get-MpvCommandLine $proc
    if (-not $cmd) {
        Check 'mpv launched' $false 'no mpv.exe process found'
    } else {
        $wantExclusive = if ($truth.IsInternal) { 'no' } else { 'yes' }
        $wantColorspace = if ($truth.HdrCapable -and $truth.HdrEnabled) { 'yes' } else { 'no' }
        $hasExclusive = $cmd -match "--d3d11-exclusive-fs=$wantExclusive"
        $hasColorspace = $cmd -match "--target-colorspace-hint=$wantColorspace"
        Check 'AutoSwitch=on: d3d11-exclusive-fs matches ground truth' $hasExclusive "want --d3d11-exclusive-fs=$wantExclusive in: $cmd"
        Check 'AutoSwitch=on: target-colorspace-hint matches ground truth' $hasColorspace "want --target-colorspace-hint=$wantColorspace in: $cmd"
    }

    # -------------------------------------------- fullscreen pixel coverage
    $hwnd = $proc.MainWindowHandle
    [void][DP]::ShowWindow($hwnd, 5)
    [void][DP]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 500
    if ([DP]::GetForegroundWindow() -ne $hwnd) {
        Check 'fullscreen coverage' $false 'player never became foreground; skipped (result would be meaningless)'
    } else {
        [DP]::Key(0x46)  # F
        Start-Sleep -Milliseconds 900

        $wr = New-Object DP+RECT; [void][DP]::GetWindowRect($hwnd, [ref]$wr)
        $efb = New-Object DP+RECT; [void][DP]::DwmGetWindowAttribute($hwnd, 9, [ref]$efb, 16)
        $mon = [DP]::MonitorFromWindow($hwnd, 2)
        $mi = New-Object DP+MONITORINFO; $mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][DP+MONITORINFO])
        [void][DP]::GetMonitorInfo2($mon, [ref]$mi)

        $matches = ($efb.Left -eq $mi.rcMonitor.Left) -and ($efb.Top -eq $mi.rcMonitor.Top) -and
                   ($efb.Right -eq $mi.rcMonitor.Right) -and ($efb.Bottom -eq $mi.rcMonitor.Bottom)
        Check 'fullscreen: window exactly covers monitor' $matches `
            ("window [{0},{1},{2},{3}] vs monitor [{4},{5},{6},{7}]" -f $efb.Left,$efb.Top,$efb.Right,$efb.Bottom,$mi.rcMonitor.Left,$mi.rcMonitor.Top,$mi.rcMonitor.Right,$mi.rcMonitor.Bottom)

        # ---------------------------------- shortcuts overlay: no overlapping text
        [DP]::Key(0xBF)  # '/' (OemQuestion) - opens the Keyboard Shortcuts overlay
        Start-Sleep -Milliseconds 700
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
        $texts = New-Object System.Collections.ArrayList
        foreach ($w in $wins) {
            $es = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Text)))
            foreach ($e in $es) { [void]$texts.Add($e.Current.BoundingRectangle) }
        }
        $overlap = $false; $overlapDetail = ''
        for ($i = 0; $i -lt $texts.Count -and -not $overlap; $i++) {
            for ($j = $i + 1; $j -lt $texts.Count; $j++) {
                $a = $texts[$i]; $b = $texts[$j]
                if ($a.Width -le 0 -or $b.Width -le 0) { continue }
                $ix = [Math]::Max(0, [Math]::Min($a.Right,$b.Right) - [Math]::Max($a.Left,$b.Left))
                $iy = [Math]::Max(0, [Math]::Min($a.Bottom,$b.Bottom) - [Math]::Max($a.Top,$b.Top))
                if ($ix -gt 2 -and $iy -gt 2) { $overlap = $true; $overlapDetail = "rects [$a] and [$b] intersect"; break }
            }
        }
        Check 'shortcuts overlay: no overlapping text' (-not $overlap) $(if ($overlap) { $overlapDetail } else { "$($texts.Count) text elements checked, no intersections" })
        [DP]::Key(0xBF)  # close it again

        [DP]::Key(0x1B)  # Escape - leave fullscreen before closing
        Start-Sleep -Milliseconds 700
    }
    Stop-Player $proc

    # --------------------------------------------------- AutoSwitch = false
    Set-AutoSwitch $false
    $proc2 = Start-Player
    $cmd2 = Get-MpvCommandLine $proc2
    if (-not $cmd2) {
        Check 'mpv launched (AutoSwitch=off)' $false 'no mpv.exe process found'
    } else {
        $noExclusiveFlag = $cmd2 -notmatch '--d3d11-exclusive-fs'
        $fixedColorspace = $cmd2 -match '--target-colorspace-hint=no'
        Check 'AutoSwitch=off: no d3d11-exclusive-fs override added' $noExclusiveFlag "cmd: $cmd2"
        Check 'AutoSwitch=off: target-colorspace-hint forced to no' $fixedColorspace "cmd: $cmd2"
    }
    Stop-Player $proc2
}
finally {
    Restore-Settings
}

Write-Output ""
$results | Format-Table -AutoSize | Out-String | Write-Output
$fail = ($results | Where-Object Result -eq 'FAIL').Count
Write-Output "$($results.Count - $fail)/$($results.Count) passed"
if ($fail -gt 0) { exit 1 }
