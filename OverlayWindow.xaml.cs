using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MpvFrontend;

public partial class OverlayWindow : Window
{
    private readonly MainWindow _main;
    private readonly DispatcherTimer _hideTimer;
    private bool _isSeeking;
    private double _duration;
    private bool _muted;
    private bool _ready; // suppress Checked events fired during InitializeComponent

    private static readonly string ShaderDir =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).Replace('\\', '/') + "/mpv/shaders/Anime4K";

    public OverlayWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        _ready = true;
        SettingsPanel.Visibility = Visibility.Collapsed;
        KeyDown += (_, e) => _main.HandleHotkey(e);
        PreviewTextInput += (_, e) => _main.ForwardTextToMpv(e);
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _hideTimer.Tick += (_, _) => HideChrome();
        _hideTimer.Start();
    }

    private MpvIpcClient? Ipc => _main.Ipc;

    // WS_EX_NOACTIVATE: clicks on the overlay must never make it the foreground
    // window. The main (non-layered) window then stays foreground, which lets
    // Windows' native fullscreen detection put the taskbar behind the video —
    // the same mechanism VLC/mpv rely on. Keyboard also always stays on main.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        // WPF still activates on click despite WS_EX_NOACTIVATE; refuse activation
        // at the message level too so the main window always keeps foreground
        HwndSource.FromHwnd(h)?.AddHook(RefuseMouseActivation);
    }

    private static IntPtr RefuseMouseActivation(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    // --- called by MainWindow from mpv events ---

    public void UpdateTime(double pos)
    {
        if (_isSeeking) return;
        TimeLabel.Text = Fmt(pos);
        if (_duration > 0) SeekSlider.Value = pos / _duration * 100.0;
    }

    public void UpdateDuration(double dur)
    {
        _duration = dur;
        DurationLabel.Text = Fmt(dur);
    }

    public void UpdatePause(bool paused) => PlayPauseButton.Content = paused ? "" : "";

    public void SetNowPlaying(string title, string sub)
    {
        NowPlayingTitle.Text = title;
        NowPlayingSub.Text = sub;
        TitleText.Text = title;
        EpisodeText.Text = sub;
        TitleBlock.Visibility = Visibility.Visible;
    }

    private static string Fmt(double s) =>
        TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    // --- auto-hide ---

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        ShowChrome();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void ShowChrome()
    {
        Chrome.Opacity = 1;
        Chrome.IsHitTestVisible = true;
        ClickLayer.Cursor = Cursors.Arrow;
    }

    private void HideChrome()
    {
        Chrome.Opacity = 0;
        Chrome.IsHitTestVisible = false;
        SettingsPanel.Visibility = Visibility.Collapsed;
        ClickLayer.Cursor = Cursors.None;
    }

    // --- video click ---

    private async void ClickLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Ipc == null) return;
        await Ipc.TogglePause();
        if (e.ClickCount == 2)
        {
            await Ipc.TogglePause(); // undo the single-click toggle
            _main.ToggleFullscreen();
        }
    }

    // --- transport ---

    private async void PlayPause_Click(object sender, RoutedEventArgs e) { if (Ipc != null) await Ipc.TogglePause(); }
    private async void Rewind_Click(object sender, RoutedEventArgs e) { if (Ipc != null) await Ipc.SendAsync("seek", -10, "relative"); }
    private async void Forward_Click(object sender, RoutedEventArgs e) { if (Ipc != null) await Ipc.SendAsync("seek", 10, "relative"); }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    public async void ToggleMute()
    {
        if (Ipc == null) return;
        _muted = !_muted;
        await Ipc.SendAsync("set_property", "mute", _muted);
        VolumeButton.Content = _muted ? "" : "";
    }

    private async void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Ipc != null) await Ipc.SetVolume(e.NewValue);
    }

    // manual click/scrub: set value straight from the mouse x — WPF's own
    // move-to-point is unreliable with a custom track template
    private void SetSeekFromMouse(MouseEventArgs e)
    {
        var x = e.GetPosition(SeekSlider).X;
        SeekSlider.Value = Math.Clamp(x / SeekSlider.ActualWidth, 0, 1) * 100.0;
    }

    private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = true;
        SetSeekFromMouse(e);
    }

    private void SeekSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isSeeking && e.LeftButton == MouseButtonState.Pressed)
            SetSeekFromMouse(e);
    }

    // fires for both clicks and thumb drags (thumb capture tunnels Preview events through the slider)
    private async void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSeeking && Ipc != null && _duration > 0)
            await Ipc.SeekAbsolute(SeekSlider.Value / 100.0 * _duration);
        _isSeeking = false;
    }

    public void AdjustVolume(double delta) =>
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 150);

    // --- right cluster ---

    private async void SubToggle_Click(object sender, RoutedEventArgs e) { if (Ipc != null) await Ipc.SendAsync("cycle", "sub-visibility"); }
    private async void AudioCycle_Click(object sender, RoutedEventArgs e) { if (Ipc != null) await Ipc.SendAsync("cycle", "audio"); }
    private void Sidebar_Click(object sender, RoutedEventArgs e) => _main.ToggleSidebar();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => _main.ToggleFullscreen();

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    // --- settings rows ---

    private async void Fit_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || Ipc == null) return;
        if (FitFill.IsChecked == true)
        {
            await Ipc.SendAsync("set_property", "keepaspect", false);
        }
        else
        {
            await Ipc.SendAsync("set_property", "keepaspect", true);
            await Ipc.SendAsync("set_property", "panscan", FitCover.IsChecked == true ? 1.0 : 0.0);
        }
    }

    private async void Speed_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || Ipc == null) return;
        double speed = Speed2.IsChecked == true ? 2.0
                     : Speed15.IsChecked == true ? 1.5
                     : Speed125.IsChecked == true ? 1.25 : 1.0;
        await Ipc.SendAsync("set_property", "speed", speed);
    }

    private async void Display_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || Ipc == null) return;
        if (DispHdr.IsChecked == true)
            await Ipc.SendAsync("apply-profile", "4k-hdr");
        else
            await Ipc.SendAsync("apply-profile", "4k-hdr", "restore");
    }

    private async void Anime4k_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || Ipc == null) return;

        if (A4kOff.IsChecked == true)
        {
            await Ipc.SendAsync("change-list", "glsl-shaders", "clr", "");
            return;
        }

        string[] files = A4kA.IsChecked == true
            ? new[] { "Anime4K_Clamp_Highlights", "Anime4K_Restore_CNN_VL", "Anime4K_Upscale_CNN_x2_VL",
                      "Anime4K_AutoDownscalePre_x2", "Anime4K_AutoDownscalePre_x4", "Anime4K_Upscale_CNN_x2_M" }
            : A4kB.IsChecked == true
            ? new[] { "Anime4K_Clamp_Highlights", "Anime4K_Restore_CNN_Soft_VL", "Anime4K_Upscale_CNN_x2_VL",
                      "Anime4K_AutoDownscalePre_x2", "Anime4K_AutoDownscalePre_x4", "Anime4K_Upscale_CNN_x2_M" }
            : new[] { "Anime4K_Clamp_Highlights", "Anime4K_Upscale_Denoise_CNN_x2_VL",
                      "Anime4K_AutoDownscalePre_x2", "Anime4K_AutoDownscalePre_x4", "Anime4K_Upscale_CNN_x2_M" };

        var list = string.Join(";", Array.ConvertAll(files, f => $"{ShaderDir}/{f}.glsl"));
        await Ipc.SendAsync("change-list", "glsl-shaders", "set", list);
    }
}
