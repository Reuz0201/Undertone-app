using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Undertone.Core
{
    public sealed record YaArtistRef(string Id, string Name);

    public sealed class YaTrack
    {
        public string Id { get; init; } = "";
        public string AlbumId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Version { get; init; } = "";
        public string[] Artists { get; init; } = Array.Empty<string>();
        public YaArtistRef[] ArtistRefs { get; init; } = Array.Empty<YaArtistRef>();
        public string Album { get; init; } = "";
        public long DurationMs { get; init; }
        public string? CoverUri { get; init; }
        public bool Available { get; init; } = true;
        public bool HasSyncLyrics { get; init; }
        public bool HasLyrics { get; init; }

        [JsonIgnore] public string FullTitle => string.IsNullOrWhiteSpace(Version) ? Title : $"{Title} ({Version})";
        [JsonIgnore] public string ArtistsText => Artists.Length == 0 ? "Неизвестный исполнитель" : string.Join(", ", Artists);
        /// <summary>Формат id:albumId — так API ждёт треки в лайках/дизлайках.</summary>
        [JsonIgnore] public string LikeId => string.IsNullOrEmpty(AlbumId) ? Id : $"{Id}:{AlbumId}";
        [JsonIgnore] public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

        /// <summary>Яндекс: шаблон с «%%»; SoundCloud: полный URL; свой файл: file:… (см. <see cref="CoverUris"/>).</summary>
        public string? CoverUrl(int size) => CoverUris.Resolve(CoverUri, size);

        /// <summary>Сервис отдаёт только короткий фрагмент (SoundCloud: access = preview).</summary>
        public bool IsPreview { get; init; }

        /// <summary>Страница трека на сайте сервиса, если он её сообщил (SoundCloud: permalink_url).</summary>
        public string? Permalink { get; init; }

        /// <summary>Ссылка «Поделиться»: страница трека в его сервисе. null — у своего файла ссылки нет.</summary>
        [JsonIgnore]
        public string? WebUrl =>
            !string.IsNullOrEmpty(Permalink) ? Permalink
            : SpotifyApi.IsSpId(Id) ? $"https://open.spotify.com/track/{Id[SpotifyApi.IdPrefix.Length..]}"
            : Id.StartsWith(LocalLibrary.IdPrefix, StringComparison.Ordinal) || ScApi.IsScId(Id) ? null
            : AlbumId.Length > 0 ? $"https://music.yandex.ru/album/{AlbumId}/track/{Id}"
            : $"https://music.yandex.ru/track/{Id}";

        public override string ToString() => $"{ArtistsText} — {FullTitle}";
    }

    public sealed class YaAlbum
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public int Year { get; init; }
        public string? CoverUri { get; init; }
        public int TrackCount { get; init; }
        public string Type { get; init; } = "";
        public YaArtistRef[] Artists { get; init; } = Array.Empty<YaArtistRef>();
        public string ArtistsText => string.Join(", ", Artists.Select(a => a.Name));
        public string? CoverUrl(int size) =>
            CoverUris.Resolve(CoverUri, size);
    }

    public sealed class YaArtist
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? CoverUri { get; init; }
        public string[] Genres { get; init; } = Array.Empty<string>();
        public long LikesCount { get; init; }
        public string? CoverUrl(int size) =>
            CoverUris.Resolve(CoverUri, size);
    }

    public sealed class YaArtistPage
    {
        public YaArtist Artist { get; init; } = new();
        public List<YaTrack> Tracks { get; init; } = new();
        public List<YaAlbum> Albums { get; init; } = new();
        /// <summary>Похожие артисты (у Spotify — по соавторам и жанрам), если сервис их знает.</summary>
        public List<YaArtist> Similar { get; init; } = new();
    }

    public sealed class YaPlaylist
    {
        public string Kind { get; init; } = "";
        public long OwnerUid { get; init; }
        public string OwnerName { get; init; } = "";
        public string Title { get; init; } = "";
        public int TrackCount { get; init; }
        public int Revision { get; init; }
        public string? CoverUri { get; init; }
        public long DurationMs { get; init; }
        public string? CoverUrl(int size) =>
            CoverUris.Resolve(CoverUri, size);
    }

    public sealed class YaAccount
    {
        public long Uid { get; init; }
        public string Login { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool HasPlus { get; init; }
        public string? AvatarUrl { get; set; }
    }

    public sealed class WaveBatch
    {
        public string SessionId { get; init; } = "";
        public string? BatchId { get; init; }
        public List<YaTrack> Tracks { get; init; } = new();
    }

    /// <summary>Лучший результат поиска: что-то одно из артиста, альбома, плейлиста или трека.</summary>
    public sealed class YaSearchBest
    {
        public string Type { get; init; } = "";
        public YaArtist? Artist { get; init; }
        public YaAlbum? Album { get; init; }
        public YaPlaylist? Playlist { get; init; }
        public YaTrack? Track { get; init; }
    }

    public sealed class YaSearchResult
    {
        public YaSearchBest? Best { get; init; }
        public List<YaTrack> Tracks { get; init; } = new();
        public int TracksTotal { get; init; }
        public List<YaArtist> Artists { get; init; } = new();
        public List<YaAlbum> Albums { get; init; } = new();
        public List<YaPlaylist> Playlists { get; init; } = new();
    }

    /// <summary>Ссылка на файл нового API get-file-info (в том числе lossless).</summary>
    public sealed record YaFileInfo(string Url, string Codec, string? Key, string Quality, double? LoudnessLufs);

    public sealed class YaApiException : Exception
    {
        public HttpStatusCode? Status { get; }
        public YaApiException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
        public bool IsUnauthorized => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }

    /// <summary>Клиент api.music.yandex.net (те же эндпоинты, что у библиотеки yandex-music).</summary>
    public sealed class YaApi : IMusicService
    {
        private const string Base = "https://api.music.yandex.net";
        private const string SignSalt = "XGRlBW9FXlekgbPrRHuSiA";
        private const int TracksChunk = 250;

        private readonly HttpClient _http;

        public long Uid { get; private set; }

        public MusicServiceKind Kind => MusicServiceKind.Yandex;
        public bool SupportsWave => true;
        public bool SupportsDislike => true;
        public bool SupportsLossless => true;
        public bool SupportsLyrics => true;
        public bool SupportsAlbums => true;

        /// <summary>Чужие id (sc-…, sp-…, local-…) Яндексу не отправляем: он ответил бы ошибкой на всю пачку.</summary>
        internal static bool IsYandexId(string id) => id.Length > 0 && !ScApi.IsScId(id) && !SpotifyApi.IsSpId(id) &&
                                                      !id.StartsWith(LocalLibrary.IdPrefix, StringComparison.Ordinal);

        /// <param name="refresh">Вызывается при 401: получает старый токен, возвращает новый или null.</param>
        public YaApi(string token, Func<string, CancellationToken, Task<string?>>? refresh = null)
            : this(new YaHttpHandler(token, refresh, new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }))
        {
        }

        /// <summary>Для тестов: любой обработчик вместо настоящей сети.</summary>
        internal YaApi(HttpMessageHandler handler)
        {
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(70) };
            _http.DefaultRequestHeaders.Add("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru");
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Undertone/" + Updater.AppVersion.ToString(3));
        }

        // ────────── транспорт (токен и повторы — в YaHttpHandler) ──────────

        private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            _http.SendAsync(request, ct);

        private async Task<JsonElement> RequestAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var owned = request;
            using var resp = await SendAsync(request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(body);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                Log.Warn($"{request.Method} {request.RequestUri?.AbsolutePath}: HTTP {(int)resp.StatusCode}, не JSON");
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
                Log.Warn($"{request.Method} {request.RequestUri?.AbsolutePath}: {message}");
                throw new YaApiException(message, resp.StatusCode);
            }

            return root.TryGetProperty("result", out var result) ? result : root;
        }

        private Task<JsonElement> GetAsync(string path, CancellationToken ct) =>
            RequestAsync(new HttpRequestMessage(HttpMethod.Get, Base + path), ct);

        private Task<JsonElement> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct) =>
            RequestAsync(new HttpRequestMessage(HttpMethod.Post, Base + path)
            {
                Content = new FormUrlEncodedContent(form)
            }, ct);

        private Task<JsonElement> PostJsonAsync(string path, object body, CancellationToken ct) =>
            RequestAsync(new HttpRequestMessage(HttpMethod.Post, Base + path)
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
            var (_, ids) = await GetLikedIdsAsync(ct);
            return await GetTracksAsync(ids, ct);
        }

        /// <summary>Только id лайкнутых треков (id:albumId) и ревизия библиотеки — один лёгкий запрос.</summary>
        public async Task<(int Revision, List<string> Ids)> GetLikedIdsAsync(CancellationToken ct = default)
        {
            var r = await GetAsync($"/users/{Uid}/likes/tracks?if-modified-since-revision=0", ct);
            var ids = new List<string>();
            int revision = 0;
            if (r.TryGetProperty("library", out var lib))
            {
                revision = (int)Int(lib, "revision");
                if (lib.TryGetProperty("tracks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in arr.EnumerateArray())
                    {
                        var id = Str(t, "id");
                        if (id.Length == 0) continue;
                        var album = Str(t, "albumId");
                        ids.Add(album.Length > 0 ? $"{id}:{album}" : id);
                    }
                }
            }
            return (revision, ids);
        }

        /// <summary>Полная информация о треках. Порядок сохраняется. Пачки по 250 идут параллельно, по 4 запроса.</summary>
        public async Task<List<YaTrack>> GetTracksAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        {
            ids = ids.Where(IsYandexId).ToList();
            var byId = new ConcurrentDictionary<string, YaTrack>();
            var chunks = new List<List<string>>();
            for (int i = 0; i < ids.Count; i += TracksChunk)
                chunks.Add(ids.Skip(i).Take(TracksChunk).ToList());

            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(chunks.Select(async chunk =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var r = await PostFormAsync("/tracks", new Dictionary<string, string>
                    {
                        ["track-ids"] = string.Join(",", chunk),
                        ["with-positions"] = "false",
                    }, ct);
                    if (r.ValueKind != JsonValueKind.Array) return;
                    foreach (var t in r.EnumerateArray())
                    {
                        var track = ParseTrack(t);
                        if (track.Id.Length > 0) byId[track.Id] = track;
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));

            var result = new List<YaTrack>(ids.Count);
            foreach (var full in ids)
            {
                var id = full.Split(':')[0];
                if (byId.TryGetValue(id, out var t)) result.Add(t);
            }
            return result;
        }

        /// <summary>Поиск по всему каталогу: лучший результат, треки, артисты, альбомы, плейлисты.</summary>
        public async Task<YaSearchResult> SearchAsync(string query, CancellationToken ct = default)
        {
            var q = Uri.EscapeDataString(query);
            var r = await GetAsync($"/search?text={q}&type=all&page=0&nocorrect=false", ct);

            List<T> Section<T>(string name, Func<JsonElement, T> parse) =>
                r.TryGetProperty(name, out var sec) && sec.ValueKind == JsonValueKind.Object &&
                sec.TryGetProperty("results", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray().Select(parse).ToList()
                    : new List<T>();

            YaSearchBest? best = null;
            if (r.TryGetProperty("best", out var b) && b.ValueKind == JsonValueKind.Object &&
                b.TryGetProperty("result", out var br) && br.ValueKind == JsonValueKind.Object)
            {
                var type = Str(b, "type");
                best = type switch
                {
                    "artist" => new YaSearchBest { Type = type, Artist = ParseArtist(br) },
                    "album" => new YaSearchBest { Type = type, Album = ParseAlbum(br) },
                    "playlist" => new YaSearchBest { Type = type, Playlist = ParsePlaylist(br) },
                    "track" => new YaSearchBest { Type = type, Track = ParseTrack(br) },
                    _ => null,
                };
            }

            var tracksTotal = r.TryGetProperty("tracks", out var tr) ? (int)Int(tr, "total") : 0;
            return new YaSearchResult
            {
                Best = best,
                Tracks = Section("tracks", ParseTrack),
                TracksTotal = tracksTotal,
                Artists = Section("artists", ParseArtist),
                Albums = Section("albums", ParseAlbum),
                Playlists = Section("playlists", ParsePlaylist),
            };
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

        public async Task<CacheSource> ResolveAudioAsync(YaTrack track, bool lowest, CancellationToken ct = default) =>
            new(await GetDirectUrlAsync(track.Id, lowest, ct), null);

        /// <summary>Прямая ссылка на mp3. lowest=true — самый экономный битрейт (для медленного интернета).</summary>
        public async Task<string> GetDirectUrlAsync(string trackId, CancellationToken ct = default) =>
            await GetDirectUrlAsync(trackId, lowest: false, ct);

        public async Task<string> GetDirectUrlAsync(string trackId, bool lowest, CancellationToken ct = default)
        {
            var r = await GetAsync($"/tracks/{trackId}/download-info", ct);
            if (r.ValueKind != JsonValueKind.Array)
                throw new YaApiException("Трек недоступен для воспроизведения");

            var ordered = r.EnumerateArray()
                .Where(i => Str(i, "codec") == "mp3" && Str(i, "downloadInfoUrl").Length > 0)
                .OrderBy(i => i.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.True ? 1 : 0);
            var best = (lowest ? ordered.ThenBy(i => Int(i, "bitrateInKbps")) : ordered.ThenByDescending(i => Int(i, "bitrateInKbps")))
                .Select(i => Str(i, "downloadInfoUrl"))
                .FirstOrDefault();
            if (best == null)
                throw new YaApiException("Нет mp3-версии этого трека");

            using var req = new HttpRequestMessage(HttpMethod.Get, best);
            using var resp = await SendAsync(req, ct);
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

        // ────────── новый API: get-file-info (lossless), тексты ──────────

        /// <summary>Ключ подписи запросов нового API (тот же, что у официальных клиентов и библиотеки yandex-music).</summary>
        private const string RequestSignKey = "p93jhgh689SBReK6ghtw62";

        internal static string HmacSign(string message)
        {
            var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(RequestSignKey), Encoding.UTF8.GetBytes(message));
            return Convert.ToBase64String(hash);
        }

        private static long UnixNow => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// Файл в нужном качестве: lq / nq / lossless. Для lossless отдаёт FLAC (иногда в MP4-контейнере),
        /// зашифрованный AES-CTR ключом Key (транспорт encraw) — расшифровка в <see cref="TrackCrypto"/>.
        /// </summary>
        public async Task<YaFileInfo> GetFileInfoAsync(string trackId, string quality, CancellationToken ct = default)
        {
            const string codecs = "flac,flac-mp4,aac,he-aac,aac-mp4,he-aac-mp4,mp3";
            const string transports = "encraw";
            var ts = UnixNow;
            // подпись — от склеенных значений параметров без запятых
            var sign = HmacSign($"{ts}{trackId}{quality}{codecs.Replace(",", "")}{transports}").TrimEnd('=');
            var query = $"ts={ts}&trackId={Uri.EscapeDataString(trackId)}&quality={quality}" +
                        $"&codecs={Uri.EscapeDataString(codecs)}&transports={transports}&sign={Uri.EscapeDataString(sign)}";
            var r = await GetAsync("/get-file-info?" + query, ct);

            var info = r.TryGetProperty("downloadInfo", out var d) ? d : r;
            var url = Str(info, "url");
            if (url.Length == 0 && info.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                url = urls.EnumerateArray().Select(u => u.GetString()).FirstOrDefault(u => !string.IsNullOrEmpty(u)) ?? "";
            if (url.Length == 0) throw new YaApiException("Сервер не вернул ссылку на файл");

            double? lufs = null;
            if (info.TryGetProperty("r128", out var r128) && r128.ValueKind == JsonValueKind.Object &&
                r128.TryGetProperty("i", out var i) && i.ValueKind == JsonValueKind.Number)
                lufs = i.GetDouble();

            var key = Str(info, "key");
            return new YaFileInfo(url, Str(info, "codec"), key.Length > 0 ? key : null, Str(info, "quality"), lufs);
        }

        /// <summary>Текст песни. sync=true — с таймкодами (LRC). null — текста нет.</summary>
        public async Task<string?> GetLyricsAsync(string trackId, bool sync, CancellationToken ct = default)
        {
            var id = trackId.Split(':')[0];
            var ts = UnixNow;
            var sign = HmacSign($"{id}{ts}");
            try
            {
                var r = await GetAsync($"/tracks/{id}/lyrics?format={(sync ? "LRC" : "TEXT")}&timeStamp={ts}&sign={Uri.EscapeDataString(sign)}", ct);
                var url = Str(r, "downloadUrl");
                if (url.Length == 0) return null;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Options.Set(YaHttpHandler.NoRefresh, true);
                using var resp = await SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                var text = await resp.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (YaApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return null;
            }
        }

        // ────────── история прослушиваний ──────────

        /// <summary>
        /// Сообщает Яндексу о прослушивании, чтобы рекомендации и «Моя волна» учитывали треки из Undertone.
        /// Не критично: ошибки только в журнал.
        /// </summary>
        public async Task ReportPlayAsync(YaTrack track, double playedSeconds, double endPositionSeconds,
            string playId, string? playlistId = null, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var form = new Dictionary<string, string>
            {
                ["track-id"] = track.Id,
                ["from-cache"] = "False",
                ["from"] = "desktop_win-undertone",
                ["play-id"] = playId,
                ["uid"] = Uid.ToString(CultureInfo.InvariantCulture),
                ["timestamp"] = now,
                ["track-length-seconds"] = Math.Round(track.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                ["total-played-seconds"] = Math.Round(playedSeconds, 1).ToString(CultureInfo.InvariantCulture),
                ["end-position-seconds"] = Math.Round(endPositionSeconds, 1).ToString(CultureInfo.InvariantCulture),
                ["album-id"] = track.AlbumId,
                ["client-now"] = now,
            };
            if (playlistId != null) form["playlist-id"] = playlistId;
            try { await PostFormAsync("/play-audio", form, ct); }
            catch (Exception ex) when (ex is YaApiException or HttpRequestException or TaskCanceledException)
            {
                Log.Warn($"play-audio {track.Id} не отправлен", ex);
            }
        }

        /// <summary>Похожие треки (полка под треком). Пусто — Яндекс ничего не подобрал.</summary>
        public async Task<List<YaTrack>> GetSimilarTracksAsync(string trackId, CancellationToken ct = default)
        {
            var r = await GetAsync($"/tracks/{trackId}/similar", ct);
            var result = new List<YaTrack>();
            if (r.TryGetProperty("similarTracks", out var list) && list.ValueKind == JsonValueKind.Array)
                result.AddRange(list.EnumerateArray().Select(ParseTrack).Where(t => t.Id.Length > 0));
            return result;
        }

        // ────────── артисты, альбомы ──────────

        public async Task<YaArtistPage> GetArtistAsync(string artistId, CancellationToken ct = default)
        {
            var brief = await GetAsync($"/artists/{artistId}/brief-info", ct);
            var tracksResult = await GetAsync($"/artists/{artistId}/tracks?page=0&page-size=100", ct);

            var page = new YaArtistPage
            {
                Artist = brief.TryGetProperty("artist", out var a) ? ParseArtist(a) : new YaArtist { Id = artistId },
            };
            if (tracksResult.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Array)
                page.Tracks.AddRange(tr.EnumerateArray().Select(ParseTrack));
            else if (brief.TryGetProperty("popularTracks", out var pop) && pop.ValueKind == JsonValueKind.Array)
                page.Tracks.AddRange(pop.EnumerateArray().Select(ParseTrack));

            if (brief.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array)
                page.Albums.AddRange(albums.EnumerateArray().Select(ParseAlbum));
            if (brief.TryGetProperty("alsoAlbums", out var also) && also.ValueKind == JsonValueKind.Array)
                page.Albums.AddRange(also.EnumerateArray().Select(ParseAlbum).Where(x => page.Albums.All(y => y.Id != x.Id)));
            return page;
        }

        public async Task<(YaAlbum Album, List<YaTrack> Tracks)> GetAlbumAsync(string albumId, CancellationToken ct = default)
        {
            var r = await GetAsync($"/albums/{albumId}/with-tracks", ct);
            var tracks = new List<YaTrack>();
            if (r.TryGetProperty("volumes", out var vols) && vols.ValueKind == JsonValueKind.Array)
                foreach (var vol in vols.EnumerateArray())
                    if (vol.ValueKind == JsonValueKind.Array)
                        tracks.AddRange(vol.EnumerateArray().Select(ParseTrack));
            return (ParseAlbum(r), tracks);
        }

        // ────────── плейлисты пользователя ──────────

        public async Task<List<YaPlaylist>> GetPlaylistsAsync(CancellationToken ct = default)
        {
            var r = await GetAsync($"/users/{Uid}/playlists/list", ct);
            return r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(ParsePlaylist).ToList() : new();
        }

        public Task<(YaPlaylist Playlist, List<YaTrack> Tracks)> GetPlaylistAsync(string kind, CancellationToken ct = default) =>
            GetPlaylistAsync(Uid, kind, ct);

        /// <summary>Плейлист любого пользователя (из поиска, подборки).</summary>
        public async Task<(YaPlaylist Playlist, List<YaTrack> Tracks)> GetPlaylistAsync(long ownerUid, string kind, CancellationToken ct = default)
        {
            var r = await GetAsync($"/users/{ownerUid}/playlists/{kind}", ct);
            var playlist = ParsePlaylist(r);

            // Каждая позиция плейлиста — либо полный трек, либо только id (ответ бывает смешанным).
            // Порядок сохраняем: догруженные треки встают на свои места, а не в конец и не вместо всех.
            var slots = new List<(YaTrack? Track, string Key)>();
            var missing = new List<string>();
            if (r.TryGetProperty("tracks", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object)
                        slots.Add((ParseTrack(t), ""));
                    else
                    {
                        var id = Str(item, "id");
                        if (id.Length == 0) continue;
                        var album = Str(item, "albumId");
                        var key = album.Length > 0 ? $"{id}:{album}" : id;
                        slots.Add((null, key));
                        missing.Add(key);
                    }
                }
            }

            var loaded = new Dictionary<string, YaTrack>();
            if (missing.Count > 0)
                foreach (var t in await GetTracksAsync(missing, ct))
                    loaded[t.Id] = t;

            var tracks = new List<YaTrack>(slots.Count);
            foreach (var (track, key) in slots)
            {
                if (track != null) tracks.Add(track);
                else if (loaded.TryGetValue(key.Split(':')[0], out var full)) tracks.Add(full);
                // трек, который сервер не отдал, просто не показываем — удаление идёт по id, не по позиции
            }
            return (playlist, tracks);
        }

        /// <summary>id треков плейлиста в серверном порядке — для сверки позиций перед изменением.</summary>
        private static List<string> RawTrackIds(JsonElement playlist)
        {
            var ids = new List<string>();
            if (playlist.TryGetProperty("tracks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                {
                    var id = Str(item, "id");
                    if (id.Length == 0 && item.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object)
                        id = Str(t, "id");
                    ids.Add(id);
                }
            return ids;
        }

        public async Task<YaPlaylist> CreatePlaylistAsync(string title, CancellationToken ct = default) =>
            ParsePlaylist(await PostFormAsync($"/users/{Uid}/playlists/create", new Dictionary<string, string>
            {
                ["title"] = title,
                ["visibility"] = "private",
            }, ct));

        public async Task<YaPlaylist> RenamePlaylistAsync(string kind, string title, CancellationToken ct = default) =>
            ParsePlaylist(await PostFormAsync($"/users/{Uid}/playlists/{kind}/name",
                new Dictionary<string, string> { ["value"] = title }, ct));

        public Task DeletePlaylistAsync(string kind, CancellationToken ct = default) =>
            PostFormAsync($"/users/{Uid}/playlists/{kind}/delete", new Dictionary<string, string>(), ct);

        /// <summary>Добавляет трек в начало плейлиста. Возвращает плейлист с новой ревизией.</summary>
        public Task<YaPlaylist> AddToPlaylistAsync(YaPlaylist playlist, YaTrack track, CancellationToken ct = default) =>
            ChangePlaylistAsync(playlist, new object[]
            {
                new Dictionary<string, object>
                {
                    ["op"] = "insert",
                    ["at"] = 0,
                    ["tracks"] = new[] { new Dictionary<string, string> { ["id"] = track.Id, ["albumId"] = track.AlbumId } },
                },
            }, ct);

        /// <summary>
        /// Удаляет трек из плейлиста. Позицию ищет по id в свежем плейлисте (с актуальной ревизией),
        /// поэтому не удалит соседний трек, даже если список на экране разошёлся с серверным.
        /// hintIndex — позиция на экране: если трек встречается несколько раз, берётся ближайшее вхождение.
        /// </summary>
        public async Task<YaPlaylist> RemoveTrackFromPlaylistAsync(string kind, string trackId, int hintIndex, CancellationToken ct = default)
        {
            var r = await GetAsync($"/users/{Uid}/playlists/{kind}", ct);
            var fresh = ParsePlaylist(r);
            var ids = RawTrackIds(r);

            int index = -1;
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] != trackId) continue;
                if (index < 0 || Math.Abs(i - hintIndex) < Math.Abs(index - hintIndex)) index = i;
            }
            if (index < 0) throw new YaApiException("трека уже нет в плейлисте");

            return await ChangePlaylistAsync(fresh, new object[]
            {
                new Dictionary<string, object> { ["op"] = "delete", ["from"] = index, ["to"] = index + 1 },
            }, ct);
        }

        private async Task<YaPlaylist> ChangePlaylistAsync(YaPlaylist playlist, object[] diff, CancellationToken ct)
        {
            var r = await PostFormAsync($"/users/{Uid}/playlists/{playlist.Kind}/change", new Dictionary<string, string>
            {
                ["kind"] = playlist.Kind,
                ["revision"] = playlist.Revision.ToString(CultureInfo.InvariantCulture),
                ["diff"] = JsonSerializer.Serialize(diff),
            }, ct);
            return ParsePlaylist(r);
        }

        // ────────── аватар ──────────

        /// <summary>Аватар из Яндекс ID. null — нет аватара или токен не даёт доступа.</summary>
        public async Task<string?> GetAvatarUrlAsync(CancellationToken ct = default)
        {
            try
            {
                // без автообновления токена: 401 тут значит «нет прав на аватар», а не протухший токен
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://login.yandex.ru/info?format=json");
                req.Options.Set(YaHttpHandler.NoRefresh, true);
                using var resp = await SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var r = doc.RootElement;
                if (r.TryGetProperty("is_avatar_empty", out var empty) && empty.ValueKind == JsonValueKind.True) return null;
                var id = Str(r, "default_avatar_id");
                return id.Length == 0 ? null : $"https://avatars.yandex.net/get-yapic/{id}/islands-200";
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or YaApiException)
            {
                return null;
            }
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

        private static YaArtistRef[] ParseArtistRefs(JsonElement e)
        {
            if (!e.TryGetProperty("artists", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<YaArtistRef>();
            return arr.EnumerateArray()
                .Select(a => new YaArtistRef(Str(a, "id"), Str(a, "name")))
                .Where(a => a.Name.Length > 0)
                .ToArray();
        }

        internal static YaArtist ParseArtist(JsonElement e)
        {
            string? cover = null;
            if (e.TryGetProperty("cover", out var c) && c.ValueKind == JsonValueKind.Object)
                cover = Str(c, "uri") is { Length: > 0 } u ? u : null;
            cover ??= Str(e, "ogImage") is { Length: > 0 } og ? og : null;
            string[] genres = e.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : Array.Empty<string>();
            return new YaArtist
            {
                Id = Str(e, "id"),
                Name = Str(e, "name"),
                CoverUri = cover,
                Genres = genres,
                LikesCount = Int(e, "likesCount"),
            };
        }

        internal static YaAlbum ParseAlbum(JsonElement e) => new()
        {
            Id = Str(e, "id"),
            Title = Str(e, "title"),
            Year = (int)Int(e, "year"),
            CoverUri = Str(e, "coverUri") is { Length: > 0 } c ? c : (Str(e, "ogImage") is { Length: > 0 } og ? og : null),
            TrackCount = (int)Int(e, "trackCount"),
            Type = Str(e, "type"),
            Artists = ParseArtistRefs(e),
        };

        internal static YaPlaylist ParsePlaylist(JsonElement e)
        {
            string? cover = null;
            if (e.TryGetProperty("cover", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                cover = Str(c, "uri") is { Length: > 0 } u ? u : null;
                if (cover == null && c.TryGetProperty("itemsUri", out var items) && items.ValueKind == JsonValueKind.Array)
                    cover = items.EnumerateArray().Select(i => i.GetString()).FirstOrDefault(i => !string.IsNullOrEmpty(i));
            }
            cover ??= Str(e, "ogImage") is { Length: > 0 } og ? og : null;
            var owner = Int(e, "uid");
            string ownerName = "";
            if (e.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object)
            {
                if (owner == 0) owner = Int(o, "uid");
                ownerName = FirstNonEmpty(Str(o, "name"), Str(o, "login"));
            }
            return new YaPlaylist
            {
                Kind = Str(e, "kind"),
                OwnerUid = owner,
                OwnerName = ownerName,
                Title = Str(e, "title"),
                TrackCount = (int)Int(e, "trackCount"),
                Revision = (int)Int(e, "revision"),
                CoverUri = cover,
                DurationMs = Int(e, "durationMs"),
            };
        }

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

            var refs = ParseArtistRefs(e);

            bool syncLyrics = false, lyrics = false;
            if (e.TryGetProperty("lyricsInfo", out var li) && li.ValueKind == JsonValueKind.Object)
            {
                syncLyrics = li.TryGetProperty("hasAvailableSyncLyrics", out var sl) && sl.ValueKind == JsonValueKind.True;
                lyrics = syncLyrics || (li.TryGetProperty("hasAvailableTextLyrics", out var tl) && tl.ValueKind == JsonValueKind.True);
            }

            return new YaTrack
            {
                Id = Str(e, "id"),
                AlbumId = albumId,
                HasSyncLyrics = syncLyrics,
                HasLyrics = lyrics,
                Title = Str(e, "title"),
                Version = Str(e, "version"),
                Artists = refs.Select(r => r.Name).ToArray(),
                ArtistRefs = refs,
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
