using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>Устройство Spotify Connect: приложение Spotify на ПК/телефоне, колонка — или сам Undertone (Web Playback SDK).</summary>
    public sealed record SpotifyDevice(string Id, string Name, string Type, bool IsActive, bool IsRestricted);

    /// <summary>Что сейчас играет у Spotify (GET /me/player).</summary>
    public sealed record SpotifyPlayerState(string? DeviceId, string? TrackId, bool IsPlaying, long ProgressMs, long DurationMs);

    /// <summary>
    /// Клиент api.spotify.com (Web API) с учётом ограничений Development Mode (февраль 2026):
    /// треки, альбомы и артисты — только по одному, поиск — до 10 результатов, лайки — через /me/library,
    /// у плейлистов «items» вместо «tracks» (и они видны только у своих и совместных плейлистов),
    /// топ-треков артиста и рекомендаций нет.
    /// Звук Spotify не отдаёт: играет Web Playback SDK (см. SpotifyPlayer) или приложение Spotify через Connect —
    /// для этого здесь команды /me/player.
    /// Внутри приложения id — sp-&lt;base62&gt; (безопасно для имён файлов), в API уходят id и URI spotify:track:….
    /// </summary>
    public sealed class SpotifyApi : IMusicService
    {
        public const string IdPrefix = "sp-";

        private const string Base = "https://api.spotify.com/v1";
        private const int MaxLikes = 5000;
        private const int MaxPlaylistTracks = 3000;

        private readonly HttpClient _http;
        private readonly YaHttpHandler? _auth;
        private readonly ConcurrentDictionary<string, YaTrack> _known = new();
        private string? _avatar;

        public SpotifyApi(string token, Func<string, CancellationToken, Task<string?>>? refresh = null)
            : this(new YaHttpHandler(token, refresh, new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }, "Spotify", "Bearer"))
        {
        }

        /// <summary>Для тестов: любой обработчик вместо настоящей сети.</summary>
        internal SpotifyApi(HttpMessageHandler handler)
        {
            _auth = handler as YaHttpHandler;
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(70) };
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Undertone/" + Updater.AppVersion.ToString(3));
        }

        public MusicServiceKind Kind => MusicServiceKind.Spotify;
        public bool SupportsWave => false;
        public bool SupportsDislike => false;
        public bool SupportsLossless => false;
        public bool SupportsLyrics => false;
        public bool SupportsAlbums => true;
        public bool SupportsDownload => false;

        public long Uid { get; private set; }

        /// <summary>id пользователя Spotify (строка) — владелец «своих» плейлистов.</summary>
        public string UserId { get; private set; } = "";

        // ────────── id ──────────

        public static bool IsSpId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal);

        /// <summary>spotify:track:abc / abc → sp-abc.</summary>
        internal static string ToId(string uriOrId)
        {
            if (uriOrId.Length == 0 || IsSpId(uriOrId)) return uriOrId;
            var colon = uriOrId.LastIndexOf(':');
            return IdPrefix + (colon >= 0 ? uriOrId[(colon + 1)..] : uriOrId);
        }

        /// <summary>sp-abc → abc (для путей API). LikeId вида sp-abc:sp-album тоже понимает.</summary>
        internal static string Raw(string id)
        {
            var bare = id.Split(':')[0];
            return IsSpId(bare) ? bare[IdPrefix.Length..] : bare;
        }

        /// <summary>sp-abc → spotify:{type}:abc.</summary>
        public static string ToUri(string id, string type = "track") =>
            id.StartsWith("spotify:", StringComparison.Ordinal) ? id : $"spotify:{type}:{Raw(id)}";

        /// <summary>
        /// Строковый id пользователя → число для локальных файлов (кэш, история, статистика).
        /// Отрицательный и больше 10^15 по модулю: не пересекается ни с Яндексом (положительные), ни с SoundCloud.
        /// </summary>
        internal static long UidFor(string userId)
        {
            ulong hash = 14695981039346656037;
            foreach (var b in Encoding.UTF8.GetBytes(userId))
            {
                hash ^= b;
                hash *= 1099511628211;
            }
            return -(1_000_000_000_000_000L + (long)(hash % 1_000_000_000_000_000UL));
        }

        // ────────── транспорт ──────────

        private static string Url(string pathOrUrl) =>
            pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : Base + pathOrUrl;

        private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? jsonBody, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(method, Url(path))
            {
                Content = jsonBody == null ? null
                    : new StringContent(JsonSerializer.Serialize(jsonBody), Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            JsonElement root = default;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    if (resp.IsSuccessStatusCode) return default; // snapshot_id строкой и т.п.
                    Log.Warn($"Spotify {method} {req.RequestUri?.AbsolutePath}: HTTP {(int)resp.StatusCode}, не JSON");
                    throw new YaApiException($"HTTP {(int)resp.StatusCode}: неожиданный ответ Spotify", resp.StatusCode);
                }
            }

            if (!resp.IsSuccessStatusCode)
            {
                var message = Friendly(ErrorMessage(root), resp.StatusCode);
                Log.Warn($"Spotify {method} {req.RequestUri?.AbsolutePath}: HTTP {(int)resp.StatusCode} {message}");
                throw new YaApiException(message, resp.StatusCode);
            }
            return root;
        }

        internal static string? ErrorMessage(JsonElement r)
        {
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (r.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object && Str(e, "message") is { Length: > 0 } m) return m;
                if (e.ValueKind == JsonValueKind.String) return Str(r, "error_description") is { Length: > 0 } d ? d : e.GetString();
            }
            return Str(r, "message") is { Length: > 0 } msg ? msg : null;
        }

        /// <summary>Самые частые ответы Spotify — по-русски и с подсказкой, что делать.</summary>
        internal static string Friendly(string? message, HttpStatusCode status)
        {
            var m = message ?? "";
            if (m.Contains("Premium", StringComparison.OrdinalIgnoreCase))
                return "нужен Spotify Premium — без него сторонние плееры Spotify не играют";
            if (status == HttpStatusCode.Forbidden && m.Contains("not registered", StringComparison.OrdinalIgnoreCase))
                return "этот аккаунт не добавлен в приложение Spotify — добавь его в Dashboard → User Management";
            if (status == HttpStatusCode.NotFound && m.Contains("device", StringComparison.OrdinalIgnoreCase))
                return "устройство Spotify не найдено — открой приложение Spotify или включи плеер заново";
            if (status == HttpStatusCode.TooManyRequests) return "Spotify просит подождать — слишком много запросов";
            return m.Length > 0 ? m : $"HTTP {(int)status}";
        }

        private Task<JsonElement> GetAsync(string path, CancellationToken ct) => RequestAsync(HttpMethod.Get, path, null, ct);

        /// <summary>Все элементы постраничного ответа (items + next), не больше max.</summary>
        private async Task<List<JsonElement>> GetPagedAsync(string path, int max, CancellationToken ct, string? wrapper = null)
        {
            var items = new List<JsonElement>();
            string? next = path;
            for (var pages = 0; next != null && items.Count < max && pages < 200; pages++)
            {
                var r = await GetAsync(next, ct);
                if (wrapper != null && r.ValueKind == JsonValueKind.Object && r.TryGetProperty(wrapper, out var w)) r = w;
                var (page, nextUrl) = ParsePage(r);
                items.AddRange(page);
                next = nextUrl;
            }
            return items.Count > max ? items.GetRange(0, max) : items;
        }

        internal static (List<JsonElement> Items, string? Next) ParsePage(JsonElement r)
        {
            var items = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array
                ? i.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList()
                : new List<JsonElement>();
            var next = Str(r, "next");
            return (items, next.Length > 0 ? next : null);
        }

        private static async Task<JsonElement?> SafeAsync(Func<Task<JsonElement>> request)
        {
            try { return await request(); }
            catch (Exception ex) when (ex is YaApiException or HttpRequestException or TaskCanceledException)
            {
                return null;
            }
        }

        private List<YaTrack> Remember(IEnumerable<JsonElement> tracks, JsonElement album = default)
        {
            var list = new List<YaTrack>();
            foreach (var e in tracks)
            {
                var t = ParseTrack(e, album);
                if (t.Id.Length == 0) continue;
                _known[t.Id] = t;
                list.Add(t);
            }
            return list;
        }

        /// <summary>Свежий токен для Web Playback SDK: лёгкий запрос /me — если токен истёк, транспорт его обновит.</summary>
        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            await GetAsync("/me", ct);
            return _auth?.Token ?? throw new InvalidOperationException("нет токена Spotify");
        }

        // ────────── аккаунт ──────────

        public async Task<YaAccount> GetAccountAsync(CancellationToken ct = default)
        {
            var me = await GetAsync("/me", ct);
            UserId = Str(me, "id");
            if (UserId.Length == 0)
                throw new YaApiException("Вход в Spotify истёк — нужно войти заново.", HttpStatusCode.Unauthorized);
            Uid = UidFor(UserId);
            _avatar = PickImage(me, 200);
            var name = Str(me, "display_name");
            return new YaAccount
            {
                Uid = Uid,
                Login = UserId,
                DisplayName = name.Length > 0 ? name : UserId,
                // product (premium/free) в Development Mode больше не отдают — проверит сам плеер
                HasPlus = true,
                AvatarUrl = _avatar,
            };
        }

        public Task<string?> GetAvatarUrlAsync(CancellationToken ct = default) => Task.FromResult(_avatar);

        // ────────── треки ──────────

        /// <summary>«Любимые треки»: /me/tracks отдаёт сразу полные треки — запоминаем их, наружу id.</summary>
        public async Task<(int Revision, List<string> Ids)> GetLikedIdsAsync(CancellationToken ct = default)
        {
            var items = await GetPagedAsync("/me/tracks?limit=50", MaxLikes, ct);
            return (0, Remember(items.Select(UnwrapItem).Where(t => t.ValueKind == JsonValueKind.Object)).Select(t => t.Id).ToList());
        }

        /// <summary>Треки по id в том же порядке. Пачкой Spotify больше не отдаёт — неизвестные запрашиваем по одному.</summary>
        public async Task<List<YaTrack>> GetTracksAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        {
            var mine = ids.Select(i => i.Split(':')[0]).Where(IsSpId).ToList();
            var missing = mine.Where(id => !_known.ContainsKey(id)).Distinct().ToList();

            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(missing.Select(async id =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var t = ParseTrack(await GetAsync($"/tracks/{Raw(id)}", ct));
                    if (t.Id.Length > 0) _known[t.Id] = t;
                }
                catch (YaApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                {
                    // трек убрали из каталога
                }
                finally
                {
                    gate.Release();
                }
            }));
            return mine.Select(id => _known.TryGetValue(id, out var t) ? t : null).Where(t => t != null).Select(t => t!).ToList();
        }

        public async Task<YaSearchResult> SearchAsync(string query, CancellationToken ct = default)
        {
            var r = await GetAsync($"/search?q={Uri.EscapeDataString(query)}&type=track,artist,album,playlist&limit=10", ct);
            var tracks = Remember(Items(r, "tracks"));
            return new YaSearchResult
            {
                Tracks = tracks,
                TracksTotal = (int)Int(Obj(r, "tracks"), "total"),
                Artists = Items(r, "artists").Select(ParseArtist).Where(a => a.Id.Length > 0).ToList(),
                Albums = Items(r, "albums").Select(ParseAlbum).Where(a => a.Id.Length > 0).ToList(),
                Playlists = Items(r, "playlists").Select(ParsePlaylist).Where(p => p.Kind.Length > 0).ToList(),
            };
        }

        /// <summary>Ещё треки по запросу: Spotify в Development Mode отдаёт по 10 за раз.</summary>
        public async Task<List<YaTrack>> SearchTracksAsync(string query, int page = 0, CancellationToken ct = default)
        {
            var r = await GetAsync($"/search?q={Uri.EscapeDataString(query)}&type=track&limit=10&offset={page * 10}", ct);
            return Remember(Items(r, "tracks"));
        }

        public Task SetLikeAsync(YaTrack track, bool like, CancellationToken ct = default) =>
            RequestAsync(like ? HttpMethod.Put : HttpMethod.Delete,
                $"/me/library?uris={Uri.EscapeDataString(ToUri(track.Id))}", null, ct);

        public Task DislikeAsync(YaTrack track, CancellationToken ct = default) =>
            throw new NotSupportedException("В Spotify нет «Не нравится»");

        // ────────── звук: только своим плеером ──────────

        public Task<CacheSource> ResolveAudioAsync(YaTrack track, bool lowest, CancellationToken ct = default) =>
            throw new NotSupportedException("Spotify не отдаёт файлы — трек играет плеер Spotify");

        public Task<YaFileInfo> GetFileInfoAsync(string trackId, string quality, CancellationToken ct = default) =>
            throw new NotSupportedException("Spotify не отдаёт файлы");

        public Task<string?> GetLyricsAsync(string trackId, bool sync, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        /// <summary>Прослушивания Spotify и так видит сам — его плеер играет.</summary>
        public Task ReportPlayAsync(YaTrack track, double playedSeconds, double endPositionSeconds,
            string playId, string? playlistId = null, CancellationToken ct = default) => Task.CompletedTask;

        /// <summary>Рекомендации закрыты для новых приложений — «похожих» нет.</summary>
        public Task<List<YaTrack>> GetSimilarTracksAsync(string trackId, CancellationToken ct = default) =>
            Task.FromResult(new List<YaTrack>());

        // ────────── артисты и альбомы ──────────

        /// <summary>Топ-треков больше нет — вместо них поиск «artist:Имя» (только треки этого артиста).</summary>
        public async Task<YaArtistPage> GetArtistAsync(string artistId, CancellationToken ct = default)
        {
            var raw = Raw(artistId);
            var artist = ParseArtist(await GetAsync($"/artists/{raw}", ct));
            var albumsTask = SafeAsync(() => GetAsync($"/artists/{raw}/albums?include_groups=album,single&limit=20", ct));
            var q = Uri.EscapeDataString($"artist:\"{artist.Name}\"");
            var tracks = new List<YaTrack>();
            for (var page = 0; page < 3; page++)
            {
                var r = await SafeAsync(() => GetAsync($"/search?q={q}&type=track&limit=10&offset={page * 10}", ct));
                if (r is not { } found) break;
                var batch = Remember(Items(found, "tracks"));
                tracks.AddRange(batch.Where(t => t.ArtistRefs.Any(a => a.Id == artist.Id) && tracks.All(x => x.Id != t.Id)).ToList());
                if (batch.Count < 10) break;
            }
            var page0 = new YaArtistPage { Artist = artist };
            page0.Tracks.AddRange(tracks);
            if (await albumsTask is { } albums)
                page0.Albums.AddRange(ParsePage(albums).Items.Select(ParseAlbum).Where(a => a.Id.Length > 0));
            return page0;
        }

        public async Task<(YaAlbum Album, List<YaTrack> Tracks)> GetAlbumAsync(string albumId, CancellationToken ct = default)
        {
            var raw = Raw(albumId);
            var a = await GetAsync($"/albums/{raw}", ct);
            var album = ParseAlbum(a);
            var first = Obj(a, "tracks");
            var items = ParsePage(first).Items;
            if (Str(first, "next") is { Length: > 0 } next)
                items.AddRange(await GetPagedAsync(next, 500, ct));
            return (album, Remember(items, a));
        }

        // ────────── плейлисты ──────────

        public async Task<List<YaPlaylist>> GetPlaylistsAsync(CancellationToken ct = default)
        {
            var items = await GetPagedAsync("/me/playlists?limit=50", 1000, ct);
            return items.Select(ParsePlaylist).Where(p => p.Kind.Length > 0).ToList();
        }

        /// <summary>Треки видны только у своих и совместных плейлистов — у чужих Spotify их больше не показывает.</summary>
        public async Task<(YaPlaylist Playlist, List<YaTrack> Tracks)> GetPlaylistAsync(string kind, CancellationToken ct = default)
        {
            var raw = Raw(kind);
            var meta = ParsePlaylist(await GetAsync($"/playlists/{raw}?fields=id,name,owner(id,display_name),images,items(total),tracks(total)", ct));
            List<JsonElement> items;
            try
            {
                items = await GetPagedAsync($"/playlists/{raw}/items?limit=50", MaxPlaylistTracks, ct);
            }
            catch (YaApiException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                throw new YaApiException("Spotify показывает треки только своих и совместных плейлистов. Этот можно открыть в приложении Spotify.", ex.Status);
            }
            var tracks = Remember(items.Select(UnwrapItem).Where(t => t.ValueKind == JsonValueKind.Object));
            return (meta.Kind.Length > 0 ? meta : Copy(meta, kind: ToId(raw)), tracks);
        }

        public Task<(YaPlaylist Playlist, List<YaTrack> Tracks)> GetPlaylistAsync(long ownerUid, string kind, CancellationToken ct = default) =>
            GetPlaylistAsync(kind, ct);

        public async Task<YaPlaylist> CreatePlaylistAsync(string title, CancellationToken ct = default) =>
            ParsePlaylist(await RequestAsync(HttpMethod.Post, "/me/playlists",
                new Dictionary<string, object> { ["name"] = title, ["public"] = false }, ct));

        public async Task<YaPlaylist> RenamePlaylistAsync(string kind, string title, CancellationToken ct = default)
        {
            await RequestAsync(HttpMethod.Put, $"/playlists/{Raw(kind)}", new Dictionary<string, object> { ["name"] = title }, ct);
            return await PlaylistMetaAsync(kind, ct);
        }

        /// <summary>Удалить плейлист Spotify нельзя — только отписаться (у своего это и есть «удалить» из библиотеки).</summary>
        public Task DeletePlaylistAsync(string kind, CancellationToken ct = default) =>
            RequestAsync(HttpMethod.Delete, $"/me/library?uris={Uri.EscapeDataString(ToUri(kind, "playlist"))}", null, ct);

        public async Task<YaPlaylist> AddToPlaylistAsync(YaPlaylist playlist, YaTrack track, CancellationToken ct = default)
        {
            await RequestAsync(HttpMethod.Post, $"/playlists/{Raw(playlist.Kind)}/items", new Dictionary<string, object>
            {
                ["uris"] = new[] { ToUri(track.Id) },
                ["position"] = 0,
            }, ct);
            return await PlaylistMetaAsync(playlist.Kind, ct);
        }

        /// <summary>Spotify убирает все вхождения трека (по позиции удалять больше нельзя).</summary>
        public async Task<YaPlaylist> RemoveTrackFromPlaylistAsync(string kind, string trackId, int hintIndex, CancellationToken ct = default)
        {
            await RequestAsync(HttpMethod.Delete, $"/playlists/{Raw(kind)}/items", new Dictionary<string, object>
            {
                ["items"] = new[] { new Dictionary<string, string> { ["uri"] = ToUri(trackId) } },
            }, ct);
            return await PlaylistMetaAsync(kind, ct);
        }

        private async Task<YaPlaylist> PlaylistMetaAsync(string kind, CancellationToken ct)
        {
            var meta = ParsePlaylist(await GetAsync($"/playlists/{Raw(kind)}?fields=id,name,owner(id,display_name),images,items(total),tracks(total)", ct));
            return meta.Kind.Length > 0 ? meta : Copy(meta, kind: kind);
        }

        private static YaPlaylist Copy(YaPlaylist p, string? kind = null) => new()
        {
            Kind = kind ?? p.Kind,
            OwnerUid = p.OwnerUid,
            OwnerName = p.OwnerName,
            Title = p.Title,
            TrackCount = p.TrackCount,
            CoverUri = p.CoverUri,
            DurationMs = p.DurationMs,
        };

        // ────────── волны нет ──────────

        public Task<WaveBatch> StartWaveAsync(IEnumerable<string> seeds, CancellationToken ct = default) =>
            throw new NotSupportedException("В Spotify нет Моей волны");

        public Task<WaveBatch> GetWaveTracksAsync(string sessionId, IEnumerable<string> queue, CancellationToken ct = default) =>
            throw new NotSupportedException("В Spotify нет Моей волны");

        public Task WaveFeedbackAsync(string sessionId, string? batchId, string type, string? trackId = null,
            double? playedSeconds = null, CancellationToken ct = default) => Task.CompletedTask;

        // ────────── плеер: Spotify Connect ──────────

        public async Task<List<SpotifyDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            var r = await GetAsync("/me/player/devices", ct);
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("devices", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Select(ParseDevice).Where(x => x.Id.Length > 0).ToList()
                : new List<SpotifyDevice>();
        }

        /// <summary>null — сейчас нигде ничего не играет (204).</summary>
        public async Task<SpotifyPlayerState?> GetPlayerStateAsync(CancellationToken ct = default) =>
            ParsePlayerState(await GetAsync("/me/player", ct));

        public Task PlayAsync(string deviceId, string trackId, long positionMs, CancellationToken ct = default) =>
            RequestAsync(HttpMethod.Put, $"/me/player/play?device_id={Uri.EscapeDataString(deviceId)}", new Dictionary<string, object>
            {
                ["uris"] = new[] { ToUri(trackId) },
                ["position_ms"] = Math.Max(0, positionMs),
            }, ct);

        public Task ResumeAsync(string deviceId, CancellationToken ct = default) =>
            RequestAsync(HttpMethod.Put, $"/me/player/play?device_id={Uri.EscapeDataString(deviceId)}", null, ct);

        public Task PauseAsync(string deviceId, CancellationToken ct = default) =>
            RequestAsync(HttpMethod.Put, $"/me/player/pause?device_id={Uri.EscapeDataString(deviceId)}", null, ct);

        public Task SeekAsync(string deviceId, long positionMs, CancellationToken ct = default) =>
            RequestAsync(HttpMethod.Put, $"/me/player/seek?position_ms={Math.Max(0, positionMs)}&device_id={Uri.EscapeDataString(deviceId)}", null, ct);

        public Task SetVolumeAsync(string deviceId, int percent, CancellationToken ct = default) =>
            RequestAsync(HttpMethod.Put, $"/me/player/volume?volume_percent={Math.Clamp(percent, 0, 100)}&device_id={Uri.EscapeDataString(deviceId)}", null, ct);

        /// <summary>Сделать устройство активным (без запуска музыки) — иначе первая команда может уйти «в никуда».</summary>
        public Task TransferAsync(string deviceId, bool play, CancellationToken ct = default) =>
            RequestAsync(HttpMethod.Put, "/me/player", new Dictionary<string, object>
            {
                ["device_ids"] = new[] { deviceId },
                ["play"] = play,
            }, ct);

        // ────────── парсинг ──────────

        /// <summary>Элемент лайков/плейлиста: {"track": {…}} или (после переименования) {"item": {…}}.</summary>
        internal static JsonElement UnwrapItem(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return default;
            foreach (var key in new[] { "item", "track" })
                if (e.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.Object)
                    return Str(t, "type") is "" or "track" ? t : default; // подкасты в плейлистах пропускаем
            return Str(e, "type") == "track" ? e : default;
        }

        /// <summary>album — для треков из /albums/{id}: у них нет своего поля album.</summary>
        internal static YaTrack ParseTrack(JsonElement e, JsonElement album = default)
        {
            if (e.ValueKind != JsonValueKind.Object) return new YaTrack();
            var al = Obj(e, "album") is { ValueKind: JsonValueKind.Object } own ? own : album;
            var artists = e.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array
                ? ar.EnumerateArray().Select(a => new YaArtistRef(ToId(Str(a, "id")), Str(a, "name"))).Where(a => a.Name.Length > 0).ToArray()
                : Array.Empty<YaArtistRef>();
            var playable = !(e.TryGetProperty("is_playable", out var p) && p.ValueKind == JsonValueKind.False) &&
                           !(e.TryGetProperty("is_local", out var l) && l.ValueKind == JsonValueKind.True);
            var id = Str(e, "id");
            return new YaTrack
            {
                Id = id.Length > 0 ? ToId(id) : "",
                AlbumId = Str(al, "id") is { Length: > 0 } aid ? ToId(aid) : "",
                Title = Str(e, "name"),
                Artists = artists.Select(a => a.Name).ToArray(),
                ArtistRefs = artists.Where(a => a.Id.Length > IdPrefix.Length).ToArray(),
                Album = Str(al, "name"),
                DurationMs = Int(e, "duration_ms"),
                CoverUri = PickImage(al, 300),
                Available = playable && id.Length > 0,
                Permalink = Str(Obj(e, "external_urls"), "spotify") is { Length: > 0 } link ? link : null,
            };
        }

        internal static YaAlbum ParseAlbum(JsonElement e)
        {
            var date = Str(e, "release_date");
            return new YaAlbum
            {
                Id = Str(e, "id") is { Length: > 0 } id ? ToId(id) : "",
                Title = Str(e, "name"),
                Year = date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : 0,
                CoverUri = PickImage(e, 300),
                TrackCount = (int)Int(e, "total_tracks"),
                Type = Str(e, "album_type"),
                Artists = e.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array
                    ? ar.EnumerateArray().Select(a => new YaArtistRef(ToId(Str(a, "id")), Str(a, "name"))).ToArray()
                    : Array.Empty<YaArtistRef>(),
            };
        }

        internal static YaArtist ParseArtist(JsonElement e) => new()
        {
            Id = Str(e, "id") is { Length: > 0 } id ? ToId(id) : "",
            Name = Str(e, "name"),
            CoverUri = PickImage(e, 300),
            Genres = e.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(3).ToArray()
                : Array.Empty<string>(),
        };

        internal static YaPlaylist ParsePlaylist(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return new YaPlaylist();
            var owner = Obj(e, "owner");
            var ownerId = Str(owner, "id");
            // после переименования счётчик в items.total, раньше — в tracks.total
            var total = Int(Obj(e, "items"), "total");
            if (total == 0) total = Int(Obj(e, "tracks"), "total");
            return new YaPlaylist
            {
                Kind = Str(e, "id") is { Length: > 0 } id ? ToId(id) : "",
                OwnerUid = ownerId.Length > 0 ? UidFor(ownerId) : 0,
                OwnerName = Str(owner, "display_name") is { Length: > 0 } n ? n : ownerId,
                Title = Str(e, "name"),
                TrackCount = (int)total,
                CoverUri = PickImage(e, 300),
            };
        }

        internal static SpotifyDevice ParseDevice(JsonElement e) => new(
            Str(e, "id"), Str(e, "name"), Str(e, "type"),
            e.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True,
            e.TryGetProperty("is_restricted", out var r) && r.ValueKind == JsonValueKind.True);

        internal static SpotifyPlayerState? ParsePlayerState(JsonElement r)
        {
            if (r.ValueKind != JsonValueKind.Object) return null;
            var item = Obj(r, "item");
            return new SpotifyPlayerState(
                Str(Obj(r, "device"), "id") is { Length: > 0 } d ? d : null,
                Str(item, "id") is { Length: > 0 } t ? ToId(t) : null,
                r.TryGetProperty("is_playing", out var p) && p.ValueKind == JsonValueKind.True,
                Int(r, "progress_ms"),
                Int(item, "duration_ms"));
        }

        /// <summary>Картинки идут от большой к маленькой — берём самую маленькую, что не меньше нужного размера.</summary>
        internal static string? PickImage(JsonElement e, int size)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array)
                return null;
            var list = imgs.EnumerateArray()
                .Select(i => (Url: Str(i, "url"), Width: Int(i, "width")))
                .Where(i => i.Url.Length > 0).ToList();
            if (list.Count == 0) return null;
            return list.Where(i => i.Width == 0 || i.Width >= size).OrderBy(i => i.Width == 0 ? long.MaxValue : i.Width)
                       .Select(i => i.Url).FirstOrDefault() ?? list[0].Url;
        }

        private static IEnumerable<JsonElement> Items(JsonElement r, string section) =>
            ParsePage(Obj(r, section)).Items;

        private static JsonElement Obj(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

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
            return 0;
        }

        public void Dispose() => _http.Dispose();
    }
}
