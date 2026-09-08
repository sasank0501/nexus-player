using System;
using System.IO;
using System.Text;

namespace MpvFrontend;

// v1 had no error surface at all: a failed loadfile was silent, mpv's stdout
// went to a console that doesn't exist when launched from the shortcut, and
// every catch swallowed. Everything interesting goes through here instead.
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    // raised for anything the user should actually see; MainWindow shows a toast
    public static event Action<string>? UserVisibleError;

    public static void Info(string message)  => Write("INFO ", message);
    public static void Warn(string message)  => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");
        if (ex != null) Write("ERROR", ex.StackTrace ?? "(no stack)");
    }

    // logs AND surfaces to the UI — use for failures the user must know about
    public static void UserError(string message, Exception? ex = null)
    {
        Error(message, ex);
        try { UserVisibleError?.Invoke(message); } catch { /* never let logging throw */ }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                AppPaths.EnsureAll();
                Roll(AppPaths.LogFile);
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* logging must never take the app down */ }
        }
        System.Diagnostics.Debug.WriteLine(line);
    }

    // keep one previous generation so a crash's context isn't lost to truncation
    private static void Roll(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var old = path + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
        }
        catch { }
    }

    // mpv's own stdout/stderr, kept in a separate file so player noise doesn't
    // drown the app log (stale mpv.conf options only ever showed up here)
    public static void Mpv(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (Gate)
        {
            try
            {
                AppPaths.EnsureAll();
                Roll(AppPaths.MpvLogFile);
                File.AppendAllText(AppPaths.MpvLogFile,
                    $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { }
        }
    }
}
