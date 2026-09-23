using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MpvFrontend;

public record PlaylistItem(string Path)
{
    public bool IsUrl => Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    public string Name => IsUrl ? Path : System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Folder => IsUrl
        ? (Uri.TryCreate(Path, UriKind.Absolute, out var u) ? u.Host : "stream")
        : new DirectoryInfo(System.IO.Path.GetDirectoryName(Path) ?? "").Name;

    public bool Matches(string path) =>
        string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);
}

public record SessionState(string Path, double Position);

public partial class MainWindow : Window
{
    private readonly MpvHost _mpvHost = new();
    private readonly ObservableCollection<PlaylistItem> _playlist = new();
    private readonly Settings _settings = Settings.Load();

    private Process? _mpvProcess;
    private OverlayWindow? _overlay;
    private DispatcherTimer? _maximizeSettleTimer;
    private IntPtr _hwnd;
    private bool _isFullscreen;
    private bool _sidebarVisible = true;
    private PlaylistItem? _currentItem;
    private double _lastTimePos;
    private double _lastSavedPos;
    private volatile bool _closing;
    private WindowState _preFullscreenState;
    private Rect _preFullscreenBounds;
    private bool _isPip;
    private Rect _prePipBounds;

    // Resume is applied when the restored file's duration arrives (seeking right
    // after loadfile fails). It is bound to the path it was recorded for so that
    // an auto-advance or a manual load can never inherit someone else's offset.
    private double? _pendingResume;
    private string? _pendingResumePath;
    private string? _screenshotPath;   // file the screenshot folder is currently set for
    private string? _mpvPath;          // the mpv we launched; yt-dlp lives beside it

    internal MpvIpcClient? Ipc { get; private set; }
    internal Settings Config => _settings;

    public MainWindow()
    {
        InitializeComponent();
        AppPaths.EnsureAll();
        PlaylistBox.ItemsSource = _playlist;

        _mpvHost.Width = 100;
        _mpvHost.Height = 100;
        VideoContainer.Child = _mpvHost;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SizeChanged += (_, _) => { ResizeMpvHost(); PositionOverlay(); UpdateWindowRegion(); };
        LocationChanged += (_, _) => PositionOverlay();
        StateChanged += MainWindow_StateChanged;

        // PreviewKeyDown, not KeyDown: a focused playlist ListBoxItem otherwise
        // consumes the arrow keys for list navigation and hotkeys never fire
        PreviewKeyDown += (_, e) => HandleHotkey(e);
        PreviewTextInput += (_, e) => ForwardTextToMpv(e);

        // fallback for wheel-over-video when Windows routes WM_MOUSEWHEEL to the
        // focused window (main) instead of the hovered overlay ("scroll inactive
        // windows" off); guard to the video area so the sidebar still scrolls
        MouseWheel += (_, e) =>
        {
            var p = e.GetPosition(VideoContainer);
            if (p.X < 0 || p.Y < 0 || p.X > VideoContainer.ActualWidth || p.Y > VideoContainer.ActualHeight) return;
            _overlay?.AdjustVolume(e.Delta > 0 ? 5 : -5);
            e.Handled = true;
        };

        Log.UserVisibleError += msg => Dispatcher.BeginInvoke(() => _overlay?.ShowToast(msg));

        ApplySavedBounds();
        _sidebarVisible = _settings.SidebarVisible;
        if (!_sidebarVisible)
        {
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarCol.Width = new GridLength(0);
        }
    }

    // Reopen where the window was last closed, including on a secondary monitor.
    // Guarded against a display that is no longer attached: restoring to a
    // detached monitor's coordinates leaves the window invisible with no way back.
    private void ApplySavedBounds()
    {
        if (_settings.WindowWidth is not double w || _settings.WindowHeight is not double h) return;
        if (_settings.WindowLeft is not double x || _settings.WindowTop is not double y) return;
        if (w < 320 || h < 240) return;

        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var virtualScreen = new Rect(vl, vt, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        // require a decent overlap, not just a touching corner
        var wanted = new Rect(x, y, w, h);
        var visible = Rect.Intersect(wanted, virtualScreen);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 150)
        {
            Log.Warn($"saved window bounds {wanted} are off-screen; using defaults");
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = x;
        Top = y;
        Width = w;
        Height = h;
    }

    private void ResizeMpvHost()
    {
        _mpvHost.Width = Math.Max(1, VideoContainer.ActualWidth);
        _mpvHost.Height = Math.Max(1, VideoContainer.ActualHeight);
    }

    // Borderless windows maximize over the taskbar by default; clamp maximize to
    // the work area. (Fullscreen doesn't use Maximized at all — it sets explicit
    // monitor bounds, so the shell's native fullscreen handling kicks in.)
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = ((HwndSource)PresentationSource.FromVisual(this)!).Handle;
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);

