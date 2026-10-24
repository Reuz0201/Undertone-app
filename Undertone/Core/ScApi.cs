using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Клиент api.soundcloud.com (OAuth 2.1). Внутри приложения id треков, плейлистов и пользователей — sc-123
    /// (безопасно для имён файлов в кэше и не пересекается с Яндексом), в API уходят URN вида soundcloud:tracks:123.
    /// «Артист» — загрузивший трек пользователь; альбомов, волны, дизлайков, lossless и текстов нет.
    /// Полные треки отдаются только как HLS — сегменты склеивает <see cref="AudioCache"/>.
    /// </summary>
    public sealed class ScApi : IMusicService
    {
        public const string IdPrefix = "sc-";

        private const string Base = "https://api.soundcloud.com";
        private const int PageSize = 200;
        private const int MaxLikes = 5000;
        private const int MaxPlaylistTracks = 5000;

        private readonly HttpClient _http;
        // плейлисты HLS на CDN уже подписаны — токен туда не отправляем
        private readonly HttpClient _plain = new() { Timeout = TimeSpan.FromSeconds(25) };
        /// <summary>Все треки, что уже приходили с сервера: лайки и плейлисты не надо перезапрашивать по одному.</summary>
        private readonly ConcurrentDictionary<string, YaTrack> _known = new();
        private string? _avatar;

        public ScApi(string token, Func<string, CancellationToken, Task<string?>>? refresh = null)
            : this(new YaHttpHandler(token, refresh, new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }, "SoundCloud"))
        {
        }

        /// <summary>Для тестов: любой обработчик вместо настоящей сети.</summary>
        internal ScApi(HttpMessageHandler handler)
        {
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(70) };
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json; charset=utf-8");
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Undertone/" + Updater.AppVersion.ToString(3));
        }

        public MusicServiceKind Kind => MusicServiceKind.SoundCloud;
        public bool SupportsWave => false;
        public bool SupportsDislike => false;
        public bool SupportsLossless => false;
        public bool SupportsLyrics => false;
        public bool SupportsAlbums => false;

        /// <summary>Отрицательный: кэш библиотеки, история и сессия не пересекаются с аккаунтом Яндекса.</summary>
        public long Uid { get; private set; }

        // ────────── id ──────────

        public static bool IsScId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal);

        /// <summary>soundcloud:tracks:123 / 123 → sc-123.</summary>
        internal static string ToId(string urnOrId)
        {
            if (urnOrId.Length == 0 || IsScId(urnOrId)) return urnOrId;
            var colon = urnOrId.LastIndexOf(':');
            return IdPrefix + (colon >= 0 ? urnOrId[(colon + 1)..] : urnOrId);
        }

        /// <summary>sc-123 → soundcloud:{kind}:123 (kind: tracks, playlists, users).</summary>
        internal static string ToUrn(string id, string kind) =>
            id.StartsWith("soundcloud:", StringComparison.Ordinal) ? id
            : $"soundcloud:{kind}:{(IsScId(id) ? id[IdPrefix.Length..] : id)}";

        /// <summary>id трека может прийти и как LikeId (sc-123:…), и как готовый URN.</summary>
        internal static string TrackUrn(string id) =>
            id.StartsWith("soundcloud:", StringComparison.Ordinal) ? id : ToUrn(id.Split(':')[0], "tracks");

        // ────────── транспорт (токен и повторы — в YaHttpHandler) ──────────

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
                    if (resp.IsSuccessStatusCode) return default;
                    Log.Warn($"SoundCloud {method} {req.RequestUri?.AbsolutePath}: HTTP {(int)resp.StatusCode}, не JSON");
                    throw new YaApiException($"HTTP {(int)resp.StatusCode}: неожиданный ответ SoundCloud", resp.StatusCode);
                }
            }

            if (!resp.IsSuccessStatusCode)
            {
                var message = ErrorMessage(root) ?? $"HTTP {(int)resp.StatusCode}";
                Log.Warn($"SoundCloud {method} {req.RequestUri?.AbsolutePath}: {message}");
                throw new YaApiException(message, resp.StatusCode);
            }
            return root;
        }

        internal static string? ErrorMessage(JsonElement r)
        {
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (Str(r, "message") is { Length: > 0 } m) return m;
            if (r.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                foreach (var e in errs.EnumerateArray())
                    if (Str(e, "error_message") is { Length: > 0 } em) return em;
            if (Str(r, "error") is { Length: > 0 } err) return err;
            return null;
        }

        private Task<JsonElement> GetAsync(string path, CancellationToken ct) => RequestAsync(HttpMethod.Get, path, null, ct);

        /// <summary>Все элементы постраничного списка (linked_partitioning: collection + next_href), не больше max.</summary>
        private async Task<List<JsonElement>> GetCollectionAsync(string path, int max, CancellationToken ct)
        {
            var items = new List<JsonElement>();
            string? next = path + (path.Contains('?') ? "&" : "?") + $"limit={Math.Min(PageSize, max)}&linked_partitioning=true";
            for (var pages = 0; next != null && items.Count < max && pages < 100; pages++)
            {
                var (page, nextHref) = ParseCollection(await GetAsync(next, ct));
                items.AddRange(page);
                next = nextHref;
            }
            return items.Count > max ? items.GetRange(0, max) : items;
        }

        internal static (List<JsonElement> Items, string? Next) ParseCollection(JsonElement r)
        {
            if (r.ValueKind == JsonValueKind.Array) return (r.EnumerateArray().ToList(), null);
            var items = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("collection", out var c) &&
                        c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().ToList()
                : new List<JsonElement>();
            var next = Str(r, "next_href");
            return (items, next.Length > 0 ? next : null);
        }

        private List<YaTrack> Remember(IEnumerable<JsonElement> items)
        {
            var list = new List<YaTrack>();
            foreach (var e in items)
            {
                var t = ParseTrack(UnwrapTrack(e));
                if (t.Id.Length == 0) continue;
                _known[t.Id] = t;
                list.Add(t);
            }
            return list;
        }

        // ────────── аккаунт ──────────

        public async Task<YaAccount> GetAccountAsync(CancellationToken ct = default)
        {
            var me = await GetAsync("/me", ct);
            var username = Str(me, "username");
            var id = ToId(Urn(me, "users"));
            if (me.ValueKind != JsonValueKind.Object || id.Length <= IdPrefix.Length)
                throw new YaApiException("Вход в SoundCloud истёк — нужно войти заново.", HttpStatusCode.Unauthorized);

            Uid = long.TryParse(id[IdPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? -n : -1;
            _avatar = CoverUris.Resolve(ArtworkTemplate(Str(me, "avatar_url")), 200);
            return new YaAccount
            {
                Uid = Uid,
                Login = FirstNonEmpty(Str(me, "permalink"), username),
                DisplayName = FirstNonEmpty(Str(me, "full_name"), username),
                HasPlus = Str(me, "plan") is { Length: > 0 } plan && !plan.Equals("Free", StringComparison.OrdinalIgnoreCase),
                AvatarUrl = _avatar,
            };
        }

        public Task<string?> GetAvatarUrlAsync(CancellationToken ct = default) => Task.FromResult(_avatar);

        // ────────── треки ──────────

        /// <summary>Лайки: полные треки приходят сразу — запоминаем, а наружу отдаём id (как у Яндекса).</summary>
        public async Task<(int Revision, List<string> Ids)> GetLikedIdsAsync(CancellationToken ct = default)
        {
            var items = await GetCollectionAsync("/me/likes/tracks?access=playable,preview,blocked", MaxLikes, ct);
            return (0, Remember(items).Select(t => t.Id).ToList());
        }

        /// <summary>Треки по id в том же порядке. Чужие id (Яндекс, свои файлы) пропускаются.</summary>
        public async Task<List<YaTrack>> GetTracksAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        {
            var mine = ids.Select(i => i.Split(':')[0]).Where(IsScId).ToList();
            var missing = mine.Where(id => !_known.ContainsKey(id)).Distinct().ToList();

            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(missing.Select(async id =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var t = ParseTrack(await GetAsync($"/tracks/{ToUrn(id, "tracks")}", ct));
                    if (t.Id.Length > 0) _known[t.Id] = t;
                }
                catch (YaApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    // трек удалён или скрыт автором — просто не показываем
                }
                finally
                {
                    gate.Release();
                }
            }));

            return mine.Select(id => _known.TryGetValue(id, out var t) ? t : null).Where(t => t != null).Select(t => t!).ToList();
        }

        /// <summary>Поиск: треки, авторы (как артисты) и плейлисты. Альбомов у SoundCloud нет.</summary>
        public async Task<YaSearchResult> SearchAsync(string query, CancellationToken ct = default)
        {
            var q = Uri.EscapeDataString(query);
            var tracksTask = GetAsync($"/tracks?q={q}&access=playable,preview&limit=40&linked_partitioning=true", ct);
            var usersTask = SafeAsync(() => GetAsync($"/users?q={q}&limit=8&linked_partitioning=true", ct));
            var playlistsTask = SafeAsync(() => GetAsync($"/playlists?q={q}&show_tracks=false&limit=8&linked_partitioning=true", ct));

            var tracks = Remember(ParseCollection(await tracksTask).Items);
            var users = await usersTask is { } u ? ParseCollection(u).Items.Select(ParseUser).Where(a => a.Id.Length > 0).ToList() : new();
            var playlists = await playlistsTask is { } p ? ParseCollection(p).Items.Select(ParsePlaylist).Where(x => x.Kind.Length > 0).ToList() : new();
            return new YaSearchResult
            {
                Tracks = tracks,
                TracksTotal = tracks.Count,
                Artists = users,
                Playlists = playlists,
            };
        }

        private static async Task<JsonElement?> SafeAsync(Func<Task<JsonElement>> request)
        {
            try { return await request(); }
            catch (Exception ex) when (ex is YaApiException or HttpRequestException or TaskCanceledException)
            {
                return null;
            }
        }

        public async Task<List<YaTrack>> SearchTracksAsync(string query, int page = 0, CancellationToken ct = default)
        {
            var q = Uri.EscapeDataString(query);
            var r = await GetAsync($"/tracks?q={q}&access=playable,preview&limit=50&offset={page * 50}&linked_partitioning=true", ct);
            return Remember(ParseCollection(r).Items);
        }

        public Task SetLikeAsync(YaTrack track, bool like, CancellationToken ct = default) =>
            RequestAsync(like ? HttpMethod.Post : HttpMethod.Delete, $"/likes/tracks/{TrackUrn(track.Id)}", null, ct);

        public Task DislikeAsync(YaTrack track, CancellationToken ct = default) =>
            throw new NotSupportedException("В SoundCloud нет «Не нравится»");

        // ────────── звук ──────────

        /// <summary>
        /// Ссылка /streams → HLS-плейлист → список сегментов для <see cref="AudioCache"/>.
        /// Ссылки подписаны и живут недолго — кэш вызывает это заново на каждую попытку.
        /// </summary>
        public async Task<CacheSource> ResolveAudioAsync(YaTrack track, bool lowest, CancellationToken ct = default)
        {
            var streams = await GetAsync($"/tracks/{TrackUrn(track.Id)}/streams", ct);
            var url = PickStreamUrl(streams, track.IsPreview)
                      ?? throw new YaApiException(track.IsPreview
                          ? "SoundCloud не дал даже фрагмент этого трека"
                          : "SoundCloud не отдаёт этот трек сторонним плеерам");
            return await ResolveUrlAsync(url, depth: 0, ct);
        }

        /// <summary>mp3 — его сегменты склеиваются в обычный MP3-файл без перекодирования.</summary>
        internal static string? PickStreamUrl(JsonElement streams, bool preview)
        {
            var keys = preview
                ? new[] { "preview_mp3_128_url", "hls_mp3_128_url", "http_mp3_128_url" }
                : new[] { "hls_mp3_128_url", "http_mp3_128_url", "preview_mp3_128_url" };
            return keys.Select(k => Str(streams, k)).FirstOrDefault(u => u.Length > 0);
        }

        private async Task<CacheSource> ResolveUrlAsync(string url, int depth, CancellationToken ct)
        {
            if (depth > 3) throw new YaApiException("Слишком много перенаправлений в потоке SoundCloud");

            var uri = new Uri(url);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            // ссылки на api.soundcloud.com требуют токен (и сами перенаправляют на CDN), ссылки CDN уже подписаны
            var authed = uri.Host.EndsWith("api.soundcloud.com", StringComparison.OrdinalIgnoreCase);
            using var resp = authed
                ? await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                : await _plain.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new YaApiException($"SoundCloud: HTTP {(int)resp.StatusCode} при запросе потока", resp.StatusCode);

            var finalUri = resp.RequestMessage?.RequestUri ?? uri;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            // обычный mp3 — пусть кэш качает его сам (с докачкой через Range)
            if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && !type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
                return new CacheSource(finalUri.ToString(), null);

            var text = (await resp.Content.ReadAsStringAsync(ct)).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (text.StartsWith('{'))
            {
                // /streams/… может ответить JSON-ом {"url": "…m3u8"}
                using var doc = JsonDocument.Parse(text);
                var next = Str(doc.RootElement, "url");
                if (next.Length == 0) throw new YaApiException("SoundCloud не вернул ссылку на поток");
                return await ResolveUrlAsync(next, depth + 1, ct);
            }
            if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal))
                return new CacheSource(finalUri.ToString(), null);

            var playlist = HlsPlaylist.Parse(text, finalUri);
            if (playlist.Variant != null) return await ResolveUrlAsync(playlist.Variant.ToString(), depth + 1, ct);
            if (playlist.Encrypted)
                throw new YaApiException("Трек защищён (DRM) — SoundCloud не даёт играть его в сторонних плеерах");
            if (playlist.Map != null)
                throw new YaApiException("Этот поток SoundCloud в формате, который Undertone пока не умеет склеивать");
            if (playlist.Segments.Count == 0) throw new YaApiException("Пустой поток SoundCloud");
            return new CacheSource(finalUri.ToString(), null, playlist.Segments.Select(s => s.ToString()).ToList());
        }

        public Task<YaFileInfo> GetFileInfoAsync(string trackId, string quality, CancellationToken ct = default) =>
            throw new NotSupportedException("У SoundCloud нет качества «без потерь»");

        public Task<string?> GetLyricsAsync(string trackId, bool sync, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task ReportPlayAsync(YaTrack track, double playedSeconds, double endPositionSeconds,
            string playId, string? playlistId = null, CancellationToken ct = default) => Task.CompletedTask;

        /// <summary>Похожие: /tracks/{urn}/related. Не вышло — пустая полка.</summary>
        public async Task<List<YaTrack>> GetSimilarTracksAsync(string trackId, CancellationToken ct = default)
        {
            if (!IsScId(trackId)) return new List<YaTrack>();
            var r = await SafeAsync(() => GetAsync($"/tracks/{TrackUrn(trackId)}/related?limit=20&linked_partitioning=true", ct));
            return r is { } related ? Remember(ParseCollection(related).Items) : new List<YaTrack>();
        }

        // ────────── «артист» = пользователь SoundCloud ──────────

        public async Task<YaArtistPage> GetArtistAsync(string artistId, CancellationToken ct = default)
        {
            var urn = ToUrn(artistId, "users");
            var user = await GetAsync($"/users/{urn}", ct);
            var tracks = await GetCollectionAsync($"/users/{urn}/tracks?access=playable,preview,blocked", 100, ct);
            var page = new YaArtistPage { Artist = ParseUser(user) };
            page.Tracks.AddRange(Remember(tracks));
            return page;
        }

        public Task<(YaAlbum Album, List<YaTrack> Tracks)> GetAlbumAsync(string albumId, CancellationToken ct = default) =>
            throw new NotSupportedException("В SoundCloud нет альбомов");

        // ────────── плейлисты ──────────

        public async Task<List<YaPlaylist>> GetPlaylistsAsync(CancellationToken ct = default)
        {
            var items = await GetCollectionAsync("/me/playlists?show_tracks=false", 500, ct);
            return items.Select(ParsePlaylist).Where(p => p.Kind.Length > 0).ToList();
        }

        public async Task<(YaPlaylist Playlist, List<YaTrack> Tracks)> GetPlaylistAsync(string kind, CancellationToken ct = default)
        {
            var urn = ToUrn(kind, "playlists");
            var meta = await GetAsync($"/playlists/{urn}?show_tracks=false", ct);
            var items = await GetCollectionAsync($"/playlists/{urn}/tracks?access=playable,preview,blocked", MaxPlaylistTracks, ct);
            return (WithKind(ParsePlaylist(meta), kind), Remember(items));
        }

        /// <summary>Чужой плейлист (из поиска): у SoundCloud id плейлиста уникален и без владельца.</summary>
        public Task<(YaPlaylist Playlist, List<YaTrack> Tracks)> GetPlaylistAsync(long ownerUid, string kind, CancellationToken ct = default) =>
            GetPlaylistAsync(kind, ct);

        public async Task<YaPlaylist> CreatePlaylistAsync(string title, CancellationToken ct = default) =>
            ParsePlaylist(await RequestAsync(HttpMethod.Post, "/playlists", new Dictionary<string, object>
            {
                ["playlist"] = new Dictionary<string, object> { ["title"] = title, ["sharing"] = "private" },
            }, ct));

        public async Task<YaPlaylist> RenamePlaylistAsync(string kind, string title, CancellationToken ct = default) =>
            WithKind(ParsePlaylist(await RequestAsync(HttpMethod.Put, $"/playlists/{ToUrn(kind, "playlists")}", new Dictionary<string, object>
            {
                ["playlist"] = new Dictionary<string, object> { ["title"] = title },
            }, ct)), kind);

        public Task DeletePlaylistAsync(string kind, CancellationToken ct = default) =>
            RequestAsync(HttpMethod.Delete, $"/playlists/{ToUrn(kind, "playlists")}", null, ct);

        /// <summary>SoundCloud меняет список треков целиком: берём свежий и ставим трек в начало (как у Яндекса).</summary>
        public async Task<YaPlaylist> AddToPlaylistAsync(YaPlaylist playlist, YaTrack track, CancellationToken ct = default)
        {
            var ids = await PlaylistTrackIdsAsync(playlist.Kind, ct);
            ids.Insert(0, track.Id);
            return await PutTracksAsync(playlist.Kind, ids, ct);
        }

        /// <summary>Удаляет ближайшее к hintIndex вхождение трека в свежем списке — соседний трек не заденет.</summary>
        public async Task<YaPlaylist> RemoveTrackFromPlaylistAsync(string kind, string trackId, int hintIndex, CancellationToken ct = default)
        {
            var ids = await PlaylistTrackIdsAsync(kind, ct);
            var index = NearestIndex(ids, trackId, hintIndex);
            if (index < 0) throw new YaApiException("трека уже нет в плейлисте");
            ids.RemoveAt(index);
            return await PutTracksAsync(kind, ids, ct);
        }

        internal static int NearestIndex(IReadOnlyList<string> ids, string trackId, int hintIndex)
        {
            var index = -1;
            for (var i = 0; i < ids.Count; i++)
            {
                if (ids[i] != trackId) continue;
                if (index < 0 || Math.Abs(i - hintIndex) < Math.Abs(index - hintIndex)) index = i;
            }
            return index;
        }

        private async Task<List<string>> PlaylistTrackIdsAsync(string kind, CancellationToken ct)
        {
            var items = await GetCollectionAsync($"/playlists/{ToUrn(kind, "playlists")}/tracks?access=playable,preview,blocked",
                MaxPlaylistTracks, ct);
            return Remember(items).Select(t => t.Id).ToList();
        }

        private async Task<YaPlaylist> PutTracksAsync(string kind, List<string> trackIds, CancellationToken ct)
        {
            var r = await RequestAsync(HttpMethod.Put, $"/playlists/{ToUrn(kind, "playlists")}", new Dictionary<string, object>
            {
                ["playlist"] = new Dictionary<string, object>
                {
                    ["tracks"] = trackIds.Select(id => new Dictionary<string, string> { ["urn"] = ToUrn(id, "tracks") }).ToArray(),
                },
            }, ct);
            var updated = WithKind(ParsePlaylist(r), kind);
            // ответ может прийти без счётчика — считаем сами
            return updated.TrackCount == 0 && trackIds.Count > 0 ? Copy(updated, trackCount: trackIds.Count) : updated;
        }

        private static YaPlaylist WithKind(YaPlaylist p, string kind) => p.Kind.Length > 0 ? p : Copy(p, kind: kind);

        private static YaPlaylist Copy(YaPlaylist p, string? kind = null, int? trackCount = null) => new()
        {
            Kind = kind ?? p.Kind,
            OwnerUid = p.OwnerUid,
            OwnerName = p.OwnerName,
            Title = p.Title,
            TrackCount = trackCount ?? p.TrackCount,
            CoverUri = p.CoverUri,
            DurationMs = p.DurationMs,
        };

        // ────────── волны нет ──────────

        public Task<WaveBatch> StartWaveAsync(IEnumerable<string> seeds, CancellationToken ct = default) =>
            throw new NotSupportedException("В SoundCloud нет Моей волны");

        public Task<WaveBatch> GetWaveTracksAsync(string sessionId, IEnumerable<string> queue, CancellationToken ct = default) =>
            throw new NotSupportedException("В SoundCloud нет Моей волны");

        public Task WaveFeedbackAsync(string sessionId, string? batchId, string type, string? trackId = null,
            double? playedSeconds = null, CancellationToken ct = default) => Task.CompletedTask;

        // ────────── парсинг ──────────

        /// <summary>Элемент лайков бывает самим треком, а бывает обёрткой {"track": {…}}.</summary>
        private static JsonElement UnwrapTrack(JsonElement e) =>
            e.ValueKind == JsonValueKind.Object && !e.TryGetProperty("title", out _) &&
            e.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object
                ? t
                : e;

        internal static YaTrack ParseTrack(JsonElement e)
        {
            var user = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("user", out var u) &&
                       u.ValueKind == JsonValueKind.Object ? u : default;
            var username = Str(user, "username");
            var userId = ToId(Urn(user, "users"));
            var artist = FirstNonEmpty(Str(e, "metadata_artist"), username);
            var access = Str(e, "access");
            var streamable = !(e.ValueKind == JsonValueKind.Object && e.TryGetProperty("streamable", out var s) &&
                               s.ValueKind == JsonValueKind.False);
            var title = Str(e, "title");

            return new YaTrack
            {
                Id = ToId(Urn(e, "tracks")),
                Title = title.Length > 0 ? title : "Трек недоступен",
                Artists = artist.Length > 0 ? new[] { artist } : Array.Empty<string>(),
                ArtistRefs = userId.Length > 0 && artist.Length > 0
                    ? new[] { new YaArtistRef(userId, artist) }
                    : Array.Empty<YaArtistRef>(),
                DurationMs = Int(e, "duration"),
                CoverUri = ArtworkTemplate(FirstNonEmpty(Str(e, "artwork_url"), Str(user, "avatar_url"))),
                Available = title.Length > 0 && streamable && access != "blocked",
                IsPreview = access == "preview",
                Permalink = Str(e, "permalink_url") is { Length: > 0 } link ? link : null,
            };
        }

        internal static YaPlaylist ParsePlaylist(JsonElement e)
        {
            var cover = ArtworkTemplate(Str(e, "artwork_url"));
            if (cover == null && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("tracks", out var tracks) &&
                tracks.ValueKind == JsonValueKind.Array)
                cover = tracks.EnumerateArray().Select(t => ArtworkTemplate(Str(t, "artwork_url"))).FirstOrDefault(c => c != null);
            var user = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("user", out var u) &&
                       u.ValueKind == JsonValueKind.Object ? u : default;
            var owner = ToId(Urn(user, "users"));
            return new YaPlaylist
            {
                Kind = ToId(Urn(e, "playlists")),
                OwnerUid = owner.Length > IdPrefix.Length && long.TryParse(owner[IdPrefix.Length..], out var n) ? -n : 0,
                OwnerName = Str(user, "username"),
                Title = Str(e, "title"),
                TrackCount = (int)Int(e, "track_count"),
                CoverUri = cover,
                DurationMs = Int(e, "duration"),
            };
        }

        internal static YaArtist ParseUser(JsonElement e)
        {
            var followers = Int(e, "followers_count");
            return new YaArtist
            {
                Id = ToId(Urn(e, "users")),
                Name = FirstNonEmpty(Str(e, "username"), Str(e, "full_name")),
                CoverUri = ArtworkTemplate(Str(e, "avatar_url")),
                Genres = followers > 0 ? new[] { $"{followers:N0} подписчиков" } : Array.Empty<string>(),
            };
        }

        /// <summary>«…-large.jpg» → «…-%%.jpg», размер подставит <see cref="CoverUris.Resolve"/>.</summary>
        internal static string? ArtworkTemplate(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var i = url.LastIndexOf("-large.", StringComparison.Ordinal);
            return i < 0 ? url : url[..i] + "-%%." + url[(i + "-large.".Length)..];
        }

        /// <summary>urn объекта; у старых ответов только числовой id — достраиваем soundcloud:{kind}:{id}.</summary>
        private static string Urn(JsonElement e, string kind)
        {
            var urn = Str(e, "urn");
            if (urn.Length > 0) return urn;
            var id = Str(e, "id");
            return id.Length > 0 ? $"soundcloud:{kind}:{id}" : "";
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

        public void Dispose()
        {
            _http.Dispose();
            _plain.Dispose();
        }
    }

    /// <summary>Минимальный разбор m3u8: вариант (master), сегменты, ключ шифрования, init-сегмент.</summary>
    internal sealed record HlsPlaylist(Uri? Variant, IReadOnlyList<Uri> Segments, bool Encrypted, Uri? Map)
    {
        public static HlsPlaylist Parse(string text, Uri baseUri)
        {
            Uri? variant = null, map = null;
            var segments = new List<Uri>();
            var encrypted = false;
            var nextIsVariant = false;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) { nextIsVariant = true; continue; }
                if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
                {
                    encrypted = !line.Contains("METHOD=NONE", StringComparison.Ordinal);
                    continue;
                }
                if (line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal))
                {
                    var at = line.IndexOf("URI=\"", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        var end = line.IndexOf('"', at + 5);
                        if (end > at) map = new Uri(baseUri, line.Substring(at + 5, end - at - 5));
                    }
                    continue;
                }
                if (line.StartsWith('#')) continue;

                var uri = new Uri(baseUri, line);
                if (nextIsVariant)
                {
                    // берём первый вариант: у SoundCloud он один
                    variant ??= uri;
                    nextIsVariant = false;
                }
                else segments.Add(uri);
            }
            return new HlsPlaylist(segments.Count == 0 ? variant : null, segments, encrypted, map);
        }
    }
}
