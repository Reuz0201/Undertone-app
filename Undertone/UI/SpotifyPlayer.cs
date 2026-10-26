using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Undertone.Audio;
using Undertone.Core;

namespace Undertone.UI
{
    /// <summary>
    /// Звук Spotify. Spotify не отдаёт файлы, поэтому играет его собственный плеер:
    /// 1) встроенный — Web Playback SDK в невидимом WebView2 (устройство «Undertone» в Spotify Connect, нужен Premium);
    /// 2) запасной — приложение Spotify на этом ПК или телефоне: Undertone управляет им через Spotify Connect.
    /// Встроенный не завёлся (нет Widevine в WebView2, нет Premium) — переходим на запасной и говорим об этом.
    /// Все события и свойства — в UI-потоке.
    /// </summary>
    public sealed class SpotifyPlayer : IExternalPlayer, IDisposable
    {
        private const string Host = "undertone-player.local";
        private static readonly TimeSpan SdkStartTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

        private readonly SpotifyApi _api;
        private readonly Dispatcher _ui;
        private readonly Action<string> _notify;

        // ── встроенный плеер ──
        private Window? _host;
        private WebView2? _web;
        private string? _sdkDeviceId;
        private TaskCompletionSource<string>? _sdkReady;
        private string? _sdkError;

        // ── Spotify Connect ──
        private string? _connectDeviceId;
        private string? _connectDeviceName;
        private readonly DispatcherTimer _poll;

        // ── что играет ──
        private string? _trackId;
        private bool _playing;
        private long _posMs;
        private DateTime _posAt = DateTime.UtcNow;
        private long _durationMs;
        private bool _endedFired;
        private bool _sawOurTrack;
        private TaskCompletionSource<bool>? _started;
        private double _volume = 0.35;
        private DispatcherTimer? _volumeDebounce;
        private bool _disposed;

        public SpotifyPlayer(SpotifyApi api, Dispatcher ui, Action<string> notify)
        {
            _api = api;
            _ui = ui;
            _notify = notify;
            _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
            _poll.Tick += async (_, _) => await PollConnectAsync();
        }

        /// <summary>Где сейчас звук: «Undertone» (встроенный) или имя устройства Spotify Connect.</summary>
        public string? OutputName => _sdkDeviceId != null ? "Undertone" : _connectDeviceName;

        public bool UsesConnect => _sdkDeviceId == null && _connectDeviceId != null;

        private string? DeviceId => _sdkDeviceId ?? _connectDeviceId;

        public event EventHandler? Ended;
        public event EventHandler<Exception>? Failed;

        // ────────── IExternalPlayer ──────────

        public bool IsPlaying => _playing;

        public TimeSpan Position
        {
            get
            {
                var ms = _posMs + (_playing ? (long)(DateTime.UtcNow - _posAt).TotalMilliseconds : 0);
                if (_durationMs > 0) ms = Math.Min(ms, _durationMs);
                return TimeSpan.FromMilliseconds(Math.Max(0, ms));
            }
        }

        public TimeSpan? Duration => _durationMs > 0 ? TimeSpan.FromMilliseconds(_durationMs) : null;

        public void Play()
        {
            if (DeviceId is not { } device || _trackId == null) return;
            SetLocal(true, Position);
            if (_sdkDeviceId != null) Run("window.undertone.resume()");
            else _ = Safe(() => _api.ResumeAsync(device));
        }

        public void Pause()
        {
            if (DeviceId is not { } device || !_playing) return;
            SetLocal(false, Position);
            if (_sdkDeviceId != null) Run("window.undertone.pause()");
            else _ = Safe(() => _api.PauseAsync(device));
        }

        public void Stop() => Pause();

        public void Seek(TimeSpan position)
        {
            if (DeviceId is not { } device) return;
            var ms = (long)position.TotalMilliseconds;
            SetLocal(_playing, position);
            if (_sdkDeviceId != null) Run($"window.undertone.seek({ms})");
            else _ = Safe(() => _api.SeekAsync(device, ms));
        }

        /// <summary>Встроенный — сразу; у приложения Spotify — с задержкой, чтобы не засыпать сервер, пока тянут ползунок.</summary>
        public void SetVolume(double volume)
        {
            _volume = Math.Clamp(volume, 0, 1);
            if (_sdkDeviceId != null)
            {
                Run($"window.undertone.volume({_volume.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
                return;
            }
            if (_connectDeviceId == null) return;
            _volumeDebounce ??= CreateVolumeDebounce();
            _volumeDebounce.Stop();
            _volumeDebounce.Start();
        }

        private DispatcherTimer CreateVolumeDebounce()
        {
            var t = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(350) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_connectDeviceId is { } d)
                    _ = Safe(() => _api.SetVolumeAsync(d, (int)Math.Round(Math.Cbrt(_volume) * 100)), quiet: true);
            };
            return t;
        }

