using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Своя «Моя волна», миксы и «похожие» для Spotify. Рекомендации, похожих артистов, топ-треки артиста
    /// и редакционные плейлисты Spotify сторонним приложениям закрыл, поэтому всё собирается из того, что открыто:
    /// топ и история слушателя (/me/top, /me/player/recently-played), лайки, поиск по артисту и жанру,
    /// дискографии и соавторы. Пропуски и дослушивания в волне меняют, кого ставить дальше.
    /// </summary>
    public sealed partial class SpotifyApi
    {
        private const string MyWaveSeed = "user:onyourwave";
        private static readonly TimeSpan TasteTtl = TimeSpan.FromMinutes(30);

        private List<YaTrack> _liked = new();
        private SpotifyTaste? _taste;
        private DateTime _tasteAt;
        private readonly SemaphoreSlim _tasteLock = new(1, 1);
        private readonly ConcurrentDictionary<string, RadioSession> _sessions = new();
        private readonly ConcurrentDictionary<string, YaArtist> _artistInfo = new();
        private readonly ConcurrentDictionary<string, (DateTime At, List<YaTrack> Tracks)> _built = new();

        /// <summary>Вход был до версии с миксами: нет прав на топ и историю — нужно перезайти в Spotify.</summary>
        public bool MissingHistoryScopes { get; private set; }

        // ────────── вкус ──────────

        public async Task<SpotifyTaste> GetTasteAsync(CancellationToken ct = default)
        {
            if (_taste != null && DateTime.UtcNow - _tasteAt < TasteTtl) return _taste;
            await _tasteLock.WaitAsync(ct);
            try
            {
                if (_taste != null && DateTime.UtcNow - _tasteAt < TasteTtl) return _taste;

                var artistsShort = TopAsync("artists", "short_term", ct);
                var artistsMedium = TopAsync("artists", "medium_term", ct);
                var tracksShort = TopAsync("tracks", "short_term", ct);
                var tracksMedium = TopAsync("tracks", "medium_term", ct);
                var tracksLong = TopAsync("tracks", "long_term", ct);
                var recent = HistoryAsync("/me/player/recently-played?limit=50", ct);

                var topArtists = (await artistsShort).Concat(await artistsMedium)
                    .Select(ParseArtist).Where(a => a.Id.Length > 0)
                    .GroupBy(a => a.Id).Select(g => g.First()).ToList();
                foreach (var a in topArtists) _artistInfo[a.Id] = a;

                var taste = new SpotifyTaste
                {
                    TopArtists = topArtists,
                    TopShort = Remember(await tracksShort),
                    TopMedium = Remember(await tracksMedium),
                    TopLong = Remember(await tracksLong),
                    Recent = Remember((await recent).Select(UnwrapItem).Where(t => t.ValueKind == JsonValueKind.Object)),
                    Liked = _liked,
                };

                // топа нет (свежий аккаунт или нет прав) — жанры берём у самых частых артистов из лайков
                if (taste.TopArtists.Count == 0)
                {
                    var ids = taste.ArtistWeights().Take(8).Select(a => a.Id).ToList();
                    var infos = await Task.WhenAll(ids.Select(id => ArtistInfoAsync(id, ct)));
                    taste.TopArtists.AddRange(infos.Where(a => a != null).Select(a => a!));
                }

                _taste = taste;
                _tasteAt = DateTime.UtcNow;
                return taste;
            }
            finally
            {
                _tasteLock.Release();
            }
        }

        private async Task<List<JsonElement>> TopAsync(string type, string range, CancellationToken ct) =>
            await HistoryAsync($"/me/top/{type}?time_range={range}&limit=50", ct);

        /// <summary>Топ и история: нет прав (старый вход) — пусто и флаг «перезайди».</summary>
        private async Task<List<JsonElement>> HistoryAsync(string path, CancellationToken ct)
        {
            try
            {
                return ParsePage(await GetAsync(path, ct)).Items;
            }
            catch (YaApiException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                MissingHistoryScopes = true;
                return new List<JsonElement>();
            }
            catch (Exception ex) when (ex is YaApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                Log.Warn($"Spotify {path}: не получено", ex);
                return new List<JsonElement>();
            }
        }

        private async Task<YaArtist?> ArtistInfoAsync(string id, CancellationToken ct)
        {
            if (_artistInfo.TryGetValue(id, out var cached)) return cached;
            var r = await SafeAsync(() => GetAsync($"/artists/{Raw(id)}", ct));
            if (r is not { } json) return null;
            var a = ParseArtist(json);
            if (a.Id.Length == 0) return null;
            _artistInfo[a.Id] = a;
            return a;
        }

        // ────────── источники нового ──────────

        /// <summary>Треки артиста из поиска (Spotify ставит популярное выше); deep — со случайной глубины.</summary>
        private async Task<List<YaTrack>> ArtistTracksAsync(string id, string name, RadioSession s, CancellationToken ct)
        {
            var offset = s.PreferPopular ? 0 : s.Rng.Next(0, 3) * 10;
            var q = Uri.EscapeDataString($"artist:\"{name}\"");
            var r = await SafeAsync(() => GetAsync($"/search?q={q}&type=track&limit=10&offset={offset}", ct));
            var found = r is { } json ? Remember(Items(json, "tracks")) : new List<YaTrack>();
            var mine = found.Where(t => t.ArtistRefs.Any(a => a.Id == id)).ToList();
            if (mine.Count == 0 && offset > 0) return await ArtistTracksAsync(id, name, new RadioSession { Rng = s.Rng, PreferPopular = true }, ct);
            // соавторы из найденного — новые источники для волны
            foreach (var t in mine)
                foreach (var a in t.ArtistRefs.Where(a => a.Id != id))
                    if (!s.Artists.ContainsKey(a.Id)) s.AddArtist(a.Id, a.Name, 0.7);
            return mine;
        }

        private async Task<List<YaTrack>> GenreTracksAsync(string genre, RadioSession s, CancellationToken ct)
        {
            var offset = (s.PreferPopular ? s.Rng.Next(0, 3) : s.Rng.Next(0, 15)) * 10;
            var q = Uri.EscapeDataString($"genre:\"{genre}\"");
            var r = await SafeAsync(() => GetAsync($"/search?q={q}&type=track&limit=10&offset={offset}", ct));
            return r is { } json ? Remember(Items(json, "tracks")) : new List<YaTrack>();
        }

        /// <summary>Артисты того же жанра — «похожие» без закрытого /related-artists.</summary>
        private async Task<List<YaArtist>> GenreArtistsAsync(string genre, int offset, CancellationToken ct)
        {
            var q = Uri.EscapeDataString($"genre:\"{genre}\"");
            var r = await SafeAsync(() => GetAsync($"/search?q={q}&type=artist&limit=10&offset={offset}", ct));
            var list = r is { } json ? Items(json, "artists").Select(ParseArtist).Where(a => a.Id.Length > 0).ToList() : new();
            foreach (var a in list) _artistInfo.TryAdd(a.Id, a);
            return list;
        }

        /// <summary>Набирает новое из разных источников, пока не наберётся need треков (или кончатся попытки).</summary>
        private async Task<List<YaTrack>> DiscoverAsync(RadioSession s, int need, CancellationToken ct)
        {
            var fresh = new List<YaTrack>();
            for (var attempt = 0; attempt < 8 && fresh.Count(t => !s.Seen.Contains(t.Id)) < need * 2; attempt++)
            {
                // источники: артисты сессии (с соавторами), жанровые треки, артисты того же жанра
                var sources = new List<double>
                {
                    s.Artists.Count > 0 ? 3 : 0,
                    s.Genres.Count > 0 ? 2 : 0,
                    s.Genres.Count > 0 ? 1.2 : 0,
                };
                switch (s.PickIndex(sources))
                {
                    case 0:
                    {
                        var artists = s.Artists.Where(kv => s.Penalty.GetValueOrDefault(kv.Key) < 2).ToList();
                        var i = s.PickIndex(artists.Select(kv => kv.Value.Weight - s.Penalty.GetValueOrDefault(kv.Key) * 0.5).ToList());
                        if (i < 0) continue;
                        fresh.AddRange(await ArtistTracksAsync(artists[i].Key, artists[i].Value.Name, s, ct));
                        break;
                    }
                    case 1:
                    {
                        var i = s.PickIndex(s.Genres.Select(g => g.Weight).ToList());
                        if (i < 0) continue;
                        fresh.AddRange(await GenreTracksAsync(s.Genres[i].Genre, s, ct));
                        break;
                    }
                    case 2:
                    {
                        var i = s.PickIndex(s.Genres.Select(g => g.Weight).ToList());
                        if (i < 0) continue;
                        var peers = await GenreArtistsAsync(s.Genres[i].Genre, s.Rng.Next(0, 4) * 10, ct);
                        var fresh0 = peers.Where(p => !s.Artists.ContainsKey(p.Id)).OrderBy(_ => s.Rng.Next()).Take(1).ToList();
                        foreach (var p in fresh0)
                        {
                            s.AddArtist(p.Id, p.Name, 0.8);
                            fresh.AddRange(await ArtistTracksAsync(p.Id, p.Name, s, ct));
                        }
                        break;
                    }
                    default:
                        attempt = 99;
                        break;
                }
            }
            return fresh.OrderBy(_ => s.Rng.Next()).ToList();
        }

        // ────────── сессии: волна, миксы, похожие ──────────

        private async Task<RadioSession> CreateSessionAsync(IReadOnlyCollection<string> seeds, Random? rng, CancellationToken ct)
        {
            var taste = await GetTasteAsync(ct);
            var liked = new HashSet<string>(_liked.Select(t => t.Id));
            var diversity = seeds.FirstOrDefault(x => x.StartsWith("settingDiversity:", StringComparison.Ordinal))?.Split(':')[1] ?? "";
            var main = seeds.FirstOrDefault(x => !x.StartsWith("setting", StringComparison.Ordinal)) ?? MyWaveSeed;
            rng ??= new Random();

            if (main.StartsWith("artist:", StringComparison.Ordinal))
            {
                var id = main["artist:".Length..];
                var info = await ArtistInfoAsync(id, ct);
                var s = new RadioSession
                {
                    Rng = rng,
                    FamiliarRatio = 0.25,
                    LikedIds = liked,
                    FamiliarPool = taste.Familiar().Where(t => t.ArtistRefs.Any(a => a.Id == id)).ToList(),
                    Genres = info?.Genres.Select((g, i) => (g, 1.0 / (1 + i))).ToList() ?? new(),
                };
                if (info != null) s.AddArtist(id, info.Name, 4);
                foreach (var (cid, c) in SpotifyTaste.Collaborators(taste.Familiar()).GetValueOrDefault(id) ?? new())
                    s.AddArtist(cid, c.Name, 1 + c.Count * 0.5);
                return s;
            }

            if (main.StartsWith("track:", StringComparison.Ordinal))
            {
                var id = main["track:".Length..];
                var track = (await GetTracksAsync(new[] { id }, ct)).FirstOrDefault();
                var lead = track?.ArtistRefs.FirstOrDefault();
                var info = lead != null ? await ArtistInfoAsync(lead.Id, ct) : null;
                var s = new RadioSession
                {
                    Rng = rng,
                    FamiliarRatio = 0.15,
                    LikedIds = liked,
                    FamiliarPool = taste.Familiar().Where(t => track != null && t.ArtistRefs.Any(a => track.ArtistRefs.Any(b => b.Id == a.Id))).ToList(),
                    Genres = info?.Genres.Select((g, i) => (g, 1.0 / (1 + i))).ToList() ?? new(),
                };
                if (track != null)
                {
                    s.Seen.Add(track.Id);
                    foreach (var a in track.ArtistRefs) s.AddArtist(a.Id, a.Name, a == lead ? 3 : 2);
                }
                return s;
            }

            if (main.StartsWith("genre:", StringComparison.Ordinal))
            {
                var genre = main["genre:".Length..];
                var s = new RadioSession
                {
                    Rng = rng,
                    FamiliarRatio = 0.3,
                    LikedIds = liked,
                    FamiliarPool = taste.Familiar().Where(t => t.ArtistRefs.Any(a =>
                        _artistInfo.TryGetValue(a.Id, out var ai) && ai.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))).ToList(),
                    Genres = new() { (genre, 1.0) },
                };
                foreach (var a in taste.TopArtists.Where(a => a.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)).Take(10))
                    s.AddArtist(a.Id, a.Name, 2);
                return s;
            }

            // Моя волна: «Любимое» — больше знакомого, «Незнакомое» — только новое, «Популярное» — из начала выдачи
            var wave = new RadioSession
            {
                Rng = rng,
                FamiliarRatio = diversity switch { "favorite" => 0.7, "discover" => 0.0, _ => 0.35 },
                ExcludeLiked = diversity == "discover",
                PreferPopular = diversity == "popular",
                LikedIds = liked,
                FamiliarPool = taste.Familiar(),
                Genres = taste.Genres().Take(8).ToList(),
            };
            foreach (var (id, name, weight) in taste.ArtistWeights().Take(30)) wave.AddArtist(id, name, weight);
            foreach (var row in SpotifyTaste.Collaborators(taste.Familiar()).Values)
                foreach (var (cid, c) in row)
                    if (!wave.Artists.ContainsKey(cid)) wave.AddArtist(cid, c.Name, 0.6);
            return wave;
        }

        private async Task<List<YaTrack>> NextBatchAsync(RadioSession s, int size, CancellationToken ct)
        {
            var need = Math.Max(1, size - (int)Math.Round(size * s.FamiliarRatio));
            var fresh = await DiscoverAsync(s, need, ct);
            return s.Compose(fresh, size);
        }

        public async Task<WaveBatch> StartWaveAsync(IEnumerable<string> seeds, CancellationToken ct = default)
        {
            var s = await CreateSessionAsync(seeds.ToList(), null, ct);
            _sessions[s.Id] = s;
            var tracks = await NextBatchAsync(s, 10, ct);
            if (tracks.Count == 0)
                throw new YaApiException(MissingHistoryScopes && _liked.Count == 0
                    ? "мало данных о вкусе — лайкни пару треков или перезайди в Spotify (меню профиля), чтобы Undertone видел историю"
                    : "Spotify не нашёл треков для волны — попробуй ещё раз");
            return new WaveBatch { SessionId = s.Id, Tracks = tracks };
        }

        public async Task<WaveBatch> GetWaveTracksAsync(string sessionId, IEnumerable<string> queue, CancellationToken ct = default)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
            {
                s = await CreateSessionAsync(new[] { MyWaveSeed }, null, ct);
                _sessions[sessionId] = s;
            }
            foreach (var id in queue) s.Seen.Add(id.Split(':')[0]);
            return new WaveBatch { SessionId = sessionId, Tracks = await NextBatchAsync(s, 8, ct) };
        }

        /// <summary>skip в первые 30 секунд — минус артисту; дослушал — плюс и его соавторы в источники.</summary>
        public Task WaveFeedbackAsync(string sessionId, string? batchId, string type, string? trackId = null,
            double? playedSeconds = null, CancellationToken ct = default)
        {
            if (trackId == null || !_sessions.TryGetValue(sessionId, out var s) || !_known.TryGetValue(trackId.Split(':')[0], out var track))
                return Task.CompletedTask;
            if (type == "skip" && (playedSeconds ?? 0) < 30) s.Feedback(track, skipped: true);
            else if (type == "trackFinished") s.Feedback(track, skipped: false);
            return Task.CompletedTask;
        }

        /// <summary>«Похожие»: артисты трека, их соавторы и тот же жанр. Кэш на полчаса.</summary>
        public async Task<List<YaTrack>> GetSimilarTracksAsync(string trackId, CancellationToken ct = default)
        {
            if (!IsSpId(trackId)) return new List<YaTrack>();
            var key = "similar:" + trackId;
            if (_built.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TasteTtl) return hit.Tracks;
            try
            {
                var s = await CreateSessionAsync(new[] { "track:" + trackId }, new Random(SpotifyMixes.DaySeed(DateTime.Now, trackId)), ct);
                var tracks = await NextBatchAsync(s, 20, ct);
                _built[key] = (DateTime.UtcNow, tracks);
                return tracks;
            }
            catch (Exception ex) when (ex is YaApiException or System.Net.Http.HttpRequestException)
            {
                Log.Warn("Spotify: похожие не собрались", ex);
                return new List<YaTrack>();
            }
        }

        // ────────── миксы ──────────

        public async Task<List<SpotifyMix>> GetMixesAsync(CancellationToken ct = default) =>
            SpotifyMixes.Catalog(await GetTasteAsync(ct));

        /// <summary>Треки микса. Собранные стабильны в течение дня (зерно от даты), топ и недавнее — как есть.</summary>
        public async Task<List<YaTrack>> GetMixTracksAsync(string mixId, CancellationToken ct = default)
        {
            var key = $"mix:{DateTime.Now:yyyy-MM-dd}:{mixId}";
            if (_built.TryGetValue(key, out var hit)) return hit.Tracks;
            var taste = await GetTasteAsync(ct);
            var rng = new Random(SpotifyMixes.DaySeed(DateTime.Now, mixId));

            List<YaTrack> tracks;
            switch (mixId)
            {
                case "top-short": tracks = taste.TopShort.Where(t => t.Available).ToList(); break;
                case "top-long": tracks = taste.TopLong.Where(t => t.Available).ToList(); break;
                case "recent": tracks = taste.Recent.Where(t => t.Available).GroupBy(t => t.Id).Select(g => g.First()).ToList(); break;
                default:
                {
                    var seeds = mixId switch
                    {
                        "daily" => new[] { MyWaveSeed },
                        "discover" => new[] { MyWaveSeed, "settingDiversity:discover" },
                        _ => new[] { mixId }, // genre:… / artist:…
                    };
                    var s = await CreateSessionAsync(seeds, rng, ct);
                    if (mixId == "daily") s = WithRatio(s, 0.5);
                    tracks = new List<YaTrack>();
                    for (var round = 0; round < 6 && tracks.Count < 30; round++)
                    {
                        var batch = await NextBatchAsync(s, 10, ct);
                        if (batch.Count == 0) break;
                        tracks.AddRange(batch);
                    }
                    break;
                }
            }
            _built[key] = (DateTime.UtcNow, tracks);
            return tracks;
        }

        private static RadioSession WithRatio(RadioSession s, double ratio)
        {
            var copy = new RadioSession
            {
                Rng = s.Rng, FamiliarRatio = ratio, ExcludeLiked = s.ExcludeLiked, PreferPopular = s.PreferPopular,
                FamiliarPool = s.FamiliarPool, Genres = s.Genres, LikedIds = s.LikedIds,
            };
            foreach (var kv in s.Artists) copy.Artists[kv.Key] = kv.Value;
            return copy;
        }

        // ────────── похожие артисты для страницы артиста ──────────

        /// <summary>Соавторы (по найденным трекам и вкусу) + артисты того же жанра. Без закрытого /related-artists.</summary>
        private async Task<List<YaArtist>> SimilarArtistsAsync(YaArtist artist, IEnumerable<YaTrack> tracks, CancellationToken ct)
        {
            var known = tracks.Concat(_taste?.Familiar() ?? new List<YaTrack>());
            var collab = (SpotifyTaste.Collaborators(known).GetValueOrDefault(artist.Id) ?? new())
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv => _artistInfo.TryGetValue(kv.Key, out var a) ? a : new YaArtist { Id = kv.Key, Name = kv.Value.Name })
                .Take(6).ToList();
            var peers = artist.Genres.Length > 0 ? await GenreArtistsAsync(artist.Genres[0], 0, ct) : new List<YaArtist>();
            return collab.Concat(peers).Where(a => a.Id != artist.Id).GroupBy(a => a.Id).Select(g => g.First()).Take(10).ToList();
        }
    }
}