        // Windows 11's DWMWA_WINDOW_CORNER_PREFERENCE is the "correct" way to
        // ask for rounded corners - it rounds the whole composited window
        // surface, mpv's video included, since that's a native child HWND a
        // WPF clip could never reach. But it is a no-op on this build (the
        // registered ProductName is "Windows 10 IoT Enterprise LTSC 2024"
        // despite a Windows-11-numbered build: LTSC channels stay on the
        // Windows 10 shell, which never implemented it - confirmed by this
        // call returning S_OK while producing no visible change). It costs
        // nothing to still ask, for whichever machine this next runs on with
        // a shell that honours it; UpdateWindowRegion below is what actually
        // draws the rounding here, and everywhere DWM's attribute doesn't.
        ApplyRoundedCorners(_hwnd);
        UpdateWindowRegion();
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int valueSize);

    private static void ApplyRoundedCorners(IntPtr hwnd)
    {
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not set DWMWA_WINDOW_CORNER_PREFERENCE: {ex.Message}");
        }
    }

    // The actual corner rounding on this machine: a hard window-shape clip via
    // SetWindowRgn, which has worked since Windows 2000 and does not depend on
    // any DWM shell feature. Reapplied on every resize because the region is
    // sized in the window's own client-area pixels, not something that scales
    // itself - and dropped back to a plain rectangle while maximized or
    // fullscreen, matching how a maximized window is supposed to sit flush
    // against the screen edges rather than clipping into content that's
    // already filling them.
    private void UpdateWindowRegion()
    {
        if (_hwnd == IntPtr.Zero) return;

        if (WindowState is WindowState.Maximized or WindowState.Minimized || _isFullscreen)
        {
            SetWindowRgn(_hwnd, IntPtr.Zero, true);
            return;
        }

        if (!GetClientRect(_hwnd, out var rect)) return;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;

        // ~8px corner radius at 100% scale, matched to Windows 11's own
        // "round" preset and scaled so it looks the same size on any monitor.
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var diameter = (int)Math.Round(16 * dpi);
        var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, diameter, diameter);
        if (region == IntPtr.Zero) return;

        // Ownership of the region handle transfers to the window on success;
        // SetWindowRgn frees the *previous* region itself, so there's nothing
        // here to clean up either way.
        if (SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region);
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECTN rect);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;

        var area = mi.rcWork;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = area.Left - mi.rcMonitor.Left;
        mmi.ptMaxPosition.Y = area.Top - mi.rcMonitor.Top;
        mmi.ptMaxSize.X = area.Right - area.Left;
        mmi.ptMaxSize.Y = area.Bottom - area.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINTN { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECTN { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINTN ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECTN rcMonitor, rcWork;
        public int dwFlags;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    // --- startup -----------------------------------------------------------

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ResizeMpvHost();
        Log.Info("Nexus Player starting");

        var mpvPath = MpvLocator.Locate(_settings);
        if (mpvPath == null)
        {
            var picked = PromptForMpv();
            if (picked == null)
            {
                Log.UserError("mpv.exe not found. Set its location in settings to start playback.");
                return;
            }
            mpvPath = picked;
            _settings.MpvPath = picked;
            _settings.Save();
        }

        if (!await StartMpv(mpvPath)) return;

        _overlay = new OverlayWindow(this) { Owner = this };
        _overlay.SetSidebarHidden(!_sidebarVisible);
        _overlay.Show();
        PositionOverlay();

        WireIpcObservers();

        await Ipc!.SetVolume(_settings.Volume);
        await Ipc.SetProperty("mute", _settings.Muted);
        if (Math.Abs(_settings.Speed - 1.0) > 0.001) await Ipc.SetProperty("speed", _settings.Speed);
        _overlay.ApplyPersistedState(_settings);

        await RestoreLastSession();
    }

    private string? PromptForMpv()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Locate mpv.exe",
            Filter = "mpv executable|mpv.exe|All files (*.*)|*.*",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private async Task<bool> StartMpv(string mpvPath)
    {
        var pipeName = $"nexus-player-{Environment.ProcessId}";
        var hwnd = _mpvHost.Handle;

        var psi = new ProcessStartInfo
        {
            FileName = mpvPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            // v1 left mpv's output going to a console that does not exist when the
            // app is launched from its shortcut, so startup errors (stale mpv.conf
            // options, failed URL loads) were invisible. Capture both streams.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            $"--wid={hwnd}",
            $"--input-ipc-server=\\\\.\\pipe\\{pipeName}",
            "--idle=yes",
            "--force-window=yes",
            "--no-border",
            "--osc=no",     // our overlay replaces mpv's built-in controls
        })
            psi.ArgumentList.Add(a);

        // AutoSwitchDisplayProfile: decide these from the monitor mpv is
        // actually about to draw on, instead of inheriting whatever machine
        // mpv.conf was last tuned for. d3d11-exclusive-fs only gets negotiated
        // at launch/fullscreen-enter, so this initial guess is launch-time
        // only - ToggleFullscreen re-derives the colorspace target live on
        // every fullscreen entry, in case the window has since moved monitors.
        if (_settings.AutoSwitchDisplayProfile)
        {
            var monitor = MonitorFromWindow(_hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var profile = DisplayProfileService.Detect(monitor);
            psi.ArgumentList.Add($"--d3d11-exclusive-fs={(profile.IsInternalPanel ? "no" : "yes")}");
            psi.ArgumentList.Add($"--target-colorspace-hint={(profile.HdrCapable && profile.HdrEnabled ? "yes" : "no")}");
        }
        else
        {
            // mpv defaults this to auto, which silently engages HDR passthrough
            // on an HDR display — leave it off when auto-detection is disabled,
            // matching this app's previous fixed behaviour.
            psi.ArgumentList.Add("--target-colorspace-hint=no");
        }

        // Auto-loading off, then the wanted scripts by name. quality-menu.lua
        // otherwise overwrites ytdl-format on every load and our Quality menu
        // has no effect. See MpvScripts.
        foreach (var a in MpvScripts.LaunchArgs(mpvPath))
            psi.ArgumentList.Add(a);

        try
        {
            _mpvProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _mpvProcess.OutputDataReceived += (_, ev) => { if (ev.Data != null) Log.Mpv(ev.Data); };
            _mpvProcess.ErrorDataReceived += (_, ev) => { if (ev.Data != null) Log.Mpv("ERR " + ev.Data); };
            _mpvProcess.Exited += MpvProcessExited;

            _mpvProcess.Start();
            _mpvProcess.BeginOutputReadLine();
            _mpvProcess.BeginErrorReadLine();
            _mpvPath = mpvPath;
            Log.Info($"mpv started (pid {_mpvProcess.Id}) from {mpvPath}");
        }
        catch (Exception ex)
        {
            // v1 let this throw out of an async void handler, killing the app on
            // launch with no message at all
            Log.UserError("Could not start mpv. Check its path in settings.", ex);
            return false;
        }

        Ipc = new MpvIpcClient(pipeName);
        if (!await Ipc.ConnectAsync())
        {
            Log.UserError("Could not connect to mpv. Try restarting the app.");
            return false;
        }
        Ipc.Disconnected += () => Dispatcher.BeginInvoke(() =>
        {
            if (!_closing) Log.UserError("mpv stopped unexpectedly. Restart the app to continue.");
        });
        return true;
    }

    private void MpvProcessExited(object? sender, EventArgs e)
    {
        if (_closing) return;
        var code = -1;
        try { code = _mpvProcess?.ExitCode ?? -1; } catch { }
        Log.Error($"mpv exited unexpectedly with code {code}; see mpv.log");
    }

    private void WireIpcObservers()
    {
        if (Ipc == null) return;

        Ipc.ObserveDouble("time-pos", pos =>
        {
            // mpv resets time-pos as it tears down — once we are closing, the
            // position saved by MainWindow_Closing is the truth, so freeze it
            if (_closing) return;
            _lastTimePos = pos;
            if (Math.Abs(pos - _lastSavedPos) >= 30) SaveSession(); // crash protection
            Dispatcher.BeginInvoke(() => _overlay?.UpdateTime(pos));
        });

        Ipc.ObserveDouble("duration", dur => Dispatcher.BeginInvoke(() =>
        {
            _overlay?.UpdateDuration(dur);
            ApplyPendingResume(dur);
        }));

        Ipc.ObserveBool("pause", paused => Dispatcher.BeginInvoke(() => _overlay?.UpdatePause(paused)));

        // THE fix for v1's worst bug. mpv advances its own playlist at end of
        // file, but v1 observed nothing, so _currentItem stayed pinned to the
        // first file: closing during episode 2 wrote episode 1's path together
        // with episode 2's timestamp, and the next launch resumed the wrong
        // episode at a meaningless offset. The now-playing text went stale for
        // exactly the same reason.
        Ipc.ObserveString("path", p => Dispatcher.BeginInvoke(() => OnMpvPathChanged(p)));

        // yt-dlp resolves the real title a moment after loadfile; swap it in for raw URLs
        Ipc.ObserveString("media-title", title => Dispatcher.BeginInvoke(() =>
        {
            if (_currentItem is { IsUrl: true } && !string.IsNullOrWhiteSpace(title) && !title.Contains("://"))
                _overlay?.SetNowPlaying(title, _currentItem.Folder);
        }));

        Ipc.ObserveDouble("speed", s => { _settings.Speed = s; Dispatcher.BeginInvoke(() => _overlay?.UpdateSpeed(s)); });
        Ipc.ObserveDouble("volume", v => { _settings.Volume = v; Dispatcher.BeginInvoke(() => _overlay?.UpdateVolume(v)); });
        Ipc.ObserveBool("mute", m => { _settings.Muted = m; Dispatcher.BeginInvoke(() => _overlay?.UpdateMute(m)); });
    }

    private void OnMpvPathChanged(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (_currentItem == null || !_currentItem.Matches(path))
        {
            var item = _playlist.FirstOrDefault(i => i.Matches(path));
            if (item == null)
            {
                item = new PlaylistItem(path);
                _playlist.Add(item);
            }

            _currentItem = item;
            PlaylistBox.SelectedItem = item;

            // the position counters belong to the file that just ended; clearing
            // them stops the next save attributing an old offset to the new file
            _lastTimePos = 0;
            _lastSavedPos = 0;

            // a resume offset is only ever valid for the file it was recorded against
            if (!string.Equals(path, _pendingResumePath, StringComparison.OrdinalIgnoreCase))
            {
                _pendingResume = null;
                _pendingResumePath = null;
            }

            ShowNowPlaying(item);
            PushQueuePosition();
            Log.Info($"now playing: {path}");
        }

        // Everything below is reconciled against what is actually playing, not
        // against whether the item changed. On a fresh start the restored file is
        // already _currentItem, so a change-gated check never runs on that launch
        // — which is how the screenshot folder, and then the Quality row, both
        // ended up silently doing nothing.
        _overlay?.SetStreamMode(
            path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        if (!string.Equals(_screenshotPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _screenshotPath = path;
            ConfigureScreenshots(path);
        }
    }

    private void ApplyPendingResume(double duration)
    {
        if (_pendingResume is not double resume || duration <= 0 || Ipc == null) return;
        if (_currentItem == null || _pendingResumePath == null || !_currentItem.Matches(_pendingResumePath)) return;

        _pendingResume = null;
        _pendingResumePath = null;
        if (resume < duration - 10)
            Fire.AndForget(Ipc.SetProperty("time-pos", resume), "resume seek");
    }

    // --- last-played session ----------------------------------------------

    private void SaveSession()
    {
        if (_currentItem == null) return;
        try
        {
            AppPaths.EnsureAll();
            var tmp = AppPaths.SessionFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new SessionState(_currentItem.Path, _lastTimePos)));
            File.Move(tmp, AppPaths.SessionFile, overwrite: true);
            _lastSavedPos = _lastTimePos;
        }
        catch (Exception ex) { Log.Error("session save failed", ex); }
    }

    private async Task RestoreLastSession()
    {
        SessionState? s = null;
        try
        {
            if (File.Exists(AppPaths.SessionFile))
                s = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(AppPaths.SessionFile));
        }
        catch (Exception ex) { Log.Warn("session file unreadable, starting fresh: " + ex.Message); }

        if (s == null || string.IsNullOrWhiteSpace(s.Path) || Ipc == null) return;

        var item = new PlaylistItem(s.Path);
        if (!item.IsUrl && !File.Exists(s.Path))
        {
            Log.Info("last session file no longer exists: " + s.Path);
            return;
        }

        _playlist.Add(item);
        PlaylistBox.SelectedItem = item;
        _currentItem = item;

        if (s.Position > 5)
        {
            _pendingResume = s.Position;
            _pendingResumePath = s.Path;
        }

        await Ipc.LoadFile(s.Path);
        await Ipc.SetPause(true); // show it, don't blast audio on launch
        ShowNowPlaying(item);
    }

    // --- window plumbing ---------------------------------------------------

    private void PositionOverlay()
    {
        if (_overlay == null || !VideoContainer.IsLoaded) return;
        var source = PresentationSource.FromVisual(VideoContainer);
        if (source?.CompositionTarget == null) return;
        var topLeft = source.CompositionTarget.TransformFromDevice
            .Transform(VideoContainer.PointToScreen(new Point(0, 0)));
        _overlay.Left = topLeft.X;
        _overlay.Top = topLeft.Y;
        _overlay.Width = VideoContainer.ActualWidth;
        _overlay.Height = VideoContainer.ActualHeight;
    }

    // Maximize/restore is a DWM compositor animation of this window's own
    // bitmap - mpv's video redraws once, immediately, and DWM visually scales
    // between the before/after frames over ~200ms. The overlay is a *separate*
    // top-level window, so it isn't part of that animation: repositioning it
    // right away (the old behaviour) snapped the controls to their final size
    // instantly while the video was still visibly growing/shrinking underneath
    // - the "stuttering, expanding in and out" look. Hiding the overlay for the
    // animation's duration and only placing it once things have settled keeps
    // both in sync instead of racing each other.
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_overlay == null) return;
        _maximizeSettleTimer?.Stop();

        if (WindowState == WindowState.Minimized)
        {
            _overlay.Hide();
            return;
        }

        _overlay.Hide();

        // Nothing to wait out if the user (or an accessibility setting) has
        // turned window animations off - there is no transition to desync from.
        if (!SystemParameters.MinimizeAnimation)
        {
            PositionOverlay();
            _overlay.Show();
            return;
        }

        if (_maximizeSettleTimer == null)
        {
            _maximizeSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _maximizeSettleTimer.Tick += MaximizeSettleTimer_Tick;
        }
        _maximizeSettleTimer.Start();
    }

    private void MaximizeSettleTimer_Tick(object? sender, EventArgs e)
    {
        _maximizeSettleTimer!.Stop();
        PositionOverlay();
        _overlay?.Show();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _maximizeSettleTimer?.Stop();
        _closing = true;
        SaveSession();

        if (WindowState == WindowState.Normal && !_isFullscreen)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settings.SidebarVisible = _sidebarVisible;
        _settings.Save();

        Ipc?.Dispose();
        try
        {
            if (_mpvProcess is { HasExited: false }) _mpvProcess.Kill();
        }
        catch (Exception ex) { Log.Warn("mpv already gone: " + ex.Message); }
        Log.Info("Nexus Player closed");
    }

    // --- window chrome -----------------------------------------------------

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    public void ToggleSidebar()
    {
        if (_isFullscreen) return;
        _sidebarVisible = !_sidebarVisible;
        Sidebar.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarCol.Width = new GridLength(_sidebarVisible ? 300 : 0);
        _overlay?.SetSidebarHidden(!_sidebarVisible);
        AfterLayoutRefresh();
    }

    public void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            TitleBar.Visibility = Visibility.Collapsed;
            TitleRow.Height = new GridLength(0); // the row itself, or 40px stays reserved
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarCol.Width = new GridLength(0);

            // a plain borderless window covering the monitor exactly is what
            // VLC/mpv use — the shell detects it and puts the taskbar behind
            // natively, as long as this (non-layered) window stays foreground.
            // NoResize drops WS_THICKFRAME first: Windows pads any resizable,
            // non-maximized top-level window with an invisible resize border
            // (~7px at 100% scale) outside its painted frame, which otherwise
            // insets the visible video from the monitor edge by exactly that
            // much - a sliver of bezel showing on every side. A real Maximized
            // window gets this compensated automatically, but this state
            // deliberately stays Normal (Maximized would clamp to the work
            // area and leave the taskbar showing), so nothing else cancels it.
            var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            var toDip = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
            var tl = toDip.Transform(new Point(mi.rcMonitor.Left, mi.rcMonitor.Top));
            var br = toDip.Transform(new Point(mi.rcMonitor.Right, mi.rcMonitor.Bottom));
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Left = tl.X;
            Top = tl.Y;
            Width = br.X - tl.X;
            Height = br.Y - tl.Y;

            // Re-derive the HDR/SDR target for whichever monitor this is,
            // fresh on every fullscreen entry - the window may have moved
            // since launch, and unlike d3d11-exclusive-fs this is a real
            // runtime property mpv can pick up without a relaunch. mpv only
            // re-negotiates its swapchain colorspace on a resize, hence the
            // NudgeVideoSurface() nudge (see its own doc comment).
            if (_settings.AutoSwitchDisplayProfile && Ipc != null)
            {
                var profile = DisplayProfileService.Detect(monitor);
                var hdrTarget = profile.HdrCapable && profile.HdrEnabled;
                Fire.AndForget(Ipc.SetProperty("target-colorspace-hint", hdrTarget ? "yes" : "no"),
                    "display HDR target");
                NudgeVideoSurface();
            }
        }
        else
        {
            TitleBar.Visibility = Visibility.Visible;
            TitleRow.Height = new GridLength(40);
            if (_sidebarVisible)
            {
                Sidebar.Visibility = Visibility.Visible;
                SidebarCol.Width = new GridLength(300);
            }
            ResizeMode = ResizeMode.CanResize;
            Left = _preFullscreenBounds.Left;
            Top = _preFullscreenBounds.Top;
            Width = _preFullscreenBounds.Width;
            Height = _preFullscreenBounds.Height;
            WindowState = _preFullscreenState;
        }
        _overlay?.SetFullscreenState(_isFullscreen);
        if (!_isFullscreen) _overlay?.SetSidebarHidden(!_sidebarVisible);
        AfterLayoutRefresh();
    }

    private void AfterLayoutRefresh() =>
        Dispatcher.BeginInvoke(() => { ResizeMpvHost(); PositionOverlay(); },
            System.Windows.Threading.DispatcherPriority.Loaded);

    // mpv only re-negotiates its swapchain colorspace (SDR vs HDR passthrough)
    // when its window is resized — moving between monitors or flipping target-*
    // options mid-play leaves it stuck on the previous mode. A 1-DIP
    // shrink-and-restore forces the re-negotiation.
    public void NudgeVideoSurface() => Fire.AndForget(NudgeVideoSurfaceAsync(), "video surface nudge");

    private async Task NudgeVideoSurfaceAsync()
    {
        if (VideoContainer.ActualWidth < 3) return;
        _mpvHost.Width = VideoContainer.ActualWidth - 1;
        await Task.Delay(80);
        ResizeMpvHost();
    }

    // --- keyboard ----------------------------------------------------------

    // Every branch sets e.Handled BEFORE starting async work. v1 set it after an
    // await, which only suppressed routing because the IPC write happened to
    // complete synchronously; making the IPC genuinely async would have silently
    // broken every hotkey (Space reaching the focused button, arrows reaching
    // the playlist) with nothing in the code to point at.
    public void HandleHotkey(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return; // typing a URL, not a player key
        var ipc = Ipc;

        switch (e.Key)
        {
            case Key.Escape when _isFullscreen:
                e.Handled = true;
                ToggleFullscreen();
                break;
            case Key.F11:
            case Key.F:
                e.Handled = true;
                ToggleFullscreen();
                break;
            case Key.Space:
                e.Handled = true;
                Fire.AndForget(ipc?.TogglePause(), "toggle pause");
                break;
            case Key.Left:
                e.Handled = true;
                Fire.AndForget(ipc?.SeekRelative(-5), "seek back");
                break;
            case Key.Right:
                e.Handled = true;
                Fire.AndForget(ipc?.SeekRelative(5), "seek forward");
                break;
            case Key.Up:
                e.Handled = true;
                _overlay?.AdjustVolume(5);
                break;
            case Key.Down:
                e.Handled = true;
                _overlay?.AdjustVolume(-5);
                break;
            case Key.M:
                e.Handled = true;
                _overlay?.ToggleMute();
                break;
            case Key.PageUp:
                e.Handled = true;
                Fire.AndForget(ipc?.KeyPress("PGUP"), "next chapter");
                break;
            case Key.PageDown:
                e.Handled = true;
                Fire.AndForget(ipc?.KeyPress("PGDWN"), "previous chapter");
                break;
            case Key.Tab:
                e.Handled = true;
                _overlay?.ToggleQueuePanel();
                break;
            case Key.OemQuestion:
            case Key.Divide:
                e.Handled = true;
                _overlay?.ToggleShortcuts();
                break;
            case Key.P when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                TogglePictureInPicture();
                break;
        }
    }

    // --- queue ---------------------------------------------------------------

    public IReadOnlyList<PlaylistItem> QueueSnapshot() => _playlist.ToList();

    public bool IsCurrent(PlaylistItem item) => _currentItem != null && _currentItem.Matches(item.Path);

    public void PlayQueueIndex(int index)
    {
        if (index < 0 || index >= _playlist.Count) return;
        PlaylistBox.SelectedItem = _playlist[index];
        PlayItem(_playlist[index]);
    }

    // Release filenames are unreadable on screen ("[FLE] Re ZERO ... - S03E02v3
    // (WEB 1080p H.264 E-AC-3) [Dual Audio] [82891140]"). Show the title and the
    // episode instead, the way the reference player does.
    private void ShowNowPlaying(PlaylistItem item) =>
        _overlay?.SetNowPlaying(TitleCleaner.ShowTitle(item.Path), TitleCleaner.EpisodeLabel(item.Path));

    // Screenshots go to Pictures/NexuSS/<show - season>/ so a session's grabs stay
    // together instead of piling into one flat folder. mpv writes them itself;
    // this just points it at the right place whenever the file changes.
    private void ConfigureScreenshots(string path)
    {
        if (Ipc == null) return;
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NexuSS");
            var dir = Path.Combine(root, TitleCleaner.FolderName(path));
            Directory.CreateDirectory(dir);
            Fire.AndForget(Ipc.SetProperty("screenshot-directory", dir), "screenshot directory");
            // %F = file name without extension, %P = playback time
            Fire.AndForget(Ipc.SetProperty("screenshot-template", "%F - %P"), "screenshot template");
            Log.Info("screenshots -> " + dir);
        }
        catch (Exception ex) { Log.Error("could not set the screenshot folder", ex); }
    }

    private void PushQueuePosition()
    {
        var index = _currentItem == null ? -1 : _playlist.IndexOf(_currentItem);
        _overlay?.SetQueuePosition(index, _playlist.Count);
    }

    // --- streamed video quality ----------------------------------------------

    public bool CurrentIsUrl => _currentItem?.IsUrl == true;
    public string? CurrentPath => _currentItem?.Path;
    public string? MpvPath => _mpvPath;

    /// <summary>
    /// Reload what is playing and land back where the viewer was. Changing the
    /// stream quality means re-resolving the link, because mpv cannot swap a
    /// ytdl-selected rendition in place; reusing the resume machinery means the
    /// seek waits for the new duration rather than racing the load.
    /// </summary>
    public async Task ReloadCurrentPreservingPosition()
    {
        if (Ipc == null || _currentItem == null) return;

        var resumeAt = _lastTimePos;
        var wasPaused = false;
        var paused = await Ipc.RequestAsync("get_property", "pause");
        if (paused.ValueKind == System.Text.Json.JsonValueKind.True) wasPaused = true;

        if (resumeAt > 2)
        {
            _pendingResume = resumeAt;
            _pendingResumePath = _currentItem.Path;
        }
        await Ipc.LoadFile(_currentItem.Path);
        if (wasPaused) await Ipc.SetPause(true);
        Log.Info($"reloading at {resumeAt:F1}s for a quality change");
    }

    // --- picture-in-picture --------------------------------------------------

    // A small always-on-top window in the corner. Deliberately not the fullscreen
    // path: that one relies on the window staying non-topmost so the shell's
    // native fullscreen detection works, and Topmost would break it.
    public void TogglePictureInPicture()
    {
        if (_isFullscreen) ToggleFullscreen();

        if (_isPip)
        {
            _isPip = false;
            Topmost = false;
            TitleBar.Visibility = Visibility.Visible;
            TitleRow.Height = new GridLength(40);
            if (_sidebarVisible)
            {
                Sidebar.Visibility = Visibility.Visible;
                SidebarCol.Width = new GridLength(300);
            }
            Left = _prePipBounds.Left;
            Top = _prePipBounds.Top;
            Width = _prePipBounds.Width;
            Height = _prePipBounds.Height;
        }
        else
        {
            _isPip = true;
            _prePipBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            WindowState = WindowState.Normal;
            TitleBar.Visibility = Visibility.Collapsed;
            TitleRow.Height = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarCol.Width = new GridLength(0);

            var work = SystemParameters.WorkArea;
            Width = 480;
            Height = 280;
            Left = work.Right - Width - 24;
            Top = work.Bottom - Height - 24;
            Topmost = true;
        }
        AfterLayoutRefresh();
    }

    // Forward printable keys to mpv so its own default bindings work
    // (s screenshot, [ ] speed, , . frame-step, 9/0 volume, v sub toggle, ...)
    public void ForwardTextToMpv(TextCompositionEventArgs e)
    {
        if (Ipc == null || string.IsNullOrEmpty(e.Text)) return;
        if (e.OriginalSource is TextBox) return; // typing a URL, not a player key

        var ch = e.Text;
        // Skip keys the app handles itself: a handled KeyDown does NOT suppress
        // TextInput in WPF, so without this they would reach mpv too and
        // double-trigger. 'q' would quit mpv's engine outright.
        if (ch is " " or "q" or "Q" or "f" or "F" or "m" or "M" or "/") return;

        e.Handled = true;
        Fire.AndForget(Ipc.KeyPress(ch), "forward key to mpv");
    }

    // --- playlist ----------------------------------------------------------

    private void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedItem is not PlaylistItem item || Ipc == null) return;
        PlayItem(item);
    }

    private void PlayItem(PlaylistItem item)
    {
        if (Ipc == null) return;
        _currentItem = item;
        _pendingResume = null;   // the user picked this; don't inherit an old offset
        _pendingResumePath = null;
        Fire.AndForget(Ipc.LoadFile(item.Path), "load file");
        ShowNowPlaying(item);
    }

    // --- play from URL -----------------------------------------------------

    private void OpenUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (UrlPanel.Visibility == Visibility.Visible)
        {
            UrlPanel.Visibility = Visibility.Collapsed;
            Focus();
        }
        else
        {
            UrlPanel.Visibility = Visibility.Visible;
            UrlBox.Focus();
        }
    }

    private async void PlayUrlButton_Click(object sender, RoutedEventArgs e) => await PlayUrlFromBox();

    private async void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await PlayUrlFromBox();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            UrlPanel.Visibility = Visibility.Collapsed;
            Focus();
        }
    }

    private async Task PlayUrlFromBox()
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0 || Ipc == null) return;
        if (!url.Contains("://")) url = "https://" + url;

        var item = new PlaylistItem(url);
        _playlist.Add(item);
        PlaylistBox.SelectedItem = item;
        _currentItem = item;
        _pendingResume = null;
        _pendingResumePath = null;

        await Ipc.LoadFile(url);
        ShowNowPlaying(item);

        UrlBox.Clear();
        UrlPanel.Visibility = Visibility.Collapsed;
        Focus(); // hand keyboard back to the window so hotkeys work

        // mpv reports nothing when a site is not yt-dlp supported; if no file has
        // loaded a moment later, say so rather than sitting on a black frame
        await Task.Delay(4000);
        if (Ipc != null && await Ipc.GetDoubleAsync("duration") is null && _currentItem == item)
            Log.UserError("Could not play that link — the site may not be supported.");
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        // The shell rejects forward-slash paths even though Directory.Exists
        // accepts them, and an InitialDirectory it dislikes throws out of
        // ShowDialog. GetFullPath canonicalises the separators; the guard stops
        // any other bad value from mattering.
        string? initial = null;
        try
        {
            var root = _settings.LibraryRoots.FirstOrDefault(Directory.Exists)
                       ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var full = Path.GetFullPath(root);
                if (Directory.Exists(full)) initial = full;
            }
        }
        catch (Exception ex) { Log.Warn("unusable library root: " + ex.Message); }

        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Video files (*.mkv;*.mp4;*.avi;*.mov;*.webm)|*.mkv;*.mp4;*.avi;*.mov;*.webm|All files (*.*)|*.*"
        };
        if (initial != null) dlg.InitialDirectory = initial;

        bool? picked;
        try { picked = dlg.ShowDialog(); }
        catch (Exception ex)
        {
            Log.UserError("Could not open the file picker.", ex);
            return;
        }
        if (picked != true || Ipc == null) return;

        var wasEmpty = _playlist.Count == 0;
        foreach (var f in dlg.FileNames) _playlist.Add(new PlaylistItem(f));

        if (wasEmpty && dlg.FileNames.Length > 0)
        {
            _currentItem = _playlist[0];
            _pendingResume = null;
            _pendingResumePath = null;
            await Ipc.LoadFile(dlg.FileNames[0]);
            ShowNowPlaying(_playlist[0]);
            for (int i = 1; i < dlg.FileNames.Length; i++)
                await Ipc.AppendFile(dlg.FileNames[i]);
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistBox.SelectedItem is PlaylistItem item)
            _playlist.Remove(item);
    }
}
