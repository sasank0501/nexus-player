using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
}

public record SessionState(string Path, double Position);

public partial class MainWindow : Window
{
    private const string MpvPath = @"C:\Users\Sasank\Downloads\bootstrapper\mpv.exe";

    private static readonly string SessionFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusPlayer", "session.json");

    private readonly MpvHost _mpvHost = new();
    private readonly ObservableCollection<PlaylistItem> _playlist = new();
    private Process? _mpvProcess;
    private OverlayWindow? _overlay;
    private bool _isFullscreen;
    private bool _sidebarVisible = true;
    private PlaylistItem? _currentItem;
    private double _lastTimePos;
    private double _lastSavedPos;
    private volatile bool _closing;
    private double? _pendingResume; // seek target once the restored file's duration arrives
    private WindowState _preFullscreenState;
    private Rect _preFullscreenBounds;

    internal MpvIpcClient? Ipc { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        PlaylistBox.ItemsSource = _playlist;

        _mpvHost.Width = 100;
        _mpvHost.Height = 100;
        VideoContainer.Child = _mpvHost;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SizeChanged += (_, _) => { ResizeMpvHost(); PositionOverlay(); };
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
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
    }

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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ResizeMpvHost();

        var pipeName = $"mpv-frontend-{Environment.ProcessId}";
        var hwnd = _mpvHost.Handle;

        _mpvProcess = new Process
        {
            StartInfo = new ProcessStartInfo { FileName = MpvPath, UseShellExecute = false }
        };
        foreach (var a in new[]
        {
            $"--wid={hwnd}",
            $"--input-ipc-server=\\\\.\\pipe\\{pipeName}",
            "--idle=yes",
            "--force-window=yes",
            "--no-border",
            "--osc=no",     // overlay window replaces mpv's built-in controls
            // mpv 0.41 defaults this to auto, silently engaging HDR passthrough on
            // HDR displays — the overlay's Display switch owns SDR/HDR instead
            "--target-colorspace-hint=no"
        })
            _mpvProcess.StartInfo.ArgumentList.Add(a);
        _mpvProcess.Start();

        Ipc = new MpvIpcClient(pipeName);
        var connected = await Ipc.ConnectAsync();
        if (!connected)
        {
            MessageBox.Show("Could not connect to mpv. Try restarting the app.", "Nexus Player");
            return;
        }

        _overlay = new OverlayWindow(this) { Owner = this };
        _overlay.Show();
        PositionOverlay();

        Ipc.TimePosChanged += pos =>
        {
            // mpv resets time-pos as it tears down — once we're closing, the
            // position saved by MainWindow_Closing is the truth, freeze it
            if (_closing) return;
            _lastTimePos = pos;
            if (Math.Abs(pos - _lastSavedPos) >= 30) SaveSession(); // crash protection
            Dispatcher.Invoke(() => _overlay?.UpdateTime(pos));
        };
        Ipc.DurationChanged += dur => Dispatcher.Invoke(() =>
        {
            _overlay?.UpdateDuration(dur);
            // duration arriving means the restored file finished loading — safe to seek now
            if (_pendingResume is double resume && dur > 0 && Ipc != null)
            {
                _pendingResume = null;
                if (resume < dur - 10) _ = Ipc.SendAsync("set_property", "time-pos", resume);
            }
        });
        Ipc.PauseChanged += paused => Dispatcher.Invoke(() => _overlay?.UpdatePause(paused));
        // yt-dlp resolves the real title a moment after loadfile; swap it in for raw URLs
        Ipc.MediaTitleChanged += title => Dispatcher.Invoke(() =>
        {
            if (_currentItem is { IsUrl: true } && !string.IsNullOrWhiteSpace(title) && !title.Contains("://"))
                _overlay?.SetNowPlaying(title, _currentItem.Folder);
        });

