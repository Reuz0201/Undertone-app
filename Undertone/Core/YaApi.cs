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

        public string FullTitle => string.IsNullOrWhiteSpace(Version) ? Title : $"{Title} ({Version})";
        public string ArtistsText => Artists.Length == 0 ? "Неизвестный исполнитель" : string.Join(", ", Artists);
        /// <summary>Формат id:albumId — так API ждёт треки в лайках/дизлайках.</summary>
        public string LikeId => string.IsNullOrEmpty(AlbumId) ? Id : $"{Id}:{AlbumId}";
        public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

        public string? CoverUrl(int size) =>
            string.IsNullOrEmpty(CoverUri) ? null : "https://" + CoverUri.Replace("%%", $"{size}x{size}");

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
            string.IsNullOrEmpty(CoverUri) ? null : "https://" + CoverUri.Replace("%%", $"{size}x{size}");
    }

    public sealed class YaArtist
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? CoverUri { get; init; }
        public string[] Genres { get; init; } = Array.Empty<string>();
        public long LikesCount { get; init; }
        public string? CoverUrl(int size) =>
            string.IsNullOrEmpty(CoverUri) ? null : "https://" + CoverUri.Replace("%%", $"{size}x{size}");
    }

    public sealed class YaArtistPage
    {
        public YaArtist Artist { get; init; } = new();
        public List<YaTrack> Tracks { get; init; } = new();
        public List<YaAlbum> Albums { get; init; } = new();
    }

    public sealed class YaPlaylist
    {
        public string Kind { get; init; } = "";
        public long OwnerUid { get; init; }
        public string Title { get; init; } = "";
        public int TrackCount { get; init; }
        public int Revision { get; init; }
        public string? CoverUri { get; init; }
        public long DurationMs { get; init; }
        public string? CoverUrl(int size) =>
            string.IsNullOrEmpty(CoverUri) ? null : "https://" + CoverUri.Replace("%%", $"{size}x{size}");
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

        public async Task<(YaPlaylist Playlist, List<YaTrack> Tracks)> GetPlaylistAsync(string kind, CancellationToken ct = default)
        {
            var r = await GetAsync($"/users/{Uid}/playlists/{kind}", ct);
            var playlist = ParsePlaylist(r);
            var tracks = new List<YaTrack>();
            var missing = new List<string>();
            if (r.TryGetProperty("tracks", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object)
                        tracks.Add(ParseTrack(t));
                    else
                    {
                        var id = Str(item, "id");
                        var album = Str(item, "albumId");
                        if (id.Length > 0) missing.Add(album.Length > 0 ? $"{id}:{album}" : id);
                    }
                }
            }
            // некоторые ответы содержат только id — догружаем треки
            if (missing.Count > 0 && tracks.Count == 0)
                tracks = await GetTracksAsync(missing, ct);
            return (playlist, tracks);
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

        /// <summary>Удаляет трек по позиции в плейлисте.</summary>
        public Task<YaPlaylist> RemoveFromPlaylistAsync(YaPlaylist playlist, int index, CancellationToken ct = default) =>
            ChangePlaylistAsync(playlist, new object[]
            {
                new Dictionary<string, object> { ["op"] = "delete", ["from"] = index, ["to"] = index + 1 },
            }, ct);

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
                using var resp = await SendOnce(() =>
                    new HttpRequestMessage(HttpMethod.Get, "https://login.yandex.ru/info?format=json"), _token, ct);
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
            return new YaPlaylist
            {
                Kind = Str(e, "kind"),
                OwnerUid = Int(e, "uid"),
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

            return new YaTrack
            {
                Id = Str(e, "id"),
                AlbumId = albumId,
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