        // ────────── запуск трека ──────────

        /// <summary>Включает трек с позиции и ждёт, пока он реально заиграет.</summary>
        public async Task LoadAsync(YaTrack track, TimeSpan start, CancellationToken ct)
        {
            var device = await EnsureDeviceAsync(ct);
            _trackId = track.Id;
            _durationMs = track.DurationMs;
            _endedFired = false;
            _sawOurTrack = false;
            SetLocal(false, start);
            _started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await _api.PlayAsync(device, track.Id, (long)start.TotalMilliseconds, ct);
            }
            catch (YaApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound && UsesConnect)
            {
                // приложение Spotify «уснуло» — будим переносом воспроизведения и пробуем ещё раз
                await _api.TransferAsync(device, play: false, ct);
                await _api.PlayAsync(device, track.Id, (long)start.TotalMilliseconds, ct);
            }

            if (UsesConnect)
            {
                SetLocal(true, start);
                _sawOurTrack = true;
                _poll.Start();
                return;
            }

            // встроенный: ждём player_state_changed с нашим треком
            var done = await Task.WhenAny(_started.Task, Task.Delay(LoadTimeout, ct));
            ct.ThrowIfCancellationRequested();
            if (done != _started.Task)
            {
                Log.Warn("Spotify: трек не заиграл за 10 с");
                throw new YaApiException("Spotify не начал играть трек — попробуй ещё раз");
            }
            SetVolume(_volume);
        }

        /// <summary>Куда играть: встроенный плеер (если заведётся), иначе приложение Spotify.</summary>
        private async Task<string> EnsureDeviceAsync(CancellationToken ct)
        {
            if (_sdkDeviceId != null) return _sdkDeviceId;
            if (_sdkError == null)
            {
                try
                {
                    var id = await StartSdkAsync(ct);
                    await _api.TransferAsync(id, play: false, ct);
                    return id;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _sdkError ??= ex.Message;
                    Log.Warn("Spotify: встроенный плеер не завёлся, перехожу на Spotify Connect", ex);
                    CloseSdk();
                    if (ex is SpotifyAccountException) throw; // без Premium и Connect не поможет
                }
            }
            if (_connectDeviceId != null) return _connectDeviceId;

            var devices = await _api.GetDevicesAsync(ct);
            var pick = devices.Where(d => !d.IsRestricted)
                .OrderByDescending(d => d.IsActive)
                .ThenByDescending(d => d.Type.Equals("Computer", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (pick == null)
                throw new YaApiException(
                    "Встроенный плеер Spotify не запустился" + (_sdkError != null ? $" ({_sdkError})" : "") +
                    ". Открой приложение Spotify на этом компьютере или телефоне — Undertone будет играть через него.");
            _connectDeviceId = pick.Id;
            _connectDeviceName = pick.Name;
            _notify($"Spotify играет через «{pick.Name}» (Spotify Connect)");
            return pick.Id;
        }

        // ────────── встроенный плеер: Web Playback SDK в WebView2 ──────────

        private async Task<string> StartSdkAsync(CancellationToken ct)
        {
            if (_sdkDeviceId != null) return _sdkDeviceId;
            if (_sdkReady == null)
            {
                _sdkReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                await CreateWebViewAsync();
            }
            var ready = _sdkReady.Task;
            var done = await Task.WhenAny(ready, Task.Delay(SdkStartTimeout, ct));
            ct.ThrowIfCancellationRequested();
            if (done != ready) throw new TimeoutException("плеер не ответил за 20 секунд");
            return await ready;
        }

        private async Task CreateWebViewAsync()
        {
            var folder = Path.Combine(EnvFile.DataDirectory, "spotify-player");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "player.html"), PlayerHtml);

            // окно за пределами экрана: без него WebView2 не живёт; в панель задач и Alt+Tab оно не попадает
            _host = new Window
            {
                Title = "Undertone · Spotify",
                Width = 320,
                Height = 200,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = false,
            };
            _host.SourceInitialized += (_, _) => HideFromAltTab(_host);
            _web = new WebView2();
            _host.Content = _web;
            _host.Show();

            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(EnvFile.DataDirectory, "webview2"), options);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.ProcessFailed += (_, e) =>
            {
                Log.Warn($"Spotify: процесс WebView2 упал ({e.ProcessFailedKind})");
                FailSdk("плеер Spotify перезапускается");
            };
            core.Navigate($"https://{Host}/player.html");
        }

        private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonElement m;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                m = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return;
            }
            switch (Str(m, "type"))
            {
                case "needToken":
                    _ = SendTokenAsync();
                    break;
                case "ready":
                    _sdkDeviceId = Str(m, "deviceId");
                    Log.Info("Spotify: встроенный плеер готов");
                    _sdkReady?.TrySetResult(_sdkDeviceId);
                    break;
                case "notReady":
                    Log.Warn("Spotify: встроенный плеер отключился");
                    break;
                case "error":
                    OnSdkError(Str(m, "kind"), Str(m, "message"));
                    break;
                case "state":
                    OnSdkState(m);
                    break;
            }
        }

        private async Task SendTokenAsync()
        {
            try
            {
                var token = await _api.GetAccessTokenAsync();
                _web?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "token", token }));
            }
            catch (Exception ex)
            {
                Log.Warn("Spotify: токен для плеера не получен", ex);
            }
        }

        private void OnSdkError(string kind, string message)
        {
            Log.Warn($"Spotify SDK: {kind} — {message}");
            switch (kind)
            {
                case "account_error":
                    FailSdk("нужен Spotify Premium", new SpotifyAccountException(
                        "Spotify играет в сторонних плеерах только с Premium — проверь подписку на этом аккаунте."));
                    break;
                case "authentication_error":
                    FailSdk("вход устарел — войди в Spotify заново");
                    break;
                case "drm":
                case "initialization_error":
                case "connect":
                    FailSdk("нет защищённого воспроизведения (Widevine) во встроенном браузере");
                    break;
                case "playback_error":
                    if (_trackId != null) Failed?.Invoke(this, new YaApiException("Spotify не смог сыграть трек: " + message));
                    break;
                case "autoplay":
                    _notify("Нажми ▶ ещё раз — Windows не дал Spotify включить звук сам");
                    break;
            }
        }

        private void FailSdk(string reason, Exception? ex = null)
        {
            _sdkError = reason;
            var wasReady = _sdkDeviceId != null;
            _sdkReady?.TrySetException(ex ?? new InvalidOperationException(reason));
            if (!wasReady) return;
            // отвалился посреди работы — следующий трек пойдёт через Connect
            CloseSdk();
            if (_trackId != null) Failed?.Invoke(this, ex ?? new YaApiException("встроенный плеер Spotify остановился: " + reason));
        }

        private void OnSdkState(JsonElement m)
        {
            if (m.TryGetProperty("none", out _)) return;
            var id = Str(m, "trackId") is { Length: > 0 } t ? SpotifyApi.ToId(t) : null;
            var linked = Str(m, "linked") is { Length: > 0 } l ? SpotifyApi.ToId(l) : null;
            var paused = m.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True;
            var pos = Long(m, "position");
            var dur = Long(m, "duration");
            var ours = _trackId != null && (id == _trackId || linked == _trackId);

            if (ours)
            {
                _sawOurTrack = true;
                if (dur > 0) _durationMs = dur;
                SetLocal(!paused, TimeSpan.FromMilliseconds(pos));
                if (!paused) _started?.TrySetResult(true);
            }

            if (!_sawOurTrack || _endedFired || _trackId == null) return;
            var previous = m.TryGetProperty("prev", out var prev) && prev.ValueKind == JsonValueKind.Array
                ? prev.EnumerateArray().Select(x => SpotifyApi.ToId(x.GetString() ?? "")).ToList()
                : new();
            // трек доиграл: Spotify встал на паузу в начале с нашим треком в «предыдущих» — или сам включил другой
            var finished = (paused && pos == 0 && previous.Contains(_trackId)) ||
                           (!ours && id != null && previous.Contains(_trackId));
            if (finished) FireEnded();
        }

        private void FireEnded()
        {
            if (_endedFired) return;
            _endedFired = true;
            SetLocal(false, Duration ?? TimeSpan.Zero);
            Ended?.Invoke(this, EventArgs.Empty);
        }

        private void Run(string script)
        {
            try { _ = _web?.CoreWebView2?.ExecuteScriptAsync(script); }
            catch (Exception ex) { Log.Warn("Spotify: команда плееру не ушла", ex); }
        }

        private void CloseSdk()
        {
            _sdkDeviceId = null;
            try
            {
                if (_web?.CoreWebView2 != null) _web.CoreWebView2.WebMessageReceived -= OnWebMessage;
                _web?.Dispose();
            }
            catch (Exception) { }
            _web = null;
            _host?.Close();
            _host = null;
        }

        // ────────── Spotify Connect: опрос состояния ──────────

        private async Task PollConnectAsync()
        {
            if (!UsesConnect || _trackId == null || _endedFired) return;
            SpotifyPlayerState? s;
            try
            {
                s = await _api.GetPlayerStateAsync();
            }
            catch (Exception)
            {
                return; // сеть моргнула — спросим через секунду
            }
            if (_trackId == null || _endedFired) return;
            var nearEnd = _durationMs > 0 && Position.TotalMilliseconds >= _durationMs - 3000;
            if (s == null || s.TrackId != _trackId)
            {
                // доиграл и Spotify переключил/остановился — дальше наша очередь; иначе в Spotify включили своё
                if (nearEnd) FireEnded();
                else if (_playing) SetLocal(false, Position);
                return;
            }
            if (s.DurationMs > 0) _durationMs = s.DurationMs;
            if (!s.IsPlaying && s.ProgressMs == 0 && nearEnd)
            {
                FireEnded();
                return;
            }
            SetLocal(s.IsPlaying, TimeSpan.FromMilliseconds(s.ProgressMs));
        }

        // ────────── общее ──────────

        private void SetLocal(bool playing, TimeSpan position)
        {
            _playing = playing;
            _posMs = (long)position.TotalMilliseconds;
            _posAt = DateTime.UtcNow;
        }

        private async Task Safe(Func<Task> command, bool quiet = false)
        {
            try
            {
                await command();
            }
            catch (Exception ex)
            {
                Log.Warn("Spotify: команда не выполнена", ex);
                if (!quiet && !_disposed) _notify("Spotify: " + ex.Message);
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        private static long Long(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt64(out var n) ? n : 0;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _poll.Stop();
            _volumeDebounce?.Stop();
            if (_playing) Pause();
            _trackId = null;
            CloseSdk();
        }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);

        private static void HideFromAltTab(Window w)
        {
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var hwnd = new WindowInteropHelper(w).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        /// <summary>
        /// Страница плеера: проверяет Widevine, запускает Spotify.Player и пересылает события в приложение.
        /// Токен берёт у приложения (оно его обновляет), сама ничего не хранит.
        /// </summary>
        private const string PlayerHtml = """
<!doctype html>
<html><head><meta charset="utf-8"><title>Undertone · Spotify</title></head>
<body>
<script>
const post = m => window.chrome.webview.postMessage(m);
let player = null;
let tokenWaiters = [];
window.chrome.webview.addEventListener('message', e => {
  const m = e.data;
  if (m && m.type === 'token') { const w = tokenWaiters; tokenWaiters = []; w.forEach(cb => cb(m.token)); }
});
async function hasWidevine() {
  try {
    await navigator.requestMediaKeySystemAccess('com.widevine.alpha', [{
      initDataTypes: ['cenc'],
      audioCapabilities: [{ contentType: 'audio/mp4; codecs="mp4a.40.2"' }]
    }]);
    return true;
  } catch (e) { return false; }
}
function sendState(s) {
  if (!s) { post({ type: 'state', none: true }); return; }
  const t = s.track_window && s.track_window.current_track;
  post({
    type: 'state', paused: s.paused, position: s.position, duration: s.duration,
    trackId: t ? t.id : null, linked: t && t.linked_from ? t.linked_from.id : null,
    prev: (s.track_window ? s.track_window.previous_tracks : []).map(x => x.id)
  });
}
window.onSpotifyWebPlaybackSDKReady = async () => {
  if (!(await hasWidevine())) { post({ type: 'error', kind: 'drm', message: 'Widevine недоступен' }); return; }
  player = new Spotify.Player({
    name: 'Undertone',
    getOAuthToken: cb => { tokenWaiters.push(cb); post({ type: 'needToken' }); },
    volume: 0.35
  });
  player.addListener('ready', ({ device_id }) => post({ type: 'ready', deviceId: device_id }));
  player.addListener('not_ready', ({ device_id }) => post({ type: 'notReady', deviceId: device_id }));
  for (const k of ['initialization_error', 'authentication_error', 'account_error', 'playback_error'])
    player.addListener(k, ({ message }) => post({ type: 'error', kind: k, message }));
  player.addListener('autoplay_failed', () => post({ type: 'error', kind: 'autoplay', message: 'autoplay blocked' }));
  player.addListener('player_state_changed', sendState);
  setInterval(async () => { if (player) { const s = await player.getCurrentState(); if (s && !s.paused) sendState(s); } }, 1000);
  const ok = await player.connect();
  if (!ok) post({ type: 'error', kind: 'connect', message: 'connect() = false' });
};
window.undertone = {
  pause: () => player && player.pause(),
  resume: () => player && player.resume(),
  seek: ms => player && player.seek(ms),
  volume: v => player && player.setVolume(v)
};
</script>
<script src="https://sdk.scdn.co/spotify-player.js"></script>
</body></html>
""";
    }

    /// <summary>Аккаунт не может играть в сторонних плеерах (нет Premium) — запасной путь тоже не поможет.</summary>
    public sealed class SpotifyAccountException : Exception
    {
        public SpotifyAccountException(string message) : base(message) { }
    }
}