        // re-observe pause so its initial value arrives now that handlers are attached
        await Ipc.SendAsync("observe_property", 4, "pause");
        await Ipc.SetVolume(35);
        await RestoreLastSession();
    }

    // --- last-played session ---

    private void SaveSession()
    {
        if (_currentItem == null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SessionFile)!);
            File.WriteAllText(SessionFile, System.Text.Json.JsonSerializer.Serialize(
                new SessionState(_currentItem.Path, _lastTimePos)));
            _lastSavedPos = _lastTimePos;
        }
        catch { /* best effort */ }
    }

    private async System.Threading.Tasks.Task RestoreLastSession()
    {
        SessionState? s = null;
        try
        {
            if (File.Exists(SessionFile))
                s = System.Text.Json.JsonSerializer.Deserialize<SessionState>(File.ReadAllText(SessionFile));
        }
        catch { /* corrupt/unreadable — start fresh */ }
        if (s == null || string.IsNullOrWhiteSpace(s.Path) || Ipc == null) return;

        var item = new PlaylistItem(s.Path);
        if (!item.IsUrl && !File.Exists(s.Path)) return; // moved or deleted since last run

        _playlist.Add(item);
        PlaylistBox.SelectedItem = item;
        _currentItem = item;
        _pendingResume = s.Position > 5 ? s.Position : null;
        await Ipc.LoadFile(s.Path);
        await Ipc.SendAsync("set_property", "pause", true); // show it, don't blast audio
        _overlay?.SetNowPlaying(item.Name, item.Folder);
    }

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

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_overlay == null) return;
        if (WindowState == WindowState.Minimized)
        {
            _overlay.Hide();
        }
        else
        {
            _overlay.Show();
            Dispatcher.BeginInvoke(PositionOverlay, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        SaveSession();
        Ipc?.Dispose();
        try
        {
            if (_mpvProcess is { HasExited: false })
                _mpvProcess.Kill();
        }
        catch { /* already gone */ }
    }

    // --- window chrome ---

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
            // natively, as long as this (non-layered) window stays foreground
            var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            var toDip = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
            var tl = toDip.Transform(new Point(mi.rcMonitor.Left, mi.rcMonitor.Top));
            var br = toDip.Transform(new Point(mi.rcMonitor.Right, mi.rcMonitor.Bottom));
            WindowState = WindowState.Normal;
            Left = tl.X;
            Top = tl.Y;
            Width = br.X - tl.X;
            Height = br.Y - tl.Y;
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
            Left = _preFullscreenBounds.Left;
            Top = _preFullscreenBounds.Top;
            Width = _preFullscreenBounds.Width;
            Height = _preFullscreenBounds.Height;
            WindowState = _preFullscreenState;
        }
        AfterLayoutRefresh();
    }

    private void AfterLayoutRefresh() =>
        Dispatcher.BeginInvoke(() => { ResizeMpvHost(); PositionOverlay(); },
            System.Windows.Threading.DispatcherPriority.Loaded);

    // mpv only re-negotiates its swapchain colorspace (SDR vs HDR passthrough)
    // when its window is resized — moving between monitors or flipping
    // target-* options mid-play leaves it stuck on the previous mode. A 1-DIP
    // shrink-and-restore forces the re-negotiation.
    public async void NudgeVideoSurface()
    {
        if (VideoContainer.ActualWidth < 3) return;
        _mpvHost.Width = VideoContainer.ActualWidth - 1;
        await System.Threading.Tasks.Task.Delay(80);
        ResizeMpvHost();
    }

    public async void HandleHotkey(KeyEventArgs e)
    {
        // typing in a TextBox (URL input) must not trigger player hotkeys
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        switch (e.Key)
        {
            case Key.Escape when _isFullscreen:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.F11:
            case Key.F:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Space:
                if (Ipc != null) await Ipc.TogglePause();
                e.Handled = true;
                break;
            case Key.Left:
                if (Ipc != null) await Ipc.SendAsync("seek", -5, "relative");
                e.Handled = true;
                break;
            case Key.Right:
                if (Ipc != null) await Ipc.SendAsync("seek", 5, "relative");
                e.Handled = true;
                break;
            case Key.Up:
                _overlay?.AdjustVolume(5);
                e.Handled = true;
                break;
            case Key.Down:
                _overlay?.AdjustVolume(-5);
                e.Handled = true;
                break;
            case Key.M:
                _overlay?.ToggleMute();
                e.Handled = true;
                break;
            case Key.PageUp:
                if (Ipc != null) await Ipc.SendAsync("keypress", "PGUP");   // next chapter
                e.Handled = true;
                break;
            case Key.PageDown:
                if (Ipc != null) await Ipc.SendAsync("keypress", "PGDWN"); // previous chapter
                e.Handled = true;
                break;
        }
    }

    // forward printable keys to mpv so its default bindings work
    // (m mute, s screenshot, [ ] speed, , . frame-step, 9/0 volume, v sub toggle, ...)
    public async void ForwardTextToMpv(TextCompositionEventArgs e)
    {
        if (Ipc == null || string.IsNullOrEmpty(e.Text)) return;
        if (e.OriginalSource is System.Windows.Controls.TextBox) return; // typing a URL, not a player key
        var ch = e.Text;
        // skip keys the app handles itself (handled KeyDown doesn't suppress TextInput
        // in WPF, so without this they'd reach mpv too and double-trigger) and q = mpv quit
        if (ch is " " or "q" or "Q" or "f" or "F" or "m" or "M") return;
        await Ipc.SendAsync("keypress", ch);
        e.Handled = true;
    }

    // --- playlist ---

    private async void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedItem is not PlaylistItem item || Ipc == null) return;
        _currentItem = item;
        _pendingResume = null; // user picked something else; don't seek it to the old spot
        await Ipc.LoadFile(item.Path);
        _overlay?.SetNowPlaying(item.Name, item.Folder);
    }

    // --- play from URL ---

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

    private async System.Threading.Tasks.Task PlayUrlFromBox()
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0 || Ipc == null) return;
        if (!url.Contains("://")) url = "https://" + url;

        var item = new PlaylistItem(url);
        _playlist.Add(item);
        PlaylistBox.SelectedItem = item;
        _currentItem = item;
        _pendingResume = null;
        await Ipc.LoadFile(url);
        _overlay?.SetNowPlaying(item.Name, item.Folder);

        UrlBox.Clear();
        UrlPanel.Visibility = Visibility.Collapsed;
        Focus(); // hand keyboard back to the window so hotkeys work
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            InitialDirectory = @"C:\Users\Sasank\Downloads",
            Filter = "Video files (*.mkv;*.mp4;*.avi;*.mov;*.webm)|*.mkv;*.mp4;*.avi;*.mov;*.webm|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var wasEmpty = _playlist.Count == 0;
        foreach (var f in dlg.FileNames) _playlist.Add(new PlaylistItem(f));

        if (wasEmpty && Ipc != null && dlg.FileNames.Length > 0)
        {
            _currentItem = _playlist[0];
            _pendingResume = null;
            await Ipc.LoadFile(dlg.FileNames[0]);
            _overlay?.SetNowPlaying(_playlist[0].Name, _playlist[0].Folder);
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
