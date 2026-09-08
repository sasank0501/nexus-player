using System;
using System.Collections.Generic;
using System.IO;

namespace MpvFrontend;

// v1 hardcoded mpv to a path inside Downloads. If that folder is ever cleaned,
// Process.Start throws inside an async void handler and the app dies on launch
// with no message at all. Look in the sensible places instead, and let the
// result be overridden in settings.
public static class MpvLocator
{
    public static IEnumerable<string> Candidates(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.MpvPath))
            yield return settings.MpvPath!;

        // shipped next to our own exe (portable install)
        var appDir = AppContext.BaseDirectory;
        yield return Path.Combine(appDir, "mpv.exe");
        yield return Path.Combine(appDir, "mpv", "mpv.exe");

        // on PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim(), "mpv.exe"); } catch { continue; }
            yield return full;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var p in new[]
                 {
                     Path.Combine(home, "Downloads", "bootstrapper", "mpv.exe"), // where v1 found it
                     Path.Combine(home, "scoop", "apps", "mpv", "current", "mpv.exe"),
                     Path.Combine(localApp, "Programs", "mpv", "mpv.exe"),
                     @"C:\Program Files\mpv\mpv.exe",
                     @"C:\ProgramData\chocolatey\bin\mpv.exe",
                 })
            yield return p;
    }

    // returns null when mpv genuinely can't be found — caller prompts the user
    public static string? Locate(Settings settings)
    {
        foreach (var c in Candidates(settings))
        {
            try
            {
                if (File.Exists(c))
                {
                    Log.Info($"mpv resolved to {c}");
                    return c;
                }
            }
            catch { /* malformed PATH entry */ }
        }
        Log.Error("mpv.exe could not be located in any known location");
        return null;
    }
}
