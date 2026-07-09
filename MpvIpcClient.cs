using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MpvFrontend
{
    public class MpvIpcClient : IDisposable
    {
        private readonly string _pipeName;
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private readonly object _writeLock = new();

        public event Action<double>? TimePosChanged;
        public event Action<double>? DurationChanged;
        public event Action<bool>? PauseChanged;
        public event Action<string>? MediaTitleChanged;

        public MpvIpcClient(string pipeName)
        {
            _pipeName = pipeName;
        }

        public async Task<bool> ConnectAsync(int timeoutMs = 5000)
        {
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await _pipe.ConnectAsync(timeoutMs);
            }
            catch (TimeoutException)
            {
                return false;
            }

            _reader = new StreamReader(_pipe, Encoding.UTF8);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };

            _ = Task.Run(ReadLoopAsync);

            await SendAsync("observe_property", 1, "time-pos");
            await SendAsync("observe_property", 2, "duration");
            await SendAsync("observe_property", 3, "pause");
            await SendAsync("observe_property", 5, "media-title");

            return true;
        }

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
            catch
            {
                // pipe closed / mpv exited
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
                if (!root.TryGetProperty("event", out var evt)) return;
                if (evt.GetString() != "property-change") return;
                if (!root.TryGetProperty("name", out var nameProp)) return;

                var name = nameProp.GetString();
                if (!root.TryGetProperty("data", out var data)) return;

                switch (name)
                {
                    case "time-pos":
                        if (data.ValueKind == JsonValueKind.Number)
                            TimePosChanged?.Invoke(data.GetDouble());
                        break;
                    case "duration":
                        if (data.ValueKind == JsonValueKind.Number)
                            DurationChanged?.Invoke(data.GetDouble());
                        break;
                    case "pause":
                        if (data.ValueKind == JsonValueKind.True || data.ValueKind == JsonValueKind.False)
                            PauseChanged?.Invoke(data.GetBoolean());
                        break;
                    case "media-title":
                        if (data.ValueKind == JsonValueKind.String)
                            MediaTitleChanged?.Invoke(data.GetString() ?? "");
                        break;
                }
            }
        }

        public async Task SendAsync(params object[] command)
        {
            if (_writer == null) return;
            var payload = JsonSerializer.Serialize(new { command });
            lock (_writeLock)
            {
                // StreamWriter isn't safe for concurrent async writes from multiple threads;
                // serialize with a lock around the synchronous Write, then flush.
                _writer.WriteLine(payload);
            }
            await Task.CompletedTask;
        }

        public Task LoadFile(string path) => SendAsync("loadfile", path, "replace");
        public Task AppendFile(string path) => SendAsync("loadfile", path, "append-play");
        public Task TogglePause() => SendAsync("cycle", "pause");
        public Task Stop() => SendAsync("stop");
        public Task SeekAbsolute(double seconds) => SendAsync("seek", seconds, "absolute");
        public Task SetVolume(double volume) => SendAsync("set_property", "volume", volume);

        public void Dispose()
        {
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
        }
    }
}
