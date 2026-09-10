using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MpvFrontend;

// Single source of truth for everything persisted between runs. v1 scattered
// this: volume lived in code (SetVolume(35)), in XAML (Value="35") and in the
// user's mpv.conf simultaneously, and nothing but position survived a restart.
public sealed class Settings
{
    // --- playback state worth remembering ---
    public double Volume { get; set; } = 35;
    public bool Muted { get; set; }
    public double Speed { get; set; } = 1.0;
    public string VideoFit { get; set; } = "Contain";   // Contain | Cover | Fill
    public int StreamQuality { get; set; }              // max height for links; 0 = best available

    // --- picture ---
    public string Anime4kPreset { get; set; } = "Off";  // Off | A | B | C | AA | BB | CA
    public bool Anime4kDarkenLines { get; set; }
    public bool Anime4kThinLines { get; set; }
    public string ShaderTier { get; set; } = "VL";      // VL | L | M | S
    public bool AutoSwitchDisplayProfile { get; set; } = true;

    // --- environment ---
    public string? MpvPath { get; set; }                 // null = auto-locate
    public List<string> LibraryRoots { get; set; } = new();
    public string? TmdbApiKey { get; set; }

    // --- window ---
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool SidebarVisible { get; set; } = true;

    [JsonIgnore] private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile));
                if (s != null) return s.WithDefaults();
            }
        }
        catch (Exception ex) { Log.Error("Settings load failed; starting from defaults", ex); }
        return new Settings().WithDefaults();
    }

    // seed the library with the folders the user actually keeps media in, but
    // only on a genuinely fresh install — never re-add roots they removed
    private Settings WithDefaults()
    {
        // Normalise separators once, here. A root stored as "D:/Movies" satisfies
        // Directory.Exists but makes the Windows shell throw when it is handed to
        // a file dialog, which crashed the app.
        for (var i = 0; i < LibraryRoots.Count; i++)
        {
            try { LibraryRoots[i] = Path.GetFullPath(LibraryRoots[i]).TrimEnd(Path.DirectorySeparatorChar); }
            catch { /* leave malformed entries alone; callers check Exists */ }
        }

        if (LibraryRoots.Count == 0 && !File.Exists(AppPaths.SettingsFile))
        {
            foreach (var candidate in new[]
                     {
                         @"D:\Movies",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                         Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                     })
            {
                if (Directory.Exists(candidate)) LibraryRoots.Add(candidate);
            }
        }
        return this;
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureAll();
            // write-then-replace so a crash mid-write can't leave a truncated file
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex) { Log.Error("Settings save failed", ex); }
    }
}
