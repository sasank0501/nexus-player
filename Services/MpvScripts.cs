using System;
using System.Collections.Generic;
using System.IO;

namespace MpvFrontend;

/// <summary>
/// Decides which of the user's mpv scripts this app's mpv gets to load.
///
/// mpv auto-loads everything in its scripts directory, which is right for
/// standalone mpv and wrong here: quality-menu.lua also owns ytdl-format. It
/// registers an on_load hook at priority 9, deliberately ahead of ytdl_hook's
/// 10, and rewrites file-local-options/ytdl-format from its own cached format
/// ids on every load. A file-local option outranks the global property, so the
/// rendition our Quality menu asked for never survived the reload — the menu
/// closed and the video came back at whatever quality-menu had pinned. Both
/// systems also ran their own reload, so one click produced three.
///
/// So the app turns auto-loading off and names the scripts it wants. This is
/// scoped to the app's own mpv process; standalone mpv is untouched and still
/// loads every script including quality-menu.
/// </summary>
public static class MpvScripts
{
    /// <summary>
    /// Scripts the app loads. autoload.lua is load-bearing — it is what queues
    /// the rest of a folder when one file opens. The other two are carried
    /// across because nothing about them conflicts with the app; only
    /// quality-menu.lua is deliberately left out.
    /// </summary>
    public static readonly IReadOnlyList<string> Wanted = new[]
    {
        "autoload.lua",
        "pause_after_chapter.lua",
        "SmartCopyPaste.lua",
    };

    /// <summary>
    /// Where mpv would look for scripts, resolved the way mpv resolves it: a
    /// portable_config beside the executable replaces the roaming config
    /// entirely rather than adding to it.
    /// </summary>
    public static string? ScriptsDir(string mpvPath)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(mpvPath));
            if (exeDir != null)
            {
                var portable = Path.Combine(exeDir, "portable_config");
                if (Directory.Exists(portable))
                {
                    var dir = Path.Combine(portable, "scripts");
                    return Directory.Exists(dir) ? dir : null;
                }
            }

            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "mpv", "scripts");
            return Directory.Exists(roaming) ? roaming : null;
        }
        catch (Exception ex)
        {
            Log.Error($"could not resolve mpv's scripts directory: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Launch arguments that pin the script set. Always disables auto-loading,
    /// even when no scripts directory exists, so the app's mpv behaves the same
    /// on a machine that has never run mpv standalone.
    ///
    /// This does not disable mpv's built-in scripts: ytdl_hook, the stats
    /// overlay and the console are governed by --ytdl, --load-stats-overlay and
    /// --load-console respectively, so streaming still resolves. Checked against
    /// mpv --list-options rather than assumed.
    /// </summary>
    public static IEnumerable<string> LaunchArgs(string mpvPath)
    {
        yield return "--load-scripts=no";

        var dir = ScriptsDir(mpvPath);
        if (dir == null)
        {
            Log.Info("mpv scripts: no scripts directory found; auto-loading off");
            yield break;
        }

        var loaded = new List<string>();
        foreach (var name in Wanted)
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full))
            {
                loaded.Add(name);
                yield return $"--script={full}";
            }
        }

        var skipped = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (!loaded.Contains(name)) skipped.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"could not list {dir}: {ex.Message}");
        }

        Log.Info($"mpv scripts: loading {Fmt(loaded)}; not loading {Fmt(skipped)}");
    }

    private static string Fmt(List<string> names) =>
        names.Count == 0 ? "none" : string.Join(", ", names);
}
