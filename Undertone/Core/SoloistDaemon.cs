using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Undertone.Core
{
    /// <summary>
    /// Spotify Soloist — официальный консольный плеер Spotify без интерфейса (developer.spotify.com/documentation/soloist).
    /// Undertone запускает его скрытым процессом: в Spotify он виден как устройство «Undertone Soloist», звук идёт через WASAPI
    /// в устройство Windows по умолчанию. Управление — по локальному WebSocket (--ws 127.0.0.1:0, порт — из data\ws.port).
    /// Встроенного браузера (WebView2) не нужно — это в разы легче для слабых ПК.
    ///
    /// Ограничения Soloist: нужен Premium и личный API-ключ; один раз устройство надо выбрать в приложении Spotify
    /// (телефон или ПК в той же сети) — так Soloist получает вход; сборка живёт 90 дней (код выхода 10) —
    /// тогда Undertone сам скачивает новую с сайта Spotify. Сами бинарники Undertone не раздаёт.
    /// События — в UI-потоке.
    /// </summary>
    public sealed class SoloistDaemon : IDisposable
    {
        public const string DownloadUrl = "https://soloist-builds.spotifycdn.com/soloist_release_windows_x86_64.zip";
        public const string KeyPageUrl = "https://developer.spotify.com/dashboard/soloist";
        public const string DocsUrl = "https://developer.spotify.com/documentation/soloist";
        public const string DeviceName = "Undertone Soloist";
        public const string KeyEnvName = "spotify_soloist_key";
        public const int ExpiredExitCode = 10;

        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

        public static string Folder => Path.Combine(EnvFile.DataDirectory, "soloist");
        public static string ExePath => Path.Combine(Folder, "soloist.exe");
        public static string DataDir => Path.Combine(Folder, "data");
        public static string CacheDir => Path.Combine(Folder, "cache");
        public static bool IsInstalled => File.Exists(ExePath);

        private readonly string _apiKey;
        private readonly Dispatcher _ui;
        private readonly Action<string> _notify;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Queue<string> _outputTail = new();

        private Process? _proc;
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private TaskCompletionSource<bool>? _firstAuth;
        private bool _stopping;
        private bool _disposed;

        public SoloistState State { get; } = new();

        /// <summary>Идёт подглядывание в чужой плейлист (см. ReadContextTracksAsync) — события трека в это время не наши.</summary>
        public bool Peeking { get; private set; }

        private TaskCompletionSource<JsonElement>? _queueWaiter;

        public bool IsRunning => !_disposed && _proc is { HasExited: false } && _ws?.State == WebSocketState.Open;

        /// <summary>Что-то поменялось (вход, трек, статус, позиция, громкость).</summary>
        public event Action<SoloistChange>? Changed;
        /// <summary>Процесс завершился сам (не по нашей команде): код выхода.</summary>
        public event Action<int>? Exited;

        public SoloistDaemon(string apiKey, Dispatcher ui, Action<string> notify)
        {
            _apiKey = apiKey.Trim();
            _ui = ui;
            _notify = notify;
        }

        // ────────── запуск ──────────

        /// <summary>
        /// Запускает Soloist и подключается к его WebSocket. Сборка устарела — скачивает новую и пробует ещё раз.
        /// Возвращается, когда сокет открыт и пришло первое auth_state (вошёл или нет — смотри State.LoggedIn).
        /// </summary>
        public async Task StartAsync(CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SoloistDaemon));
            await _startLock.WaitAsync(ct);
            try
            {
                if (IsRunning) return;
                try
                {
                    await StartCoreAsync(ct);
                }
                catch (SoloistExpiredException)
                {
                    _notify("Сборка Spotify Soloist устарела (они живут 90 дней) — скачиваю новую…");
                    StopProcess();
                    await InstallAsync(null, ct);
                    await StartCoreAsync(ct);
                }
            }
            finally
            {
                _startLock.Release();
            }
        }

        private async Task StartCoreAsync(CancellationToken ct)
        {
            StopProcess();
            if (!IsInstalled) throw new SoloistException("Spotify Soloist не скачан — скачай его в Настройках");
            if (_apiKey.Length == 0) throw new SoloistException("нет API-ключа Soloist — вставь его в Настройках");

            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(CacheDir);
            KillStale();
            TryDelete(Path.Combine(DataDir, "ws.port"));
            TryDelete(Path.Combine(DataDir, "ws.addr"));
            lock (_outputTail) _outputTail.Clear();

            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Folder,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in new[]
                     {
                         "--device-name", DeviceName,
                         "--api-key", _apiKey,
                         "--data-dir", DataDir,
                         "--cache-dir", CacheDir,
                         "--cache-size", "300",
                         "--ws", "127.0.0.1:0",
                     })
                psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => OnOutput(e.Data);
            proc.ErrorDataReceived += (_, e) => OnOutput(e.Data);
            proc.Exited += (_, _) => _ui.InvokeAsync(() => OnProcessExited(proc));
            _stopping = false;
            if (!proc.Start()) throw new SoloistException("Spotify Soloist не запустился");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _proc = proc;
            Log.Info($"Soloist: запущен (pid {proc.Id})");
            // звук у Soloist идёт обычными потоками: на загруженном слабом ПК поток просыпается поздно — и музыка
            // подлагивает. Чуть выше обычного приоритета хватает, а системе не мешает (не «реального времени»).
            try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; }
            catch (Exception ex) { Log.Warn("Soloist: не удалось поднять приоритет", ex); }

            var port = await WaitForPortAsync(proc, ct);
            await ConnectAsync(port, ct);
        }

        /// <summary>Soloist пишет выбранный порт в data\ws.port — ждём его (или ранний выход процесса).</summary>
        private async Task<int> WaitForPortAsync(Process proc, CancellationToken ct)
        {
            var file = Path.Combine(DataDir, "ws.port");
            var until = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (proc.HasExited)
                {
                    proc.WaitForExit(); // дочитать вывод — в нём причина
                    throw ExitError(proc.ExitCode);
                }
                try
                {
                    if (File.Exists(file) && int.TryParse((await File.ReadAllTextAsync(file, ct)).Trim(), out var port) && port > 0)
                        return port;
                }
                catch (IOException) { } // файл ещё пишется
                await Task.Delay(150, ct);
            }
            StopProcess();
            throw new SoloistException("Spotify Soloist не открыл порт управления за 15 секунд");
        }

        private async Task ConnectAsync(int port, CancellationToken ct)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _firstAuth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
            }
            _ws = ws;
            _ = ReceiveLoopAsync(ws, _cts.Token);
            // auth_state приходит сразу после подключения
            await Task.WhenAny(_firstAuth.Task, Task.Delay(TimeSpan.FromSeconds(3), ct));
            Log.Info($"Soloist: управление на порту {port}, вход: {(State.LoggedIn ? "есть" : "нет — нужно выбрать устройство в Spotify")}");
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    _ui.InvokeAsync(() => OnMessage(text));
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                if (!_stopping) Log.Warn("Soloist: соединение управления оборвалось", ex);
            }
            catch (Exception ex)
            {
                Log.Warn("Soloist: ошибка чтения событий", ex);
            }
        }

        private void OnMessage(string text)
        {
            if (_disposed) return;
            SoloistChange change;
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) &&
                    t.ValueKind == JsonValueKind.String && t.GetString() == "queue_changed")
                    _queueWaiter?.TrySetResult(root.Clone());
                change = State.Apply(root);
            }
            catch (JsonException)
            {
                return;
            }
            if (change.HasFlag(SoloistChange.Auth)) _firstAuth?.TrySetResult(true);
            if (change.HasFlag(SoloistChange.Error)) Log.Warn("Soloist: " + State.LastError);
            if (change != SoloistChange.None) Changed?.Invoke(change);
        }

        private void OnOutput(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            line = Redact(line);
            lock (_outputTail)
            {
                _outputTail.Enqueue(line);
                while (_outputTail.Count > 8) _outputTail.Dequeue();
            }
        }

        private void OnProcessExited(Process proc)
        {
            if (proc != _proc) return;
            int code;
            try { code = proc.ExitCode; }
            catch (InvalidOperationException) { code = -1; }
            if (_stopping || _disposed) return;
            Log.Warn($"Soloist: процесс завершился с кодом {code}. {LastOutput()}");
            _cts?.Cancel();
            _ws = null;
            Exited?.Invoke(code);
        }

        private Exception ExitError(int code)
        {
            var output = LastOutput();
            Log.Warn($"Soloist: вышел при запуске с кодом {code}. {output}");
            if (code == ExpiredExitCode) return new SoloistExpiredException();
            var lower = output.ToLowerInvariant();
            if (lower.Contains("api key") || lower.Contains("api-key") || lower.Contains("unauthorized"))
                return new SoloistException("Soloist не принял API-ключ — проверь его в Настройках");
            if (lower.Contains("another instance") || lower.Contains("already running"))
                return new SoloistException("Soloist уже запущен другой программой с той же папкой данных");
            return new SoloistException($"Spotify Soloist завершился с кодом {code}" + (output.Length > 0 ? $": {output}" : ""));
        }

        private string LastOutput()
        {
            lock (_outputTail) return string.Join(" | ", _outputTail.TakeLast(3));
        }

        private string Redact(string line) => _apiKey.Length >= 8 ? line.Replace(_apiKey, "***") : line;

        /// <summary>Soloist, оставшийся от прошлого запуска (Undertone упал), держит папку данных — закрываем его.</summary>
        private static void KillStale()
        {
            try
            {
                var pidFile = Path.Combine(DataDir, "soloist.pid");
                if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)) return;
                using var p = Process.GetProcessById(pid);
                if (!p.ProcessName.Equals("soloist", StringComparison.OrdinalIgnoreCase)) return;
                Log.Info($"Soloist: закрываю оставшийся процесс {pid}");
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (ArgumentException) { } // такого процесса уже нет
            catch (Exception ex)
            {
                Log.Warn("Soloist: не удалось закрыть старый процесс", ex);
            }
        }

        // ────────── команды ──────────

        public Task PlayUriAsync(string uri, CancellationToken ct = default) =>
            SendAsync(new Dictionary<string, object> { ["type"] = "command", ["command"] = "play", ["uri"] = uri }, ct);

        public Task ResumeAsync() => Command("play");
        public Task PauseAsync() => Command("pause");
        public Task ActivateAsync() => Command("activate");

        /// <summary>Порядок задаёт очередь Undertone: перемешивание и повтор у самого Soloist выключаем.</summary>
        public async Task ResetOptionsAsync()
        {
            await SendAsync(new Dictionary<string, object> { ["type"] = "command", ["command"] = "set_shuffle", ["enabled"] = false });
            await SendAsync(new Dictionary<string, object> { ["type"] = "command", ["command"] = "set_repeat_track", ["enabled"] = false });
            await SendAsync(new Dictionary<string, object> { ["type"] = "command", ["command"] = "set_repeat_context", ["enabled"] = false });
        }

        public Task SeekAsync(long positionMs) =>
            SendAsync(new Dictionary<string, object> { ["type"] = "command", ["command"] = "seek", ["position_ms"] = Math.Max(0, positionMs) });

        public Task SetVolumeAsync(int volume) =>
            SendAsync(new Dictionary<string, object> { ["type"] = "command", ["command"] = "set_volume", ["volume"] = Math.Clamp(volume, 0, 100) });

        private Task Command(string name) =>
            SendAsync(new Dictionary<string, object> { ["type"] = "command", ["command"] = name });

        private async Task SendAsync(Dictionary<string, object> payload, CancellationToken ct = default)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) throw new SoloistException("Spotify Soloist не на связи");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await _sendLock.WaitAsync(ct);
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ────────── чужие плейлисты ──────────

        /// <summary>
        /// Треки плейлиста или альбома, которые Web API не отдаёт (чужие плейлисты): Soloist на секунду включает его
        /// без звука, Undertone читает очередь (get_queue) и возвращает всё как было — тот же трек, место, громкость.
        /// </summary>
        public async Task<List<YaTrack>> ReadContextTracksAsync(string contextUri, CancellationToken ct)
        {
            await StartAsync(ct);
            if (!State.LoggedIn) throw new SoloistNotPairedException();

            var prevUri = State.ItemUri;
            var prevPos = State.PositionMs;
            var prevPlaying = State.IsPlaying || State.Status == "buffering";
            var prevVolume = State.Volume;
            Peeking = true;
            try
            {
                if (prevVolume > 0) await SetVolumeAsync(0);
                await SendAsync(new Dictionary<string, object> { ["type"] = "command", ["command"] = "set_shuffle", ["enabled"] = false }, ct);
                await PlayUriAsync(contextUri, ct);
                await WaitUntilAsync(() => State.ContextUri == contextUri && State.ItemUri.Length > 0, TimeSpan.FromSeconds(8), ct);
                await Task.Delay(400, ct); // Soloist дочитывает контекст
                await PauseAsync();

                _queueWaiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                await SendAsync(new Dictionary<string, object> { ["type"] = "command", ["command"] = "get_queue", ["limit"] = 0 }, ct);
                var done = await Task.WhenAny(_queueWaiter.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
                ct.ThrowIfCancellationRequested();
                if (done != _queueWaiter.Task) throw new SoloistException("Soloist не отдал список треков");
                var tracks = SoloistState.ContextTracks(State.CurrentItem, await _queueWaiter.Task);
                Log.Info($"Soloist: из {contextUri} прочитано треков: {tracks.Count}");
                return tracks;
            }
            finally
            {
                _queueWaiter = null;
                await RestoreAfterPeekAsync(prevUri, prevPos, prevPlaying, prevVolume);
                Peeking = false;
                Changed?.Invoke(SoloistChange.Track | SoloistChange.Playback | SoloistChange.Position);
            }
        }

        private async Task RestoreAfterPeekAsync(string uri, long positionMs, bool playing, int volume)
        {
            try
            {
                if (uri.Length > 0)
                {
                    await PlayUriAsync(uri);
                    await WaitUntilAsync(() => State.ItemUri == uri, TimeSpan.FromSeconds(5), CancellationToken.None);
                    if (positionMs > 1000) await SeekAsync(positionMs);
                    if (!playing) await PauseAsync();
                }
                else
                {
                    await PauseAsync();
                }
                if (volume > 0) await SetVolumeAsync(volume);
            }
            catch (Exception ex)
            {
                Log.Warn("Soloist: не удалось вернуть воспроизведение после чтения плейлиста", ex);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
        {
            var until = DateTime.UtcNow + timeout;
            while (!condition() && DateTime.UtcNow < until)
                await Task.Delay(100, ct);
        }

        // ────────── установка ──────────

        /// <summary>
        /// Качает сборку с сайта Spotify и кладёт soloist.exe в папку данных Undertone.
        /// progress — 0..1 (если размер известен).
        /// </summary>
        public static async Task InstallAsync(IProgress<double>? progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Folder);
            var zip = Path.Combine(Folder, "soloist.zip.part");
            var unpack = Path.Combine(Folder, "unpack");
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new SoloistException($"сайт Spotify не отдал Soloist (HTTP {(int)resp.StatusCode})");
                    var total = resp.Content.Headers.ContentLength ?? 0;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(zip);
                    var buf = new byte[81920];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        if (total > 0) progress?.Report((double)done / total);
                    }
                }

                if (Directory.Exists(unpack)) Directory.Delete(unpack, recursive: true);
                ZipFile.ExtractToDirectory(zip, unpack);
                var exe = Directory.EnumerateFiles(unpack, "soloist.exe", SearchOption.AllDirectories).FirstOrDefault()
                          ?? throw new SoloistException("в архиве Soloist нет soloist.exe");
                File.Move(exe, ExePath, overwrite: true);
                Log.Info("Soloist: скачана новая сборка");
            }
            finally
            {
                TryDelete(zip);
                try { if (Directory.Exists(unpack)) Directory.Delete(unpack, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Строка из «soloist --version» (версия и дата сборки) или null.</summary>
        public static async Task<string?> GetVersionAsync()
        {
            if (!IsInstalled) return null;
            try
            {
                var psi = new ProcessStartInfo(ExePath, "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(); } catch (Exception) { }
                    return null;
                }
                if (p.ExitCode == ExpiredExitCode) return "сборка устарела";
                var text = (await outTask).Trim();
                if (text.Length == 0) text = (await errTask).Trim();
                return text.Split('\n').FirstOrDefault()?.Trim();
            }
            catch (Exception ex)
            {
                Log.Warn("Soloist: не удалось узнать версию", ex);
                return null;
            }
        }

        // ────────── остановка ──────────

        private void StopProcess()
        {
            _stopping = true;
            _cts?.Cancel();
            _cts = null;
            try { _ws?.Abort(); } catch (Exception) { }
            _ws?.Dispose();
            _ws = null;
            var p = _proc;
            _proc = null;
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Soloist: не удалось остановить процесс", ex);
            }
            p.Dispose();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopProcess();
        }
    }

    public class SoloistException : Exception
    {
        public SoloistException(string message) : base(message) { }
    }

    /// <summary>Сборка Soloist старше 90 дней — нужна новая.</summary>
    public sealed class SoloistExpiredException : SoloistException
    {
        public SoloistExpiredException() : base("сборка Spotify Soloist устарела — нужна новая") { }
    }

    /// <summary>Soloist запущен, но в Spotify ещё не вошёл: устройство «Undertone Soloist» надо один раз выбрать в приложении Spotify.</summary>
    public sealed class SoloistNotPairedException : SoloistException
    {
        public SoloistNotPairedException()
            : base("Spotify Soloist ждёт подключения: открой Spotify на телефоне или ПК в той же сети, нажми значок устройств " +
                   "и выбери «Undertone Soloist». Это нужно один раз.") { }
    }
}
