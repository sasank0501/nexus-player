using System;
using System.Collections.Generic;
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
// alias: System.IO.Path is used here too, and bare `Path` is ambiguous
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;

namespace MpvFrontend;

public partial class OverlayWindow : Window
{
    private readonly MainWindow _main;
    private readonly DispatcherTimer _hideTimer;

    private bool _isSeeking;
    private double _duration;
    private bool _muted;
    private bool _ready;              // suppresses Checked events raised during InitializeComponent
    private bool _isFullscreen;
    private bool _suppressVolumeEcho;
    private bool _pointerOverChrome;  // debounces auto-hide against a resting cursor
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _clickTimer;

    private static readonly string ShaderDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mpv", "shaders", "Anime4K");

    public OverlayWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        BuildFiltersTab();
        BuildShortcutsOverlay();
        _ready = true;

        PreviewTextInput += (_, e) => _main.ForwardTextToMpv(e);

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _hideTimer.Tick += (_, _) => HideChrome();
        _hideTimer.Start();
    }

    private MpvIpcClient? Ipc => _main.Ipc;
    private Settings Config => _main.Config;

    // WS_EX_NOACTIVATE: clicks on the overlay must never make it the foreground
    // window. The main (non-layered) window then stays foreground, which lets
    // Windows' native fullscreen detection put the taskbar behind the video —
    // the same mechanism VLC and mpv rely on. Keyboard also always stays on main.
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

    private IntPtr RefuseMouseActivation(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;
        if (msg == WM_MOUSEACTIVATE)
        {
            // The overlay must never activate, but the click should still focus the
            // player: without this, clicking the video while another app is
            // foreground leaves keyboard focus, and every hotkey, in that app.
            if (!_main.IsActive) _main.Activate();
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern int GetDoubleClickTime();

    // ================= state pushed in from mpv observers =================

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

    public void UpdatePause(bool paused)
    {
        PlayPauseButton.Tag = FindResource(paused ? "IconPlay" : "IconPause");
        // a paused player keeps its controls up: hiding them mid-pause reads as a freeze
        if (paused) ShowChrome();
    }

    public void SetNowPlaying(string title, string sub)
    {
        NowPlayingTitle.Text = title;
        NowPlayingSub.Text = sub;
        TitleText.Text = title;
        EpisodeText.Text = sub;
        TitleBlock.Visibility = Visibility.Visible;
    }

    public void SetQueuePosition(int index, int count)
    {
        if (count > 1 && index >= 0)
        {
            EpPillText.Text = $"EP {index + 1} / {count}";
            EpPill.Visibility = Visibility.Visible;
        }
        else EpPill.Visibility = Visibility.Collapsed;
        NextButton.IsEnabled = index >= 0 && index < count - 1;
    }

    public void UpdateVolume(double volume)
    {
        if (Math.Abs(VolumeSlider.Value - volume) < 0.5) return;
        _suppressVolumeEcho = true;
        VolumeSlider.Value = volume;
        _suppressVolumeEcho = false;
    }

    public void UpdateMute(bool muted)
    {
        _muted = muted;
        VolumeButton.Tag = FindResource(muted ? "IconVolumeMuted" : "IconVolume");
    }

    public void UpdateSpeed(double speed) =>
        SpeedValue.Text = Math.Abs(speed - 1.0) < 0.01 ? "Normal" : $"{speed:0.##}x";

    public void ApplyPersistedState(Settings s)
    {
        _ready = false;
        FitContain.IsChecked = s.VideoFit == "Contain";
        FitCover.IsChecked = s.VideoFit == "Cover";
        FitFill.IsChecked = s.VideoFit == "Fill";
        TierVL.IsChecked = s.ShaderTier == "VL";
        TierL.IsChecked = s.ShaderTier == "L";
        TierM.IsChecked = s.ShaderTier == "M";
        TierS.IsChecked = s.ShaderTier == "S";
        DarkenLines.IsChecked = s.Anime4kDarkenLines;
        ThinLines.IsChecked = s.Anime4kThinLines;
        UpscalerEnabled.IsChecked = s.Anime4kPreset != "Off";
        SelectPresetRadio(s.Anime4kPreset);
        _ready = true;

        UpdateVolume(s.Volume);
        UpdateMute(s.Muted);
        UpdateSpeed(s.Speed);
        UpscalerValue.Text = PresetLabel(s.Anime4kPreset);
    }

    public void SetFullscreenState(bool isFullscreen)
    {
        _isFullscreen = isFullscreen;
        FullscreenButton.Tag = FindResource(isFullscreen ? "IconFullscreenExit" : "IconFullscreen");
    }

    // Failures used to be entirely invisible — a dead loadfile just left a black
    // frame. Anything routed through Log.UserError surfaces here.
    public void ShowToast(string message, int milliseconds = 5000)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            _toastTimer = null;
            Toast.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    private static string Fmt(double s) =>
        TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    // ============================= auto-hide =============================

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        _pointerOverChrome = e.GetPosition(this).Y > ActualHeight - 130;
        ShowChrome();
        RestartHideTimer();
    }

    private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        AdjustVolume(e.Delta > 0 ? 5 : -5);
        ShowChrome();
        RestartHideTimer();
        e.Handled = true;
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private bool AnyPanelOpen =>
        SettingsPanel.Visibility == Visibility.Visible ||
        EpisodesPanel.Visibility == Visibility.Visible ||
        MoreMenu.Visibility == Visibility.Visible ||
        VideoProcessing.Visibility == Visibility.Visible ||
        ShortcutsOverlay.Visibility == Visibility.Visible;

    private void ShowChrome()
    {
        Chrome.Opacity = 1;
        Chrome.IsHitTestVisible = true;
        ClickLayer.Cursor = Cursors.Arrow;
    }

    // Never hide while a menu is open, a drag is in progress, or the cursor is
    // resting over the controls. Timing purely from the last event is what makes
    // other players flicker the bar in and out under a stationary cursor.
    private void HideChrome()
    {
        if (_isSeeking || _pointerOverChrome || AnyPanelOpen) return;
        Chrome.Opacity = 0;
        Chrome.IsHitTestVisible = false;
        ClickLayer.Cursor = Cursors.None;
    }

    // =========================== click on video ===========================

    // v1 toggled pause immediately and toggled it back on the second click, so the
    // play icon visibly blinked on every double-click to fullscreen. Hold the
    // single-click action until the double-click window has passed.
    private void ClickLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelPendingClick();
        if (e.ClickCount >= 2)
        {
            _main.ToggleFullscreen();
            return;
        }
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _clickTimer.Tick += (_, _) =>
        {
            CancelPendingClick();
            if (AnyPanelOpen) { CloseAllPanels(); return; }
            Fire.AndForget(Ipc?.TogglePause(), "click to pause");
        };
        _clickTimer.Start();
    }

    private void CancelPendingClick()
    {
        _clickTimer?.Stop();
        _clickTimer = null;
    }

    // ============================== transport ==============================

    private void PlayPause_Click(object s, RoutedEventArgs e) => Fire.AndForget(Ipc?.TogglePause(), "toggle pause");
    private void Rewind_Click(object s, RoutedEventArgs e) => Fire.AndForget(Ipc?.SeekRelative(-10), "seek back");
    private void Forward_Click(object s, RoutedEventArgs e) => Fire.AndForget(Ipc?.SeekRelative(10), "seek forward");
    private void Next_Click(object s, RoutedEventArgs e) => Fire.AndForget(Ipc?.PlaylistNext(), "next in queue");
    private void Mute_Click(object s, RoutedEventArgs e) => ToggleMute();
    private void Fullscreen_Click(object s, RoutedEventArgs e) => _main.ToggleFullscreen();
    private void Pip_Click(object s, RoutedEventArgs e) => _main.TogglePictureInPicture();

    public void ToggleMute() => Fire.AndForget(Ipc?.SetProperty("mute", !_muted), "toggle mute");

    public void AdjustVolume(double delta) =>
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 150);

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolumeEcho) return; // the value came from mpv; don't send it back
        Fire.AndForget(Ipc?.SetVolume(e.NewValue), "set volume");
    }

    // ================================ seeking ================================

    // Manual click/scrub: set the value straight from the mouse x. WPF's own
    // move-to-point silently does nothing with a custom track template.
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
        if (_isSeeking && e.LeftButton == MouseButtonState.Pressed) SetSeekFromMouse(e);
    }

    // Fires for both clicks and thumb drags (thumb capture tunnels preview events
    // through the slider). SeekAbsolute awaits mpv's acknowledgement, so the
    // _isSeeking guard stays up until position reports are trustworthy again —
    // otherwise one stale pre-seek time-pos snaps the bar backwards.
    private async void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_isSeeking && Ipc != null && _duration > 0)
                await Ipc.SeekAbsolute(SeekSlider.Value / 100.0 * _duration);
        }
        catch (Exception ex) { Log.Error("seek failed", ex); }
        finally { _isSeeking = false; }
    }

    // Hover readout on the bar. The frame preview itself needs a second headless
    // mpv instance and lands with the thumbnail work; the timestamp is useful now.
    private void SeekSlider_HoverMove(object sender, MouseEventArgs e)
    {
        if (_duration <= 0) return;
        var frac = Math.Clamp(e.GetPosition(SeekSlider).X / SeekSlider.ActualWidth, 0, 1);
        ScrubTime.Text = Fmt(frac * _duration);
        ScrubPreview.Visibility = Visibility.Visible;
        var px = e.GetPosition(this).X;
        ScrubPreview.Margin = new Thickness(
            Math.Clamp(px - 91, 8, Math.Max(8, ActualWidth - 190)), 0, 0, 118);
    }

    private void SeekSlider_HoverLeave(object sender, MouseEventArgs e) =>
        ScrubPreview.Visibility = Visibility.Collapsed;

    // =============================== panels ===============================

    private void CloseAllPanels()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        EpisodesPanel.Visibility = Visibility.Collapsed;
        MoreMenu.Visibility = Visibility.Collapsed;
        VideoProcessing.Visibility = Visibility.Collapsed;
        ShortcutsOverlay.Visibility = Visibility.Collapsed;
        ShowSettingsRoot();
    }

    private void TogglePanel(FrameworkElement panel)
    {
        var wasOpen = panel.Visibility == Visibility.Visible;
        CloseAllPanels();
        panel.Visibility = wasOpen ? Visibility.Collapsed : Visibility.Visible;
        ShowChrome();
        RestartHideTimer();
    }

    private void Settings_Click(object s, RoutedEventArgs e) => TogglePanel(SettingsPanel);
    private void More_Click(object s, RoutedEventArgs e) => TogglePanel(MoreMenu);

    // The queue button does two jobs: hovering peeks at the queue so you can pick
    // an item without committing to anything, clicking opens the docked sidebar.
    // The close is delayed because the pointer has to cross a gap between the
    // button and the flyout, and closing on the first MouseLeave makes that
    // journey impossible.
    private DispatcherTimer? _queuePeekTimer;

    private void QueuePeek_Enter(object s, MouseEventArgs e)
    {
        _queuePeekTimer?.Stop();
        _queuePeekTimer = null;
        if (EpisodesPanel.Visibility == Visibility.Visible) return;

        RefreshQueue();
        SettingsPanel.Visibility = Visibility.Collapsed;
        MoreMenu.Visibility = Visibility.Collapsed;
        EpisodesPanel.Visibility = Visibility.Visible;
        ShowChrome();
        RestartHideTimer();
    }

    private void QueuePeek_Leave(object s, MouseEventArgs e)
    {
        _queuePeekTimer?.Stop();
        _queuePeekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _queuePeekTimer.Tick += (_, _) =>
        {
            _queuePeekTimer?.Stop();
            _queuePeekTimer = null;
            // still inside either surface? then the pointer only crossed the gap
            if (QueueButton.IsMouseOver || EpisodesPanel.IsMouseOver) return;
            EpisodesPanel.Visibility = Visibility.Collapsed;
        };
        _queuePeekTimer.Start();
    }

    private void QueueButton_Click(object s, RoutedEventArgs e)
    {
        _queuePeekTimer?.Stop();
        _queuePeekTimer = null;
        EpisodesPanel.Visibility = Visibility.Collapsed;
        _main.ToggleSidebar();
    }

    public void ToggleQueuePanel()
    {
        RefreshQueue();
        TogglePanel(EpisodesPanel);
    }

    public void ToggleShortcuts() => TogglePanel(ShortcutsOverlay);

    // =========================== settings drill-in ===========================

    private void ShowSettingsRoot()
    {
        SettingsRoot.Visibility = Visibility.Visible;
        SettingsSub.Visibility = Visibility.Collapsed;
        SubPanelItems.Children.Clear();
    }

    private void SettingsBack_Click(object s, RoutedEventArgs e) => ShowSettingsRoot();

    private void ShowSubPanel(string title)
    {
        SubPanelTitle.Text = title;
        SubPanelItems.Children.Clear();
        SettingsRoot.Visibility = Visibility.Collapsed;
        SettingsSub.Visibility = Visibility.Visible;
    }

    /// <summary>One selectable row inside a drill-in panel, with an optional tick.</summary>
    private void AddSubRow(string label, bool selected, Action onClick, string? detail = null)
    {
        var grid = new Grid();
        var left = new StackPanel { Orientation = Orientation.Horizontal };

        left.Children.Add(new ShapePath
        {
            Style = (Style)FindResource("IconPath"),
            Data = (Geometry)FindResource("IconCheck"),
            Stroke = (Brush)FindResource("Text"),
            Width = 13,
            Height = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = selected ? Visibility.Visible : Visibility.Hidden,
        });

        left.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("MenuRowLabel"),
            MaxWidth = 205,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
        });
        grid.Children.Add(left);

        if (detail != null)
        {
            grid.Children.Add(new TextBlock
            {
                Text = detail,
                Style = (Style)FindResource("MenuRowValue"),
                HorizontalAlignment = HorizontalAlignment.Right,
            });
        }

        var button = new Button { Style = (Style)FindResource("MenuRow"), Content = grid };
        button.Click += (_, _) => onClick();
        SubPanelItems.Children.Add(button);
    }

    private void OpenPrefs_Click(object s, RoutedEventArgs e)
    {
        ShowSubPanel("Playback Preferences");
        AddSubRow($"Library: {Config.LibraryRoots.Count} folder(s)", false,
            () => ShowToast(string.Join("     ", Config.LibraryRoots), 7000));
        AddSubRow("Open settings file", false, () => OpenInExplorer(AppPaths.SettingsFile));
        AddSubRow("Open log folder", false, () => OpenInExplorer(AppPaths.LogFile));
        AddSubRow($"mpv: {Path.GetFileName(Config.MpvPath ?? "auto-located")}", false,
            () => ShowToast(Config.MpvPath ?? "Located automatically", 6000));
    }

    private void OpenSpeed_Click(object s, RoutedEventArgs e)
    {
        ShowSubPanel("Speed");
        foreach (var value in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            var speed = value;
            AddSubRow(Math.Abs(speed - 1.0) < 0.01 ? "Normal" : $"{speed:0.##}x",
                Math.Abs(Config.Speed - speed) < 0.01,
                () =>
                {
                    Fire.AndForget(Ipc?.SetProperty("speed", speed), "set speed");
                    ShowSettingsRoot();
                });
        }
    }

    private void OpenQuality_Click(object s, RoutedEventArgs e)
    {
        ShowSubPanel("Quality");
        foreach (var entry in new[]
                 {
                     ("Auto (best)", "bestvideo+bestaudio/best"),
                     ("2160p", "bestvideo[height<=2160]+bestaudio/best"),
                     ("1440p", "bestvideo[height<=1440]+bestaudio/best"),
                     ("1080p", "bestvideo[height<=1080]+bestaudio/best"),
                     ("720p", "bestvideo[height<=720]+bestaudio/best"),
                 })
        {
            var (label, format) = entry;
            AddSubRow(label, QualityValue.Text == label, () =>
            {
                QualityValue.Text = label;
                Fire.AndForget(Ipc?.SetProperty("ytdl-format", format), "set quality");
                ShowToast("Quality applies to the next link you play.", 4000);
                ShowSettingsRoot();
            });
        }
    }

    private void OpenAudio_Click(object s, RoutedEventArgs e) =>
        Fire.AndForget(ShowTrackPanel("audio", "Audio", "aid", AudioValue), "audio tracks");

    private void OpenSubs_Click(object s, RoutedEventArgs e) =>
        Fire.AndForget(ShowTrackPanel("sub", "Subtitles", "sid", SubsValue), "subtitle tracks");

    private void SubToggle_Click(object s, RoutedEventArgs e)
    {
        CloseAllPanels();
        SettingsPanel.Visibility = Visibility.Visible;
        OpenSubs_Click(s, e);
    }

    private void AudioCycle_Click(object s, RoutedEventArgs e)
    {
        CloseAllPanels();
        SettingsPanel.Visibility = Visibility.Visible;
        OpenAudio_Click(s, e);
    }

    /// <summary>
    /// Track picker as a drill-in panel. Audio and subtitles stay independent
    /// axes here — some streaming players tie subtitle choice to the audio dub,
    /// which is precisely the behaviour worth not copying.
    /// </summary>
    private async Task ShowTrackPanel(string type, string title, string property, TextBlock valueLabel)
    {
        ShowSubPanel(title);
        if (Ipc == null) return;

        var tracks = await Ipc.RequestAsync("get_property", "track-list");
        SubPanelItems.Children.Clear();

        if (tracks.ValueKind != JsonValueKind.Array)
        {
            AddSubRow("Nothing playing", false, () => { });
            return;
        }

        var rows = new List<(string Label, string Detail, long Id, bool Selected)>();
        var anySelected = false;
        foreach (var t in tracks.EnumerateArray())
        {
            if (!t.TryGetProperty("type", out var ty) || ty.GetString() != type) continue;
            var selected = t.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.True;
            anySelected |= selected;
            rows.Add((DescribeTrack(t), DetailOf(t), t.GetProperty("id").GetInt64(), selected));
        }

        if (type == "sub")
        {
            AddSubRow("Off", !anySelected, () =>
            {
                Fire.AndForget(Ipc?.SetProperty("sid", "no"), "disable subtitles");
                valueLabel.Text = "Off";
                ShowSettingsRoot();
            });
        }

        foreach (var entry in rows)
        {
            var row = entry;
            AddSubRow(row.Label, row.Selected, () =>
            {
                Fire.AndForget(Ipc?.SetProperty(property, row.Id), "select track");
                if (property == "sid") Fire.AndForget(Ipc?.SetProperty("sub-visibility", true), "show subtitles");
                valueLabel.Text = row.Label;
                ShowSettingsRoot();
            }, row.Detail);

            if (row.Selected) valueLabel.Text = row.Label;
        }

        if (rows.Count == 0)
            AddSubRow(type == "audio" ? "No audio tracks" : "No subtitles", false, () => { });
    }

    private static string DetailOf(JsonElement t)
    {
        string? S(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var channels = t.TryGetProperty("demux-channel-count", out var c) && c.TryGetInt32(out var n) ? n : 0;
        return string.Join(" ", new[] { S("codec")?.ToUpperInvariant(), channels > 0 ? channels + "ch" : null }
            .Where(x => !string.IsNullOrEmpty(x)));
    }

    private static string DescribeTrack(JsonElement t)
    {
        string? S(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var lang = S("lang");
        var title = S("title");
        var forced = t.TryGetProperty("forced", out var fr) && fr.ValueKind == JsonValueKind.True;

        string name;
        if (!string.IsNullOrWhiteSpace(lang) && !string.IsNullOrWhiteSpace(title))
            name = LanguageName(lang!) + " - " + title;
        else if (!string.IsNullOrWhiteSpace(title))
            name = title!;
        else if (!string.IsNullOrWhiteSpace(lang))
            name = LanguageName(lang!);
        else
            name = "Track " + t.GetProperty("id").GetInt64();

        return forced ? name + " (forced)" : name;
    }

    // Full language names rather than bare ISO codes: the library has HIN/TAM/TEL
    // multi-audio releases and Japanese dual-audio anime, where "Track 2" says
    // nothing useful.
    private static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "eng" or "en" => "English",
        "jpn" or "ja" or "jp" => "Japanese",
        "spa" or "es" or "spn" => "Spanish",
        "fre" or "fra" or "fr" => "French",
        "ger" or "deu" or "de" => "German",
        "ita" or "it" => "Italian",
        "por" or "pt" => "Portuguese",
        "rus" or "ru" => "Russian",
        "kor" or "ko" => "Korean",
        "chi" or "zho" or "zh" => "Chinese",
        "hin" or "hi" => "Hindi",
        "tam" or "ta" => "Tamil",
        "tel" or "te" => "Telugu",
        "ara" or "ar" => "Arabic",
        _ => code.ToUpperInvariant(),
    };

    // ======================== video processing modal ========================

    private void OpenFilters_Click(object s, RoutedEventArgs e) => OpenVideoProcessing(filters: true);
    private void OpenUpscaler_Click(object s, RoutedEventArgs e) => OpenVideoProcessing(filters: false);

    private void OpenVideoProcessing(bool filters)
    {
        CloseAllPanels();
        _ready = false;
        TabFilters.IsChecked = filters;
        TabAnime4k.IsChecked = !filters;
        _ready = true;
        Anime4kTab.Visibility = filters ? Visibility.Collapsed : Visibility.Visible;
        FiltersTab.Visibility = filters ? Visibility.Visible : Visibility.Collapsed;
        VideoProcessing.Visibility = Visibility.Visible;
        Fire.AndForget(RefreshGpuReadout(), "gpu readout");
    }

    private void CloseVideoProcessing_Click(object s, RoutedEventArgs e) =>
        VideoProcessing.Visibility = Visibility.Collapsed;

    private void VpTab_Checked(object s, RoutedEventArgs e)
    {
        if (!_ready) return;
        Anime4kTab.Visibility = TabAnime4k.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FiltersTab.Visibility = TabFilters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // The reference shows "WebGPU supported" here; the honest local equivalent is
    // what mpv actually negotiated.
    private async Task RefreshGpuReadout()
    {
        if (Ipc == null) return;
        var api = await Ipc.GetStringAsync("gpu-api");
        var hwdec = await Ipc.GetStringAsync("hwdec-current");
        GpuReadout.Text = $"mpv {api ?? "gpu"} - hardware decode: {hwdec ?? "none"}";
    }

    // ============================ video filters ============================

    // mpv's equalizer, built in code so each row costs one line rather than 20 of XAML.
    private void BuildFiltersTab()
    {
        foreach (var entry in new[]
                 {
                     ("Brightness", "brightness"),
                     ("Contrast", "contrast"),
                     ("Saturation", "saturation"),
                     ("Gamma", "gamma"),
                     ("Hue", "hue"),
                 })
        {
            var (label, property) = entry;
            var grid = new Grid { Margin = new Thickness(4, 6, 4, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            var name = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = (Brush)FindResource("Text"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);

            var readout = new TextBlock
            {
                Text = "0",
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(readout, 2);

            var slider = new Slider
            {
                Style = (Style)FindResource("MiniSlider"),
                Minimum = -100,
                Maximum = 100,
                Value = 0,
                IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
            };
            slider.ValueChanged += (_, ev) =>
            {
                readout.Text = ((int)ev.NewValue).ToString();
                Fire.AndForget(Ipc?.SetProperty(property, (int)ev.NewValue), "set " + property);
                FiltersValue.Text = AnyFilterActive() ? "Custom" : "Off";
            };
            Grid.SetColumn(slider, 1);

            grid.Children.Add(name);
            grid.Children.Add(slider);
            grid.Children.Add(readout);
            FiltersTab.Children.Add(grid);
        }

        var reset = new Button
        {
            Style = (Style)FindResource("TextButton"),
            Content = "Reset Filters",
            Margin = new Thickness(2, 10, 2, 0),
        };
        reset.Click += (_, _) =>
        {
            foreach (var slider in FilterSliders()) slider.Value = 0;
            FiltersValue.Text = "Off";
        };
        FiltersTab.Children.Add(reset);
    }

    private IEnumerable<Slider> FilterSliders() =>
        FiltersTab.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Slider>());

    private bool AnyFilterActive() => FilterSliders().Any(s => Math.Abs(s.Value) > 0.5);

    // ========================== Anime4K upscaler ==========================

    // The official v4 chains. Clamp_Highlights leads every one; the doubled modes
    // add a second restore pass after the first upscale. {T} is the tier picked in
    // the Performance section — VL looks best, S is cheapest.
    private static readonly Dictionary<string, string[]> Presets = new()
    {
        ["A"] = new[] { "Clamp_Highlights", "Restore_CNN_{T}", "Upscale_CNN_x2_{T}",
                        "AutoDownscalePre_x2", "AutoDownscalePre_x4", "Upscale_CNN_x2_M" },
        ["B"] = new[] { "Clamp_Highlights", "Restore_CNN_Soft_{T}", "Upscale_CNN_x2_{T}",
                        "AutoDownscalePre_x2", "AutoDownscalePre_x4", "Upscale_CNN_x2_M" },
        ["C"] = new[] { "Clamp_Highlights", "Upscale_Denoise_CNN_x2_{T}",
                        "AutoDownscalePre_x2", "AutoDownscalePre_x4", "Upscale_CNN_x2_M" },
        ["AA"] = new[] { "Clamp_Highlights", "Restore_CNN_{T}", "Upscale_CNN_x2_{T}",
                         "Restore_CNN_M", "AutoDownscalePre_x2", "AutoDownscalePre_x4", "Upscale_CNN_x2_M" },
        ["BB"] = new[] { "Clamp_Highlights", "Restore_CNN_Soft_{T}", "Upscale_CNN_x2_{T}",
                         "AutoDownscalePre_x2", "AutoDownscalePre_x4", "Restore_CNN_Soft_M", "Upscale_CNN_x2_M" },
        ["CA"] = new[] { "Clamp_Highlights", "Upscale_Denoise_CNN_x2_{T}",
                         "AutoDownscalePre_x2", "AutoDownscalePre_x4", "Restore_CNN_M", "Upscale_CNN_x2_M" },
    };

    private static string PresetLabel(string key) => key switch
    {
        "A" => "Mode A",
        "B" => "Mode B",
        "C" => "Mode C",
        "AA" => "Mode A+A",
        "BB" => "Mode B+B",
        "CA" => "Mode C+A",
        _ => "Off",
    };

    private void SelectPresetRadio(string key)
    {
        A4kA.IsChecked = key == "A";
        A4kB.IsChecked = key == "B";
        A4kC.IsChecked = key == "C";
        A4kAA.IsChecked = key == "AA";
        A4kBB.IsChecked = key == "BB";
        A4kCA.IsChecked = key == "CA";
    }

    private string CurrentPresetKey() =>
        A4kA.IsChecked == true ? "A" :
        A4kB.IsChecked == true ? "B" :
        A4kC.IsChecked == true ? "C" :
        A4kAA.IsChecked == true ? "AA" :
        A4kBB.IsChecked == true ? "BB" :
        A4kCA.IsChecked == true ? "CA" : "Off";

    private void Anime4k_Checked(object s, RoutedEventArgs e)
    {
        if (!_ready) return;
        _ready = false;
        UpscalerEnabled.IsChecked = true;
        _ready = true;
        ApplyShaders();
    }

    private void Tier_Checked(object s, RoutedEventArgs e)
    {
        if (!_ready) return;
        Config.ShaderTier = TierVL.IsChecked == true ? "VL"
                          : TierL.IsChecked == true ? "L"
                          : TierM.IsChecked == true ? "M" : "S";
        ApplyShaders();
    }

    private void LineEffect_Changed(object s, RoutedEventArgs e)
    {
        if (!_ready) return;
        Config.Anime4kDarkenLines = DarkenLines.IsChecked == true;
        Config.Anime4kThinLines = ThinLines.IsChecked == true;
        ApplyShaders();
    }

    private void UpscalerEnabled_Changed(object s, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (UpscalerEnabled.IsChecked == true && CurrentPresetKey() == "Off")
        {
            _ready = false;
            A4kAA.IsChecked = true; // the reference defaults to A+A
            _ready = true;
        }
        ApplyShaders();
    }

    private void ResetUpscaler_Click(object s, RoutedEventArgs e)
    {
        _ready = false;
        UpscalerEnabled.IsChecked = false;
        SelectPresetRadio("Off");
        DarkenLines.IsChecked = false;
        ThinLines.IsChecked = false;
        TierVL.IsChecked = true;
        _ready = true;

        Config.ShaderTier = "VL";
        Config.Anime4kDarkenLines = false;
        Config.Anime4kThinLines = false;
        ApplyShaders();
    }

    private void ApplyShaders()
    {
        if (Ipc == null) return;

        var key = UpscalerEnabled.IsChecked == true ? CurrentPresetKey() : "Off";
        Config.Anime4kPreset = key;
        UpscalerValue.Text = PresetLabel(key);

        if (key == "Off" || !Presets.TryGetValue(key, out var chain))
        {
            Fire.AndForget(Ipc.SendAsync("change-list", "glsl-shaders", "clr", ""), "clear shaders");
            return;
        }

        var names = chain.Select(n => n.Replace("{T}", Config.ShaderTier)).ToList();
        if (Config.Anime4kDarkenLines) names.Add("Darken_HQ");
        if (Config.Anime4kThinLines) names.Add("Thin_HQ");

        var paths = new List<string>();
        var missing = new List<string>();
        foreach (var n in names)
        {
            var file = Path.Combine(ShaderDir, "Anime4K_" + n + ".glsl");
            if (File.Exists(file)) paths.Add(file.Replace('\\', '/'));
            else missing.Add("Anime4K_" + n + ".glsl");
        }

        // A missing shader used to make the whole chain a silent no-op.
        if (missing.Count > 0)
        {
            Log.UserError("Anime4K shaders missing: " + string.Join(", ", missing));
            return;
        }

        Fire.AndForget(Ipc.SendAsync("change-list", "glsl-shaders", "set", string.Join(";", paths)), "apply shaders");
        Log.Info($"Anime4K {PresetLabel(key)} tier {Config.ShaderTier}: {paths.Count} shaders");
    }

    // ============================== video fit ==============================

    private void Fit_Checked(object s, RoutedEventArgs e)
    {
        if (!_ready || Ipc == null) return;
        Config.VideoFit = FitFill.IsChecked == true ? "Fill"
                        : FitCover.IsChecked == true ? "Cover" : "Contain";

        if (Config.VideoFit == "Fill")
        {
            Fire.AndForget(Ipc.SetProperty("keepaspect", false), "video fit");
        }
        else
        {
            Fire.AndForget(Ipc.SetProperty("keepaspect", true), "video fit");
            Fire.AndForget(Ipc.SetProperty("panscan", Config.VideoFit == "Cover" ? 1.0 : 0.0), "panscan");
        }
    }

    // ============================ more options ============================

    private void Stats_Click(object s, RoutedEventArgs e)
    {
        Fire.AndForget(Ipc?.KeyPress("i"), "toggle stats");
        CloseAllPanels();
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
            Fire.AndForget(Ipc?.KeyPress(key), "screenshot");
        CloseAllPanels();
    }

    private void OpenLogFolder_Click(object s, RoutedEventArgs e)
    {
        OpenInExplorer(AppPaths.LogFile);
        CloseAllPanels();
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            AppPaths.EnsureAll();
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("could not open explorer", ex); }
    }

    // ========================== shortcuts overlay ==========================

    private void Shortcuts_Click(object s, RoutedEventArgs e) => TogglePanel(ShortcutsOverlay);

    private void CloseShortcuts_Click(object s, RoutedEventArgs e) =>
        ShortcutsOverlay.Visibility = Visibility.Collapsed;

    // Built from KeyMap so the list can never drift from what the keys actually do.
    private void BuildShortcutsOverlay()
    {
        foreach (var (group, items) in KeyMap.Groups)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, 26, 18) };
            column.Children.Add(new TextBlock
            {
                Text = group.ToUpperInvariant(),
                Style = (Style)FindResource("SectionHeader"),
                Margin = new Thickness(0, 0, 0, 6),
            });

            foreach (var item in items)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                row.Children.Add(new TextBlock
                {
                    Text = item.Description,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("Text"),
                    MaxWidth = 175,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                row.Children.Add(new TextBlock
                {
                    Text = item.Keys,
                    Style = (Style)FindResource("ShortcutKey"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
                column.Children.Add(row);
            }
            ShortcutColumns.Items.Add(column);
        }
    }

    // =============================== queue ===============================

    public void RefreshQueue()
    {
        QueueItems.Children.Clear();
        var items = _main.QueueSnapshot();
        QueueCount.Text = items.Count == 0 ? "" : items.Count + " ITEMS";

        if (items.Count == 0)
        {
            QueueItems.Children.Add(new TextBlock
            {
                Text = "Nothing queued yet. Add files from the sidebar.",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 4, 10, 10),
            });
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var item = items[i];
            var isCurrent = _main.IsCurrent(item);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 12.5,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource("Text"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = item.Folder,
                FontSize = 10.5,
                Foreground = (Brush)FindResource("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
            grid.Children.Add(text);

            if (isCurrent)
            {
                var tick = new ShapePath
                {
                    Style = (Style)FindResource("IconPath"),
                    Data = (Geometry)FindResource("IconCheck"),
                    Stroke = (Brush)FindResource("Text"),
                    Width = 14,
                    Height = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(tick, 1);
                grid.Children.Add(tick);
            }

            var button = new Button
            {
                Style = (Style)FindResource("MenuRow"),
                Height = 50,
                Content = grid,
                Background = isCurrent ? (Brush)FindResource("SelectedOverlay") : Brushes.Transparent,
            };
            button.Click += (_, _) =>
            {
                _main.PlayQueueIndex(index);
                CloseAllPanels();
            };
            QueueItems.Children.Add(button);
        }
    }
}
