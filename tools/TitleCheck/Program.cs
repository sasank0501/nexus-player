using MpvFrontend;

// Runs the shipping TitleCleaner over the real libraries and prints what it makes
// of every file, so parsing regressions are visible instead of theoretical.
//   dotnet run --project tools/TitleCheck

var roots = args.Length > 0
    ? args
    : new[] { @"D:\Movies", Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") };

var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi", ".m4v", ".webm" };

var files = new List<string>();
foreach (var root in roots)
{
    if (!Directory.Exists(root)) { Console.WriteLine($"(skipping missing root {root})"); continue; }
    try
    {
        files.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f))));
    }
    catch (Exception ex) { Console.WriteLine($"(error reading {root}: {ex.Message})"); }
}

files.Sort(StringComparer.OrdinalIgnoreCase);
Console.WriteLine($"{files.Count} video files across {roots.Length} root(s)\n");

var suspect = 0;
var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (var f in files)
{
    var title = TitleCleaner.ShowTitle(f);
    var episode = TitleCleaner.EpisodeLabel(f);
    var folder = TitleCleaner.FolderName(f);
    folders.Add(folder);

    // things that mean the parser did not really understand the name
    var bad = title.Length == 0
              || title.Length > 60
              || System.Text.RegularExpressions.Regex.IsMatch(
                     title, @"\b(1080p|2160p|720p|x26[45]|HEVC|WEB|BluRay|DDP|AAC|E-AC-3|Dual Audio|v\d)\b",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (bad) suspect++;

    Console.WriteLine($"{(bad ? "??" : "  ")} {Path.GetFileName(f)}");
    Console.WriteLine($"     title   : {title}");
    Console.WriteLine($"     episode : {episode}");
    Console.WriteLine($"     folder  : {folder}");
}

Console.WriteLine($"\n=== {folders.Count} distinct screenshot folders ===");
foreach (var d in folders) Console.WriteLine("  " + d);

Console.WriteLine($"\n{files.Count - suspect}/{files.Count} parsed cleanly; {suspect} suspect (marked ??)");
