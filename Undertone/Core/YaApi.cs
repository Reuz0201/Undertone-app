using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Undertone.Core
{
    public sealed class YaTrack
    {
        public string Id { get; init; } = "";
        public string AlbumId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Version { get; init; } = "";
        public string[] Artists { get; init; } = Array.Empty<string>();
        public string Album { get; init; } = "";
        public long DurationMs { get; init; }
        public string? CoverUri { get; init; }
        public bool Available { get; init; } = true;

        public string FullTitle => string.IsNullOrWhiteSpace(Version) ? Title : $"{Title} ({Version})";
        public string ArtistsText => Artists.Length == 0 ? "Неизвестный исполнитель" : string.Join(", ", Artists);
        /// <summary>Формат id:albumId — так API ждёт треки в лайках/дизлайках.</summary>
        public string LikeId => string.IsNullOrEmpty(AlbumId) ? Id : $"{Id}:{AlbumId}";
        public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

        public string? CoverUrl(int size) =>
            string.IsNullOrEmpty(CoverUri) ? null : "https://" + CoverUri.Replace("%%", $"{size}x{size}");

        public override string ToString() => $"{ArtistsText} — {FullTitle}";
    }

    public sealed class YaAccount
    {
        public long Uid { get; init; }
        public string Login { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool HasPlus { get; init; }
    }

    public sealed class WaveBatch
    {
        public string SessionId { get; init; } = "";
        public string? BatchId { get; init; }
        public List<YaTrack> Tracks { get; init; } = new();
    }

    public sealed class YaApiException : Exception
    {
        public HttpStatusCode? Status { get; }
        public YaApiException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
        public bool IsUnauthorized => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }

    /// <summary>Клиент api.music.yandex.net (те же эндпоинты, что у библиотеки yandex-music).</summary>
    public sealed class YaApi : IDisposable
    {
        private const string Base = "https://api.music.yandex.net";
        private const string SignSalt = "XGRlBW9FXlekgbPrRHuSiA";
        private const int TracksChunk = 250;

        private readonly HttpClient _http;
        private readonly Func<string, CancellationToken, Task<string?>>? _refresh;
        private string _token;

        public long Uid { get; private set; }

        /// <param name="refresh">Вызывается при 401: получает старый токен, возвращает новый или null.</param>
        public YaApi(string token, Func<string, CancellationToken, Task<string?>>? refresh = null)
        {
            _token = token;
            _refresh = refresh;
            _http = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            { Timeout = TimeSpan.FromSeconds(25) };
            _http.DefaultRequestHeaders.Add("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Undertone/2.0");
        }

        // ────────── транспорт ──────────

        private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
        {
            var token = _token;
            var resp = await SendOnce(make, token, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && _refresh != null)
            {
                resp.Dispose();
                var fresh = await _refresh(token, ct);
                if (fresh != null)
                {
                    _token = fresh;
                    resp = await SendOnce(make, fresh, ct);
                }
                else
                {
                    throw new YaApiException("Токен недействителен — нужно войти заново.", HttpStatusCode.Unauthorized);
                }
            }
            return resp;
        }

        private async Task<HttpResponseMessage> SendOnce(Func<HttpRequestMessage> make, string token, CancellationToken ct)
        {
            var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
            return await _http.SendAsync(req, ct);
        }

        private async Task<JsonElement> RequestAsync(Func<HttpRequestMessage> make, CancellationToken ct)
        {
            using var resp = await SendAsync(make, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(body);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new YaApiException($"HTTP {(int)resp.StatusCode}: неожиданный ответ сервера", resp.StatusCode);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var message = $"HTTP {(int)resp.StatusCode}";
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                        message = m.GetString() ?? message;
                    else if (err.ValueKind == JsonValueKind.String)
                        message = err.GetString() ?? message;
                }
                throw new YaApiException(message, resp.StatusCode);
            }

            return root.TryGetProperty("result", out var result) ? result : root;
        }

        private Task<JsonElement> GetAsync(string path, CancellationToken ct) =>
            RequestAsync(() => new HttpRequestMessage(HttpMethod.Get, Base + path), ct);

        private Task<JsonElement> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct) =>
            RequestAsync(() => new HttpRequestMessage(HttpMethod.Post, Base + path)
            {
                Content = new FormUrlEncodedContent(form)
            }, ct);

        private Task<JsonElement> PostJsonAsync(string path, object body, CancellationToken ct) =>
            RequestAsync(() => new HttpRequestMessage(HttpMethod.Post, Base + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            }, ct);

        // ────────── аккаунт ──────────

        public async Task<YaAccount> GetAccountAsync(CancellationToken ct = default)
        {
            var r = await GetAsync("/account/status", ct);
            var acc = r.GetProperty("account");
            if (!acc.TryGetProperty("uid", out var uidEl))
                throw new YaApiException("Токен недействителен — нужно войти заново.", HttpStatusCode.Unauthorized);

            var account = new YaAccount
            {
                Uid = uidEl.ValueKind == JsonValueKind.Number ? uidEl.GetInt64() : long.Parse(uidEl.GetString()!),
                Login = Str(acc, "login"),
                DisplayName = FirstNonEmpty(Str(acc, "displayName"), Str(acc, "fullName"), Str(acc, "login")),
                HasPlus = r.TryGetProperty("plus", out var plus) &&
                          plus.TryGetProperty("hasPlus", out var hp) && hp.ValueKind == JsonValueKind.True,
            };
            Uid = account.Uid;
            return account;
        }

        // ────────── треки ──────────

        public async Task<List<YaTrack>> GetLikedTracksAsync(CancellationToken ct = default)
        {
            var r = await GetAsync($"/users/{Uid}/likes/tracks?if-modified-since-revision=0", ct);
            var ids = new List<string>();
            if (r.TryGetProperty("library", out var lib) && lib.TryGetProperty("tracks", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in arr.EnumerateArray())
                {
                    var id = Str(t, "id");
                    if (id.Length == 0) continue;
                    var album = Str(t, "albumId");
                    ids.Add(album.Length > 0 ? $"{id}:{album}" : id);
                }
            }
            return await GetTracksAsync(ids, ct);
        }

        /// <summary>Полная информация о треках. Порядок сохраняется.</summary>
        public async Task<List<YaTrack>> GetTracksAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        {
            var byId = new Dictionary<string, YaTrack>();
            for (int i = 0; i < ids.Count; i += TracksChunk)
            {
                var chunk = ids.Skip(i).Take(TracksChunk).ToList();
                var r = await PostFormAsync("/tracks", new Dictionary<string, string>
                {
                    ["track-ids"] = string.Join(",", chunk),
                    ["with-positions"] = "false",
                }, ct);
                if (r.ValueKind != JsonValueKind.Array) continue;
                foreach (var t in r.EnumerateArray())
                {
                    var track = ParseTrack(t);
                    if (track.Id.Length > 0) byId[track.Id] = track;
                }
            }
            var result = new List<YaTrack>(ids.Count);
            foreach (var full in ids)
            {
                var id = full.Split(':')[0];
                if (byId.TryGetValue(id, out var t)) result.Add(t);
            }
            return result;
        }

        public async Task<List<YaTrack>> SearchTracksAsync(string query, int page = 0, CancellationToken ct = default)
        {
            var q = Uri.EscapeDataString(query);
            var r = await GetAsync($"/search?text={q}&type=track&page={page}&nocorrect=false", ct);
            var result = new List<YaTrack>();
            if (r.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Object &&
                tr.TryGetProperty("results", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in items.EnumerateArray()) result.Add(ParseTrack(t));
            }
            return result;
        }

        public Task SetLikeAsync(YaTrack track, bool like, CancellationToken ct = default) =>
            PostFormAsync(like
                    ? $"/users/{Uid}/likes/tracks/add-multiple"
                    : $"/users/{Uid}/likes/tracks/remove",
                new Dictionary<string, string> { ["track-ids"] = track.LikeId }, ct);

        public Task DislikeAsync(YaTrack track, CancellationToken ct = default) =>
            PostFormAsync($"/users/{Uid}/dislikes/tracks/add-multiple",
                new Dictionary<string, string> { ["track-ids"] = track.LikeId }, ct);

        /// <summary>Прямая ссылка на mp3 (лучший доступный битрейт).</summary>
        public async Task<string> GetDirectUrlAsync(string trackId, CancellationToken ct = default)
        {
            var r = await GetAsync($"/tracks/{trackId}/download-info", ct);
            if (r.ValueKind != JsonValueKind.Array)
                throw new YaApiException("Трек недоступен для воспроизведения");

            var best = r.EnumerateArray()
                .Where(i => Str(i, "codec") == "mp3" && Str(i, "downloadInfoUrl").Length > 0)
                .OrderBy(i => i.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.True ? 1 : 0)
                .ThenByDescending(i => Int(i, "bitrateInKbps"))
                .Select(i => Str(i, "downloadInfoUrl"))
                .FirstOrDefault();
            if (best == null)
                throw new YaApiException("Нет mp3-версии этого трека");

            using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, best), ct);
            resp.EnsureSuccessStatusCode();
            var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            string X(string name) => xml.Root?.Element(name)?.Value ?? "";

            var host = X("host");
            var path = X("path");
            var ts = X("ts");
            var s = X("s");
            if (host.Length == 0 || path.Length == 0)
                throw new YaApiException("Сервер не вернул ссылку на файл");

            return $"https://{host}/get-mp3/{Sign(path, s)}/{ts}{path}";
        }

        internal static string Sign(string path, string s)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(SignSalt + path.Substring(1) + s));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        // ────────── Моя волна (rotor) ──────────

        public async Task<WaveBatch> StartWaveAsync(IEnumerable<string> seeds, CancellationToken ct = default)
        {
            var r = await PostJsonAsync("/rotor/session/new", new Dictionary<string, object>
            {
                ["seeds"] = seeds.ToArray(),
                ["includeTracksInResponse"] = true,
            }, ct);
            return ParseWave(r, null);
        }

        public async Task<WaveBatch> GetWaveTracksAsync(string sessionId, IEnumerable<string> queue,
            CancellationToken ct = default)
        {
            var r = await PostJsonAsync($"/rotor/session/{sessionId}/tracks", new Dictionary<string, object>
            {
                ["queue"] = queue.ToArray(),
            }, ct);
            return ParseWave(r, sessionId);
        }

        /// <summary>Фидбэк волне: radioStarted, trackStarted, trackFinished, skip.</summary>
        public async Task WaveFeedbackAsync(string sessionId, string? batchId, string type,
            string? trackId = null, double? playedSeconds = null, CancellationToken ct = default)
        {
            var ev = new Dictionary<string, object>
            {
                ["type"] = type,
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            };
            if (trackId != null) ev["trackId"] = trackId;
            if (playedSeconds != null) ev["totalPlayedSeconds"] = Math.Round(playedSeconds.Value, 1);

            var body = new Dictionary<string, object> { ["event"] = ev };
            if (batchId != null) body["batchId"] = batchId;

            try { await PostJsonAsync($"/rotor/session/{sessionId}/feedback", body, ct); }
            catch (Exception ex) when (ex is YaApiException or HttpRequestException or TaskCanceledException)
            {
                // фидбэк не критичен
            }
        }

        private static WaveBatch ParseWave(JsonElement r, string? sessionId)
        {
            var batch = new WaveBatch
            {
                SessionId = FirstNonEmpty(Str(r, "radioSessionId"), sessionId ?? ""),
                BatchId = Str(r, "batchId") is { Length: > 0 } b ? b : null,
            };
            if (r.TryGetProperty("sequence", out var seq) && seq.ValueKind == JsonValueKind.Array)
                foreach (var item in seq.EnumerateArray())
                    if (item.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object)
                        batch.Tracks.Add(ParseTrack(t));
            if (batch.SessionId.Length == 0)
                throw new YaApiException("Не удалось запустить Мою волну");
            return batch;
        }

        // ────────── парсинг ──────────

        internal static YaTrack ParseTrack(JsonElement e)
        {
            string album = "", albumId = "";
            string? cover = Str(e, "coverUri") is { Length: > 0 } c ? c : null;
            if (e.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array)
            {
                var first = albums.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    album = Str(first, "title");
                    albumId = Str(first, "id");
                    cover ??= Str(first, "coverUri") is { Length: > 0 } ac ? ac : null;
                }
            }
            cover ??= Str(e, "ogImage") is { Length: > 0 } og ? og : null;

            string[] artists = Array.Empty<string>();
            if (e.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
                artists = arr.EnumerateArray().Select(a => Str(a, "name")).Where(s => s.Length > 0).ToArray();

            return new YaTrack
            {
                Id = Str(e, "id"),
                AlbumId = albumId,
                Title = Str(e, "title"),
                Version = Str(e, "version"),
                Artists = artists,
                Album = album,
                DurationMs = Int(e, "durationMs"),
                CoverUri = cover,
                Available = !(e.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.False),
            };
        }

        private static string Str(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            };
        }

        private static long Int(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var p)) return p;
            return 0;
        }

        private static string FirstNonEmpty(params string[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        public void Dispose() => _http.Dispose();
    }
}
