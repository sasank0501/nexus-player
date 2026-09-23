using System;
using System.Runtime.InteropServices;

namespace MpvFrontend;

// What a given monitor's HDR toggle and panel type actually are - the same
// QueryDisplayConfig/DisplayConfigGetDeviceInfo surface Windows' own Settings
// app uses, not GetMonitorInfo (which only ever tells you pixels). Exists so
// mpv gets launched with settings that match the screen it is actually about
// to draw on, instead of inheriting whatever machine mpv.conf was last tuned
// for - see MainWindow.StartMpv/ToggleFullscreen for how this gets used.
public readonly record struct DisplayProfile(bool IsInternalPanel, bool HdrCapable, bool HdrEnabled);

public static class DisplayProfileService
{
    // Never throws: a detection failure falls back to "assume external, assume
    // SDR", which is today's shipped behaviour, so a bad driver or an unusual
    // multi-GPU setup degrades gracefully instead of breaking playback.
    public static DisplayProfile Detect(IntPtr hMonitor)
    {
        try
        {
            return DetectCore(hMonitor) ?? new DisplayProfile(false, false, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"display profile detection failed, assuming external/SDR: {ex.Message}");
            return new DisplayProfile(false, false, false);
        }
    }

    private static DisplayProfile? DetectCore(IntPtr hMonitor)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref mi)) return null;

        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != ERROR_SUCCESS)
            return null;
        if (pathCount == 0) return null;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ERROR_SUCCESS)
            return null;

        for (var i = 0; i < pathCount; i++)
        {
            var path = paths[i];

            var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                }
            };
            if (DisplayConfigGetDeviceInfo(ref sourceName) != ERROR_SUCCESS) continue;
            if (!string.Equals(sourceName.viewGdiDeviceName, mi.szDevice, StringComparison.OrdinalIgnoreCase))
                continue;

            var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                }
            };
            var isInternal = DisplayConfigGetDeviceInfo(ref targetName) == ERROR_SUCCESS
                && (targetName.outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                    || targetName.outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED);

            var colorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                    size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                }
            };
            var hdrCapable = false;
            var hdrEnabled = false;
            if (DisplayConfigGetDeviceInfo(ref colorInfo) == ERROR_SUCCESS)
            {
                hdrCapable = (colorInfo.value & 0x1) != 0; // AdvancedColorSupported
                hdrEnabled = (colorInfo.value & 0x2) != 0; // AdvancedColorEnabled
            }

            return new DisplayProfile(isInternal, hdrCapable, hdrEnabled);
        }

        return null; // active path list didn't include this monitor - shouldn't happen, but degrade gracefully
    }

    // --- Win32 ---------------------------------------------------------------
    // Struct sizes here must exactly match the native ones (QueryDisplayConfig
    // writes raw arrays of these) - verified against the documented layouts,
    // not guessed. Fields we never read (mode info contents) are still given
    // real, correctly-sized placeholders so array indexing stays correct.

    private const int ERROR_SUCCESS = 0;
    private const int QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    // Legacy LVDS-connected laptop panels report INTERNAL; modern eDP-connected
    // panels (this machine included) report as embedded DisplayPort instead -
    // both mean "built-in panel," so both count.
    private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;
    private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;

    // No unsuffixed export exists - without an explicit EntryPoint this
    // resolves to GetMonitorInfoA (ANSI, a 72-byte MONITORINFOEXA), which
    // rejects our Unicode-marshaled (104-byte) struct via its own cbSize
    // check. Must call the W variant to match.
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(int flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(int flags,
        ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO request);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;   // union with clone-group/source-mode bitfields; unused
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;   // union with desktop/target-mode bitfields; unused
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // Real native size is 64 bytes (infoType+id+adapterId+largest union member).
    // Contents of the union (target/source mode, desktop image info) are never
    // read - only the array's element stride has to match, so this is sized
    // with explicit padding rather than modelling the union's branches.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public int size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value; // bit 0 = AdvancedColorSupported, bit 1 = AdvancedColorEnabled
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }
}
