using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// «Слушает …» в профиле Discord через локальный RPC (именованный канал discord-ipc-N).
    /// Нужен Application ID приложения из Discord Developer Portal (своё приложение с названием «Undertone»).
    /// Всё в фоне: Discord не запущен — тихо ждём и переподключаемся.
    /// </summary>
    public sealed class DiscordPresence : IDisposable
    {
        private const int OpHandshake = 0;
        private const int OpFrame = 1;
        private const int OpClose = 2;

        private readonly string _appId;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly object _sync = new();
        private Dictionary<string, object?>? _pending;
        private bool _hasPending;

        public DiscordPresence(string appId)
        {
            _appId = appId.Trim();
            _ = Task.Run(() => RunAsync(_cts.Token));
        }

        /// <summary>Показать трек. playing=false — убрать статус.</summary>
        public void Set(YaTrack? track, bool playing, TimeSpan position, TimeSpan duration)
        {
            Dictionary<string, object?>? activity = null;
            if (track != null && playing)
            {
                var now = DateTimeOffset.UtcNow;
                var start = now - position;
                activity = new Dictionary<string, object?>
                {
                    ["type"] = 2, // «Слушает»
                    ["details"] = Cut(track.FullTitle),
                    ["state"] = Cut(track.ArtistsText),
                    ["timestamps"] = new Dictionary<string, object>
                    {
                        ["start"] = start.ToUnixTimeMilliseconds(),
                        ["end"] = (start + duration).ToUnixTimeMilliseconds(),
                    },
                    ["assets"] = new Dictionary<string, object?>
                    {
                        ["large_image"] = track.CoverUrl(400) ?? "undertone",
                        ["large_text"] = Cut(string.IsNullOrEmpty(track.Album) ? track.FullTitle : track.Album),
                    },
                };
                if (track.WebUrl is { } url && (track.AlbumId.Length > 0 || ScApi.IsScId(track.Id)))
                    activity["buttons"] = new[]
                    {
                        new Dictionary<string, string>
                        {
                            ["label"] = ScApi.IsScId(track.Id) ? "Слушать в SoundCloud"
                                : SpotifyApi.IsSpId(track.Id) ? "Слушать в Spotify" : "Слушать в Яндекс Музыке",
                            ["url"] = url,
                        },
                    };
            }
            lock (_sync)
            {
                _pending = activity;
                _hasPending = true;
            }
            if (_signal.CurrentCount == 0)
            {
                try { _signal.Release(); } catch (SemaphoreFullException) { }
            }
        }

        private static string Cut(string s) => s.Length <= 120 ? (s.Length < 2 ? s + "  " : s) : s.Substring(0, 117) + "…";

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeClientStream? pipe = null;
                try
                {
                    pipe = await ConnectAsync(ct);
                    if (pipe == null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), ct); // Discord не запущен
                        continue;
                    }

                    await WriteAsync(pipe, OpHandshake, new Dictionary<string, object> { ["v"] = 1, ["client_id"] = _appId }, ct);
                    var (op, _) = await ReadAsync(pipe, ct);
                    if (op == OpClose) throw new IOException("Discord отклонил подключение (проверьте Application ID)");
                    Log.Info("Discord: подключено");
                    lock (_sync) _hasPending = true; // после переподключения отправим текущее состояние

                    // ответы Discord нужно вычитывать, иначе канал переполнится
                    using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var reader = Task.Run(async () =>
                    {
                        while (!readerCts.IsCancellationRequested)
                        {
                            var (o, _) = await ReadAsync(pipe, readerCts.Token);
                            if (o == OpClose) break;
                        }
                    }, readerCts.Token);

                    while (!ct.IsCancellationRequested && !reader.IsCompleted)
                    {
                        Dictionary<string, object?>? activity;
                        bool send;
                        lock (_sync)
                        {
                            send = _hasPending;
                            activity = _pending;
                            _hasPending = false;
                        }
                        if (send)
                        {
                            var args = new Dictionary<string, object?> { ["pid"] = Environment.ProcessId };
                            if (activity != null) args["activity"] = activity;
                            await WriteAsync(pipe, OpFrame, new Dictionary<string, object?>
                            {
                                ["cmd"] = "SET_ACTIVITY",
                                ["args"] = args,
                                ["nonce"] = Guid.NewGuid().ToString(),
                            }, ct);
                            // Discord ограничивает обновления статуса: не чаще ~раз в 4 секунды
                            await Task.Delay(TimeSpan.FromSeconds(4), ct);
                            continue;
                        }
                        await Task.WhenAny(_signal.WaitAsync(ct), reader);
                    }
                    readerCts.Cancel();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn("Discord: связь потеряна", ex);
                    try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
                    catch (OperationCanceledException) { break; }
                }
                finally
                {
                    pipe?.Dispose();
                }
            }
        }

        private static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken ct)
        {
            for (int i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(300, ct);
                    return pipe;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    pipe.Dispose();
                }
            }
            return null;
        }

        private static async Task WriteAsync(Stream pipe, int op, object payload, CancellationToken ct)
        {
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            var frame = new byte[8 + json.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), op);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), json.Length);
            json.CopyTo(frame, 8);
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }

        private static async Task<(int Op, string Json)> ReadAsync(Stream pipe, CancellationToken ct)
        {
            var header = new byte[8];
            await pipe.ReadExactlyAsync(header, ct);
            var op = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (len < 0 || len > 1 << 20) throw new IOException("Discord: неверный размер сообщения");
            var body = new byte[len];
            await pipe.ReadExactlyAsync(body, ct);
            return (op, Encoding.UTF8.GetString(body));
        }

        public void Dispose() => _cts.Cancel(); // фоновый цикл сам закроет канал; Discord уберёт статус
    }
}
