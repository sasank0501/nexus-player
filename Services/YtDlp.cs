using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MpvFrontend;

/// <summary>One selectable video quality for a streamed link.</summary>
public record StreamQuality(int Height, int Fps)
{
    public string Label => Fps >= 50 ? $"{Height}p{Fps}" : $"{Height}p";

    /// <summary>
    /// Cap the height rather than pinning it exactly: an exact match can fail on
    /// videos that lack that rendition, and falling back to "best under N" is
    /// what the viewer actually meant.
    /// </summary>
    public string YtdlFormat => $"bestvideo[height<={Height}]+bestaudio/best[height<={Height}]/best";
}

/// <summary>
/// Asks yt-dlp what a link actually offers. mpv resolves streams through yt-dlp
/// but never exposes the format list over IPC, so the only way to populate a real
/// quality menu is to ask yt-dlp directly — which is what mpv's own quality_menu
/// script does too.
/// </summary>
public static class YtDlp
{
    public static string? Locate(Settings settings, string? mpvPath)
    {
        var candidates = new List<string>();

        // beside our own exe first, matching how mpv is resolved
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe"));

        // then beside whichever mpv we are actually driving
        if (!string.IsNullOrWhiteSpace(mpvPath))
        {
            try
            {
                var dir = Path.GetDirectoryName(mpvPath);
                if (dir != null) candidates.Add(Path.Combine(dir, "yt-dlp.exe"));
            }
            catch { /* malformed path */ }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "yt-dlp.exe")); } catch { }
        }

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    /// <summary>
    /// Distinct video heights the link offers, best first. Empty when yt-dlp is
    /// missing, the site is unsupported, or the lookup fails — callers show that
    /// as a message rather than an empty menu.
    /// </summary>
    public static async Task<IReadOnlyList<StreamQuality>> GetQualitiesAsync(
        string exePath, string url, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "--no-warnings", "--no-playlist", "--dump-single-json", url })
            psi.ArgumentList.Add(a);

        string json;
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return Array.Empty<StreamQuality>();

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            var exited = await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(timeoutMs));

            if (exited is not Task || !proc.HasExited)
            {
                try { proc.Kill(true); } catch { }
                Log.Warn("yt-dlp format lookup timed out for " + url);
                return Array.Empty<StreamQuality>();
            }

            json = await stdout;
            var err = await stderr;
            if (proc.ExitCode != 0)
            {
                Log.Warn($"yt-dlp exited {proc.ExitCode}: {Trim(err)}");
                return Array.Empty<StreamQuality>();
            }
        }
        catch (Exception ex)
        {
            Log.Error("yt-dlp format lookup failed", ex);
            return Array.Empty<StreamQuality>();
        }

        return Parse(json);
    }

    private static IReadOnlyList<StreamQuality> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("formats", out var formats) ||
                formats.ValueKind != JsonValueKind.Array)
                return Array.Empty<StreamQuality>();

            // keep the highest frame rate seen per height, so 1080p60 wins over 1080p30
            var best = new Dictionary<int, int>();
            foreach (var f in formats.EnumerateArray())
            {
                // TryGetInt32 throws rather than returning false when the element is
                // JSON null, and every audio-only format carries "height": null.
                // The ValueKind check has to come first.
                if (!f.TryGetProperty("height", out var hEl) ||
                    hEl.ValueKind != JsonValueKind.Number ||
                    !hEl.TryGetInt32(out var h) || h <= 0)
                    continue;
                // audio-only entries carry a height of null, but guard vcodec too
                if (f.TryGetProperty("vcodec", out var v) && v.GetString() is "none" or null)
                    continue;

                var fps = 0;
                if (f.TryGetProperty("fps", out var fEl) && fEl.ValueKind == JsonValueKind.Number)
                    fps = (int)Math.Round(fEl.GetDouble());

                if (!best.TryGetValue(h, out var existing) || fps > existing) best[h] = fps;
            }

            return best.Keys.OrderByDescending(h => h)
                       .Select(h => new StreamQuality(h, best[h]))
                       .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("could not parse yt-dlp output", ex);
            return Array.Empty<StreamQuality>();
        }
    }

    private static string Trim(string s) =>
        string.IsNullOrWhiteSpace(s) ? "(no detail)" : s.Trim().Split('\n')[0];
}
