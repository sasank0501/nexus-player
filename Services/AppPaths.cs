using System;
using System.IO;

namespace MpvFrontend;

// All on-disk locations the app owns. Deliberately under NexusPlayerV2 rather
// than NexusPlayer so v2 can run side by side with v1 without either one
// clobbering the other's session or settings.
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusPlayerV2");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string SessionFile  => Path.Combine(Root, "session.json");
    public static string LogFile      => Path.Combine(Root, "nexus.log");
    public static string MpvLogFile   => Path.Combine(Root, "mpv.log");

    public static string ThumbCache   => Path.Combine(Root, "cache", "thumbs");
    public static string MetaCache    => Path.Combine(Root, "cache", "meta");
    public static string ImageCache   => Path.Combine(Root, "cache", "images");

    public static void EnsureAll()
    {
        foreach (var d in new[] { Root, ThumbCache, MetaCache, ImageCache })
        {
            try { Directory.CreateDirectory(d); } catch { /* best effort */ }
        }
    }
}
