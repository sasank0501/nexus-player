using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MpvFrontend;

/// <summary>
/// Turns a release filename into something worth putting on screen and using as
/// a folder name. Deliberately conservative: it strips the junk it recognises
/// and otherwise leaves the name alone, rather than guessing.
///
/// This is the local-only first pass. Phase 3 replaces the lookup half of this
/// with real metadata, but the parsing here is what feeds it.
/// </summary>
public static class TitleCleaner
{
    // release/quality tokens; everything from the first one onwards is noise
    private static readonly string[] NoiseTokens =
    {
        "2160p", "1080p", "720p", "480p", "4k", "uhd", "hdr10plus", "hdr10", "hdr", "dv",
        "web-dl", "webrip", "web", "bluray", "blu-ray", "bdrip", "brrip", "dvdrip", "remux",
        "x264", "x265", "h264", "h265", "hevc", "avc", "10bit", "8bit",
        "aac", "aac5", "ac3", "eac3", "e-ac-3", "ddp5", "ddp", "dts", "truehd", "atmos", "flac",
        "dual", "multi", "internal", "repack", "proper", "extended", "unrated", "remastered",
        "amzn", "nf", "dsnp", "atvp", "hulu", "max",
    };

    private static readonly Regex LeadingGroup = new(@"^\s*[\[\{（(][^\]\}）)]{1,40}[\]\}）)]\s*", RegexOptions.Compiled);
    private static readonly Regex TrailingCrc = new(@"\s*[\[\(][0-9A-Fa-f]{8}[\]\)]\s*$", RegexOptions.Compiled);
    private static readonly Regex SeasonEpisode = new(@"\bS(\d{1,2})[\s._-]?E(\d{1,3})(?:v\d+)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SeasonOnly = new(@"\bS(\d{1,2})(?![\s._-]?E\d)(?![\dA-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SitePrefix = new(@"^\s*\[?(?:www\.)?[A-Za-z0-9-]+\.(?:org|com|net|to|me|tv|io|cc|info)\]?\s*[-–—]*\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SeasonWord = new(@"\bSeason\s*(\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Year = new(@"\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    // anime absolute numbering: "Show Name - 48" / "- 48v2"
    private static readonly Regex AbsoluteEpisode = new(@"\s-\s(\d{1,4})(v\d)?\b", RegexOptions.Compiled);

    /// <summary>Show/movie name suitable for display and for a folder name.</summary>
    public static string ShowTitle(string path)
    {
        if (IsUrl(path)) return SafeHost(path);

        // Prefer the file's own name, but fall back to the parent folder when the
        // filename carries no title at all — releases like
        // "Harold And Kumar .../KINGDOM  RG.mkv" and ".../Redline ITBD Remux.mkv"
        // put the title only on the folder.
        var fromFile = Clean(Path.GetFileNameWithoutExtension(path));
        var fromFolder = Clean(ParentName(path));

        if (!LooksLikeTitle(fromFolder) || IsGenericFolder(ParentName(path)))
            return LooksLikeTitle(fromFile) ? fromFile : fromFolder;
        if (!LooksLikeTitle(fromFile)) return fromFolder;

        // A meaningfully longer folder name means the filename lost the title:
        // "Harold And Kumar .../KINGDOM  RG.mkv" and ".../Redline ITBD Remux.mkv"
        // both carry it only on the folder. It also recovers the year for scene
        // releases whose folder has it and whose filename does not.
        return fromFolder.Length >= fromFile.Length * 1.25 ? fromFolder : fromFile;
    }

    /// <summary>"S03E02", "Episode 48", or the folder name for a movie.</summary>
    public static string EpisodeLabel(string path)
    {
        if (IsUrl(path)) return "Stream";

        var name = Path.GetFileNameWithoutExtension(path);
        var se = SeasonEpisode.Match(name);
        if (se.Success)
            return $"S{int.Parse(se.Groups[1].Value):00}E{int.Parse(se.Groups[2].Value):00}";

        var stripped = TrailingCrc.Replace(LeadingGroup.Replace(name, ""), "");
        var abs = AbsoluteEpisode.Match(stripped);
        if (abs.Success) return "Episode " + int.Parse(abs.Groups[1].Value);

        var folder = ParentName(path);
        return string.IsNullOrWhiteSpace(folder) ? "" : Clean(folder);
    }

    /// <summary>Show name plus season, used to group screenshots on disk.</summary>
    public static string FolderName(string path)
    {
        var title = ShowTitle(path);

        var name = Path.GetFileNameWithoutExtension(path);
        var se = SeasonEpisode.Match(name);
        var season = se.Success ? int.Parse(se.Groups[1].Value) : -1;
        if (season < 0)
        {
            var folder = ParentName(path);
            var so = SeasonOnly.Match(folder);
            if (so.Success) season = int.Parse(so.Groups[1].Value);
            else
            {
                var word = SeasonWord.Match(folder);
                if (word.Success) season = int.Parse(word.Groups[1].Value);
            }
        }
        if (season >= 0) title += $" - Season {season:00}";

        return Sanitize(title);
    }

    // --- helpers ----------------------------------------------------------

    private static bool IsUrl(string p) =>
        p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "Stream";

    private static string ParentName(string path)
    {
        try { return new DirectoryInfo(Path.GetDirectoryName(path) ?? "").Name; }
        catch { return ""; }
    }

    private static readonly string[] GenericFolders =
    {
        "downloads", "movies", "videos", "video", "temp", "tmp", "desktop",
        "documents", "new folder", "media", "anime", "tv", "shows", "films",
    };

    private static bool IsGenericFolder(string name) =>
        GenericFolders.Contains(name.Trim().ToLowerInvariant());

    // a name that is only release-group noise is not a title
    private static bool LooksLikeTitle(string s) =>
        s.Length >= 3 && s.Any(char.IsLetter) && s.Split(' ').Length >= 1 &&
        !Regex.IsMatch(s, @"^(RG|Remux|Untitled)$", RegexOptions.IgnoreCase);

    private static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var s = SitePrefix.Replace(raw, "");
        s = LeadingGroup.Replace(s, "");
        s = TrailingCrc.Replace(s, "");

        // scene names are dot-separated; don't touch names that already use spaces
        if (!s.Contains(' ') && (s.Count(c => c == '.') >= 2 || s.Count(c => c == '_') >= 2))
            s = s.Replace('.', ' ').Replace('_', ' ');

        // cut at SxxExx / absolute episode number: everything after is episode detail
        var se = SeasonEpisode.Match(s);
        if (se.Success) s = s[..se.Index];
        else
        {
            var so = SeasonOnly.Match(s);
            if (so.Success && so.Index > 0) s = s[..so.Index];
            else
            {
                var word = SeasonWord.Match(s);
                if (word.Success && word.Index > 0) s = s[..word.Index];
                else
                {
                    var abs = AbsoluteEpisode.Match(s);
                    if (abs.Success && abs.Index > 0) s = s[..abs.Index];
                }
            }
        }

        // keep the year when present — it disambiguates remakes — and drop the rest
        var year = Year.Match(s);
        if (year.Success && year.Index > 0)
        {
            var head = s[..year.Index].Trim(' ', '-', '.', '_', '(', '[');
            return Tidy($"{head} ({year.Groups[1].Value})");
        }

        // otherwise cut at the first recognised quality/source token
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keep = words
            .TakeWhile(w => !NoiseTokens.Contains(w.Trim('[', ']', '(', ')', '-', '.').ToLowerInvariant()))
            .ToArray();
        if (keep.Length > 0 && keep.Length < words.Length) s = string.Join(' ', keep);

        // and at any remaining bracketed block
        var bracket = s.IndexOfAny(new[] { '[', '(' });
        if (bracket > 2) s = s[..bracket];

        return Tidy(s);
    }

    private static string Tidy(string s) =>
        Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '.', '_');

    private static string Sanitize(string s)
    {
        var cleaned = new string(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        cleaned = Tidy(cleaned).TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }
}
