using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MpvFrontend;

// JSON IPC to mpv over its named pipe. mpv is the single source of truth for
// all playback state; nothing here caches what mpv already knows.
public sealed class MpvIpcClient : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    // StreamWriter is not safe for concurrent writes, and a lock cannot be held
    // across an await — a semaphore serialises writers without blocking a thread
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, List<Action<JsonElement>>> _observers = new();
    private int _nextRequestId = 1000;
    private int _nextObserveId;
    private volatile bool _disposed;

    public MpvIpcClient(string pipeName) => _pipeName = pipeName;

    /// <summary>Raised when the pipe closes — mpv exited or crashed.</summary>
    public event Action? Disconnected;

    public async Task<bool> ConnectAsync(int timeoutMs = 5000)
    {
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await _pipe.ConnectAsync(timeoutMs);
        }
        catch (Exception ex)
        {
            Log.Error("IPC connect failed for pipe " + _pipeName, ex);
            return false;
        }

        _reader = new StreamReader(_pipe, Encoding.UTF8);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
        _ = Task.Run(ReadLoopAsync);
        Log.Info("IPC connected: " + _pipeName);
        return true;
    }

    // --- property observation ---------------------------------------------

    // Registers a handler AND subscribes in the same call. v1 observed its
    // properties during connect, before any handler was attached, so mpv's
    // initial value for `pause` arrived with nobody listening and the play icon
    // started out wrong — it needed a second observe with a fresh id to paper
    // over it. Subscribing at handler-attach time makes that race impossible.
    public void Observe(string property, Action<JsonElement> handler)
    {
        _observers.AddOrUpdate(property,
            _ => new List<Action<JsonElement>> { handler },
            (_, list) => { lock (list) { list.Add(handler); } return list; });

        var id = Interlocked.Increment(ref _nextObserveId);
        _ = SendAsync("observe_property", id, property);
    }

    public void ObserveDouble(string property, Action<double> handler) =>
        Observe(property, e => { if (e.ValueKind == JsonValueKind.Number) handler(e.GetDouble()); });

    public void ObserveBool(string property, Action<bool> handler) =>
        Observe(property, e =>
        {
            if (e.ValueKind is JsonValueKind.True or JsonValueKind.False) handler(e.GetBoolean());
        });

    public void ObserveString(string property, Action<string> handler) =>
        Observe(property, e =>
        {
            if (e.ValueKind == JsonValueKind.String) handler(e.GetString() ?? "");
        });

    // Integer properties (playlist-pos, chapter, aid, sid) report null when there
    // is no current value — surface that rather than flattening it to zero.
    public void ObserveInt(string property, Action<int?> handler) =>
        Observe(property, e =>
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v)) handler(v);
            else if (e.ValueKind == JsonValueKind.Null) handler(null);
        });

    // --- read loop ---------------------------------------------------------

    private async Task ReadLoopAsync()
    {
        if (_reader == null) return;
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync();
                if (line == null) break;
                HandleLine(line);
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) Log.Error("IPC read loop ended unexpectedly", ex);
        }

        // fail every in-flight request rather than leaving callers on a 3s timeout
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetResult(default);

        if (!_disposed)
        {
            Log.Error("IPC pipe closed — mpv has exited");
            try { Disconnected?.Invoke(); }
            catch (Exception ex) { Log.Error("Disconnected handler threw", ex); }
        }
    }

    private void HandleLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { return; }

        using (doc)
        {
            var root = doc.RootElement;

            // reply to a RequestAsync command. Clone: the document is disposed on return
            if (root.TryGetProperty("request_id", out var rid) && rid.TryGetInt32(out var reqId))
            {
                if (_pending.TryRemove(reqId, out var tcs))
                {
                    if (root.TryGetProperty("error", out var err) && err.GetString() != "success")
                        Log.Warn("mpv command failed: " + err.GetString());
                    tcs.TrySetResult(root.TryGetProperty("data", out var d) ? d.Clone() : default);
                }
                return;
            }

            if (!root.TryGetProperty("event", out var evt)) return;
            var evtName = evt.GetString();
            if (evtName != "property-change") return;

            if (!root.TryGetProperty("name", out var nameProp)) return;
            var name = nameProp.GetString();
            if (name == null || !_observers.TryGetValue(name, out var handlers)) return;

            // An absent `data` means the property currently has no value (time-pos
            // with nothing loaded, say). Pass JSON null through so ObserveInt and
            // friends can tell "none" apart from zero.
            var data = root.TryGetProperty("data", out var d2) ? d2.Clone() : default;

            Action<JsonElement>[] snapshot;
            lock (handlers) { snapshot = handlers.ToArray(); }
            foreach (var h in snapshot)
            {
                try { h(data); }
                catch (Exception ex) { Log.Error("observer for '" + name + "' threw", ex); }
            }
        }
    }

    // --- sending -----------------------------------------------------------

    public async Task SendAsync(params object[] command)
    {
        if (_writer == null || _disposed) return;
        var payload = JsonSerializer.Serialize(new { command });
        await _writeLock.WaitAsync();
        try { await _writer.WriteLineAsync(payload); }
        catch (Exception ex) { Log.Error("IPC write failed: " + payload, ex); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Send a command and await mpv's reply. ValueKind is Undefined on timeout.</summary>
    public async Task<JsonElement> RequestAsync(params object[] command)
    {
        if (_writer == null || _disposed) return default;
        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new { command, request_id = id });
        await _writeLock.WaitAsync();
        try { await _writer.WriteLineAsync(payload); }
        catch (Exception ex)
        {
            Log.Error("IPC request write failed: " + payload, ex);
            _pending.TryRemove(id, out _);
            return default;
        }
        finally { _writeLock.Release(); }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        if (done != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            Log.Warn("IPC request timed out: " + payload);
            return default;
        }
        return await tcs.Task;
    }

    public async Task<double?> GetDoubleAsync(string property)
    {
        var e = await RequestAsync("get_property", property);
        return e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
    }

    public async Task<string?> GetStringAsync(string property)
    {
        var e = await RequestAsync("get_property", property);
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    // --- command helpers ---------------------------------------------------

    public Task SetProperty(string name, object value) => SendAsync("set_property", name, value);
    public Task LoadFile(string path) => SendAsync("loadfile", path, "replace");
    public Task AppendFile(string path) => SendAsync("loadfile", path, "append-play");
    public Task TogglePause() => SendAsync("cycle", "pause");
    public Task SetPause(bool paused) => SetProperty("pause", paused);
    public Task SeekRelative(double seconds) => SendAsync("seek", seconds, "relative");
    public Task SetVolume(double volume) => SetProperty("volume", volume);
    public Task KeyPress(string key) => SendAsync("keypress", key);
    public Task PlaylistNext() => SendAsync("playlist-next", "weak");
    public Task PlaylistPrev() => SendAsync("playlist-prev", "weak");

    /// <summary>
    /// Absolute seek that completes only once mpv acknowledges it. Callers use the
    /// returned task to know when position updates are trustworthy again: mpv emits
    /// one more pre-seek time-pos after the command is sent, which otherwise snaps
    /// the seek bar backwards for a frame.
    /// </summary>
    public Task SeekAbsolute(double seconds) => RequestAsync("seek", seconds, "absolute");

    public void Dispose()
    {
        _disposed = true;
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _writeLock.Dispose();
    }
}
