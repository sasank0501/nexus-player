using System.Collections.Generic;

namespace MpvFrontend;

/// <summary>How a binding is serviced.</summary>
public enum KeyHandling
{
    /// <summary>Handled in the app, because it changes state the UI displays.</summary>
    App,
    /// <summary>Forwarded to mpv via `keypress`, so mpv's own binding and OSD run.</summary>
    Mpv,
}

public record Shortcut(string Keys, string Description, KeyHandling Handling, string? MpvKey = null);

/// <summary>
/// The single source of truth for keyboard behaviour: the hotkey handler, the
/// button tooltips and the shortcuts overlay all read this table, so they cannot
/// drift apart.
///
/// Anything that changes state the UI shows (pause, volume, tracks, speed) is
/// handled in-app so the controls stay in sync. The long tail is forwarded to
/// mpv, which already implements it and draws its own OSD feedback.
/// </summary>
public static class KeyMap
{
    public static readonly IReadOnlyList<(string Group, IReadOnlyList<Shortcut> Items)> Groups = new List<(string, IReadOnlyList<Shortcut>)>
    {
        ("Playback", new List<Shortcut>
        {
            new("Space", "Play / pause", KeyHandling.App),
            new("Left / Right", "Seek 5 seconds", KeyHandling.App),
            new("Shift+Left / Right", "Seek 1 second (exact)", KeyHandling.Mpv),
            new("Up / Down", "Volume up / down", KeyHandling.App),
            new("Page Up / Page Down", "Next / previous chapter", KeyHandling.App),
            new(", / .", "Step one frame back / forward", KeyHandling.Mpv),
            new("[ / ]", "Speed down / up by 10%", KeyHandling.Mpv),
            new("{ / }", "Halve / double speed", KeyHandling.Mpv),
            new("Backspace", "Reset speed to normal", KeyHandling.Mpv),
            new("< / >", "Previous / next in queue", KeyHandling.App),
            new("L", "Set A-B loop points", KeyHandling.Mpv),
        }),

        ("Audio", new List<Shortcut>
        {
            new("M", "Mute", KeyHandling.App),
            new("9 / 0", "Volume down / up", KeyHandling.Mpv),
            new("#", "Cycle audio track", KeyHandling.Mpv),
            new("Ctrl+Plus / Ctrl+Minus", "Audio delay +/- 100ms", KeyHandling.Mpv),
        }),

        ("Subtitles", new List<Shortcut>
        {
            new("V", "Toggle subtitle visibility", KeyHandling.Mpv),
            new("J / Shift+J", "Cycle subtitle track forward / back", KeyHandling.Mpv),
            new("Z / Shift+Z", "Subtitle delay -/+ 100ms", KeyHandling.Mpv),
            new("R / Shift+R", "Move subtitles up / down", KeyHandling.Mpv),
        }),

        ("Video", new List<Shortcut>
        {
            new("1 / 2", "Contrast down / up", KeyHandling.Mpv),
            new("3 / 4", "Brightness down / up", KeyHandling.Mpv),
            new("5 / 6", "Gamma down / up", KeyHandling.Mpv),
            new("7 / 8", "Saturation down / up", KeyHandling.Mpv),
            new("D", "Toggle deinterlacer", KeyHandling.Mpv),
            new("A", "Cycle aspect ratio override", KeyHandling.Mpv),
            new("Ctrl+H", "Toggle hardware decoding", KeyHandling.Mpv),
            new("Alt+arrows", "Pan the picture", KeyHandling.Mpv),
        }),

        ("Window", new List<Shortcut>
        {
            new("F / F11", "Toggle fullscreen", KeyHandling.App),
            new("Esc", "Leave fullscreen", KeyHandling.App),
            new("Ctrl+P", "Picture-in-picture", KeyHandling.App),
            new("Tab", "Show / hide the queue panel", KeyHandling.App),
        }),

        ("Capture and info", new List<Shortcut>
        {
            new("S", "Screenshot with subtitles", KeyHandling.Mpv, "s"),
            new("Shift+S", "Screenshot without subtitles", KeyHandling.Mpv, "S"),
            new("Ctrl+S", "Screenshot exactly as displayed", KeyHandling.Mpv, "Ctrl+s"),
            new("I", "Video stats overlay", KeyHandling.Mpv),
            new("O", "Toggle mpv's on-screen progress", KeyHandling.Mpv),
            new("/", "Show this shortcut list", KeyHandling.App),
        }),
    };
}
