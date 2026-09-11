using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MpvFrontend;

/// <summary>One cast or crew credit, trimmed to what the panel actually shows.</summary>
public record TmdbCredit(string Name, string? Role, string? ProfilePath, bool IsCast);

/// <summary>Everything the Cast &amp; Crew panel needs for one title.</summary>
public record TmdbCredits(string Title, IReadOnlyList<TmdbCredit> Cast, IReadOnlyList<TmdbCredit> Crew);

/// <summary>
/// Looks up cast/crew for whatever's playing, against TMDb's free API - this is
/// the "real metadata" TitleCleaner's own doc comment marks as Phase 3, replacing
/// the local-only guesswork there with an actual lookup. Nothing here touches mpv
/// or the UI; OverlayWindow calls in and renders whatever comes back (or doesn't).
/// </summary>
public static class TmdbClient
{
    private const string ApiBase = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/w185"; // small enough for a list of faces

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Cast + crew for a title, or null if there's no key, no match, or the
    /// request failed - every case logged, none of them thrown at the caller.
    /// Disk-cached indefinitely by title+year+kind: a movie's credits don't
    /// change between rewatches, and TMDb's free tier is rate-limited.
    /// </summary>
    public static async Task<TmdbCredits?> GetCredits(string title, int? year, bool isTv, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Info("TMDb: no API key set, skipping lookup");
            return null;
        }
        if (string.IsNullOrWhiteSpace(title)) return null;

        var cacheKey = $"{(isTv ? "tv" : "movie")}-{title}-{year}";
        var cached = ReadCache(cacheKey);
        if (cached != null) return cached;

        try
        {
            var id = await Search(title, year, isTv, apiKey);
            if (id == null)
            {
                Log.Info($"TMDb: no {(isTv ? "TV" : "movie")} match for '{title}'" + (year is int y ? $" ({y})" : ""));
                return null;
            }

            var credits = await FetchCredits(id.Value, isTv, apiKey, title);
            WriteCache(cacheKey, credits);
            return credits;
        }
        catch (Exception ex)
        {
            Log.Warn($"TMDb lookup failed for '{title}': {ex.Message}");
            return null;
        }
    }

    private static async Task<int?> Search(string title, int? year, bool isTv, string apiKey)
    {
        var kind = isTv ? "tv" : "movie";
        var yearParam = year is int y ? $"&{(isTv ? "first_air_date_year" : "year")}={y}" : "";
        var url = $"{ApiBase}/search/{kind}?api_key={Uri.EscapeDataString(apiKey)}" +
                  $"&query={Uri.EscapeDataString(title)}{yearParam}";

        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            // a bad key reads as 401 here, not an exception - worth its own line
            // rather than falling into the generic "no match" case silently
            Log.Warn($"TMDb search for '{title}' returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;
        return results[0].GetProperty("id").GetInt32();
    }

    private static async Task<TmdbCredits> FetchCredits(int id, bool isTv, string apiKey, string title)
    {
        var kind = isTv ? "tv" : "movie";
        var url = $"{ApiBase}/{kind}/{id}/credits?api_key={Uri.EscapeDataString(apiKey)}";
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var cast = new List<TmdbCredit>();
        if (root.TryGetProperty("cast", out var castArr))
        {
            // TMDb orders "cast" by billing already; capping keeps a sprawling
            // ensemble (some anime/K-drama credit lists run past 100 names) from
            // turning the panel into an endless scroll of extras
            foreach (var c in castArr.EnumerateArray().Take(30))
            {
                cast.Add(new TmdbCredit(
                    GetString(c, "name") ?? "",
                    GetString(c, "character"),
                    GetString(c, "profile_path"),
                    IsCast: true));
            }
        }

        var crew = new List<TmdbCredit>();
        if (root.TryGetProperty("crew", out var crewArr))
        {
            // the full crew list runs to sound mixers and craft services; keep
            // only the roles a viewer would actually recognise
            var wanted = new HashSet<string>
                { "Director", "Writer", "Screenplay", "Story", "Producer", "Executive Producer", "Creator" };
            foreach (var c in crewArr.EnumerateArray())
            {
                var job = GetString(c, "job");
                if (job == null || !wanted.Contains(job)) continue;
                crew.Add(new TmdbCredit(GetString(c, "name") ?? "", job, GetString(c, "profile_path"), IsCast: false));
            }
        }

        return new TmdbCredits(title, cast, crew);
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// A credit's photo, downloaded once and served from disk after that - the
    /// panel is something a viewer opens mid-rewatch, not something that should
    /// wait on the network every time. Null profile paths (TMDb has plenty - no
    /// photo on file for a given person) and download failures both just mean no
    /// photo; the panel falls back to initials either way.
    /// </summary>
    public static async Task<string?> GetCachedProfileImage(string? profilePath)
    {
        if (string.IsNullOrEmpty(profilePath)) return null;

        var fileName = SanitizeFileName(profilePath.TrimStart('/'));
        var localPath = Path.Combine(AppPaths.ImageCache, fileName);
        if (File.Exists(localPath)) return localPath;

        try
        {
            AppPaths.EnsureAll();
            var bytes = await Http.GetByteArrayAsync(ImageBase + profilePath);
            await File.WriteAllBytesAsync(localPath, bytes);
            return localPath;
        }
        catch (Exception ex)
        {
            Log.Warn($"TMDb profile image download failed for {profilePath}: {ex.Message}");
            return null;
        }
    }

    // --- disk cache: one JSON file per title+year+kind ----------------------

    private static string CachePath(string key) =>
        Path.Combine(AppPaths.MetaCache, SanitizeFileName(key) + ".json");

    private static TmdbCredits? ReadCache(string key)
    {
        try
        {
            var path = CachePath(key);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TmdbCredits>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"TMDb cache read failed for '{key}': {ex.Message}");
            return null;
        }
    }

    private static void WriteCache(string key, TmdbCredits credits)
    {
        try
        {
            AppPaths.EnsureAll();
            File.WriteAllText(CachePath(key), JsonSerializer.Serialize(credits));
        }
        catch (Exception ex)
        {
            Log.Warn($"TMDb cache write failed for '{key}': {ex.Message}");
        }
    }

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
