using System;
using System.Collections.Generic;
using System.Linq;

namespace Undertone.Core
{
    /// <summary>Микс Spotify на странице «Подборки»: id, название, подпись и значок.</summary>
    public sealed record SpotifyMix(string Id, string Title, string Sub, string Glyph);

    /// <summary>
    /// Вкус слушателя по данным Spotify: топ артистов и треков, недавнее, лайки, жанры, соавторы.
    /// Рекомендаций и «похожих артистов» Spotify сторонним приложениям больше не даёт — поэтому похожесть
    /// собираем сами: соавторы (кто с кем записывал треки) + общие жанры. Чистая логика, без сети — проверяется тестами.
    /// </summary>
    public sealed class SpotifyTaste
    {
        public List<YaArtist> TopArtists { get; init; } = new();
        public List<YaTrack> TopShort { get; init; } = new();
        public List<YaTrack> TopMedium { get; init; } = new();
        public List<YaTrack> TopLong { get; init; } = new();
        public List<YaTrack> Recent { get; init; } = new();
        public List<YaTrack> Liked { get; init; } = new();

        /// <summary>Знакомое: топ, недавнее, лайки — без повторов, самое актуальное первым.</summary>
        public List<YaTrack> Familiar() =>
            TopShort.Concat(Recent).Concat(TopMedium).Concat(Liked).Concat(TopLong)
                .Where(t => t.Available).GroupBy(t => t.Id).Select(g => g.First()).ToList();

        /// <summary>Артисты по весу: топ Spotify (если он есть), иначе — как часто встречаются в лайках и недавнем.</summary>
        public List<(string Id, string Name, double Weight)> ArtistWeights()
        {
            var weights = new Dictionary<string, (string Name, double Weight)>();
            void Add(string id, string name, double w)
            {
                if (id.Length == 0 || name.Length == 0) return;
                weights[id] = weights.TryGetValue(id, out var cur) ? (cur.Name, cur.Weight + w) : (name, w);
            }
            for (var i = 0; i < TopArtists.Count; i++) Add(TopArtists[i].Id, TopArtists[i].Name, 3.0 * (1 - i / (double)(TopArtists.Count + 1)));
            foreach (var t in TopShort) foreach (var a in t.ArtistRefs.Take(1)) Add(a.Id, a.Name, 1.0);
            foreach (var t in Recent) foreach (var a in t.ArtistRefs.Take(1)) Add(a.Id, a.Name, 0.6);
            foreach (var t in Liked.Take(300)) foreach (var a in t.ArtistRefs.Take(1)) Add(a.Id, a.Name, 0.4);
            return weights.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Weight)).OrderByDescending(x => x.Item3).ToList();
        }

        /// <summary>Жанры по весу: у артистов из начала топа вес больше.</summary>
        public static List<(string Genre, double Weight)> RankGenres(IReadOnlyList<YaArtist> artists)
        {
            var w = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < artists.Count; i++)
                foreach (var g in artists[i].Genres)
                    w[g] = w.GetValueOrDefault(g) + 1.0 / (1 + i * 0.15);
            return w.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
        }

        public List<(string Genre, double Weight)> Genres() => RankGenres(TopArtists);

        /// <summary>Соавторы: кто с кем встречается в одном треке. artistId → (соавтор → (имя, сколько раз)).</summary>
        public static Dictionary<string, Dictionary<string, (string Name, int Count)>> Collaborators(IEnumerable<YaTrack> tracks)
        {
            var map = new Dictionary<string, Dictionary<string, (string Name, int Count)>>();
            foreach (var t in tracks)
            {
                var refs = t.ArtistRefs.Where(a => a.Id.Length > 0).GroupBy(a => a.Id).Select(g => g.First()).ToList();
                if (refs.Count < 2) continue;
                foreach (var a in refs)
                    foreach (var b in refs)
                    {
                        if (a.Id == b.Id) continue;
                        if (!map.TryGetValue(a.Id, out var row)) map[a.Id] = row = new();
                        row[b.Id] = row.TryGetValue(b.Id, out var cur) ? (cur.Name, cur.Count + 1) : (b.Name, 1);
                    }
            }
            return map;
        }
    }

    /// <summary>Одна волна (или сборка микса/«похожих»): что уже было, кого пропускают, откуда брать новое.</summary>
    public sealed class RadioSession
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public Random Rng { get; init; } = new();
        /// <summary>Сколько в каждой пачке знакомого (0..1). Остальное — новое.</summary>
        public double FamiliarRatio { get; init; } = 0.35;
        /// <summary>«Незнакомое» — лайкнутое не подсовываем вовсе.</summary>
        public bool ExcludeLiked { get; init; }
        /// <summary>Брать из начала выдачи поиска (там популярное), а не из глубины.</summary>
        public bool PreferPopular { get; init; }
        public List<YaTrack> FamiliarPool { get; init; } = new();
        /// <summary>Артисты-источники с весами; пополняются соавторами по ходу волны.</summary>
        public Dictionary<string, (string Name, double Weight)> Artists { get; } = new();
        public List<(string Genre, double Weight)> Genres { get; init; } = new();
        public HashSet<string> Seen { get; } = new();
        /// <summary>Сколько раз пропустили артиста (≥2 — больше не ставим), минус — дослушивали.</summary>
        public Dictionary<string, double> Penalty { get; } = new();
        public HashSet<string> LikedIds { get; init; } = new();

        public void AddArtist(string id, string name, double weight)
        {
            if (id.Length == 0 || name.Length == 0) return;
            Artists[id] = Artists.TryGetValue(id, out var cur) ? (cur.Name, Math.Max(cur.Weight, weight)) : (name, weight);
        }

        public static string MainArtist(YaTrack t) =>
            t.ArtistRefs.FirstOrDefault()?.Id is { Length: > 0 } id ? id : t.ArtistsText;

        /// <summary>Пропуск в первые 30 секунд — минус артисту; дослушал — плюс (и меньше штрафа).</summary>
        public void Feedback(YaTrack track, bool skipped)
        {
            var key = MainArtist(track);
            Penalty[key] = Penalty.GetValueOrDefault(key) + (skipped ? 1 : -0.5);
            if (!skipped)
                foreach (var a in track.ArtistRefs)
                    AddArtist(a.Id, a.Name, Artists.TryGetValue(a.Id, out var cur) ? cur.Weight + 0.5 : 1);
        }

        public bool Blocked(YaTrack t) => Penalty.GetValueOrDefault(MainArtist(t)) >= 2;

        /// <summary>Случайный индекс по весам (вес ≤ 0 не выпадает). -1 — выбирать не из чего.</summary>
        public int PickIndex(IReadOnlyList<double> weights)
        {
            var total = weights.Sum(w => Math.Max(0, w));
            if (total <= 0) return -1;
            var x = Rng.NextDouble() * total;
            for (var i = 0; i < weights.Count; i++)
            {
                x -= Math.Max(0, weights[i]);
                if (x <= 0 && weights[i] > 0) return i;
            }
            for (var i = weights.Count - 1; i >= 0; i--)
                if (weights[i] > 0) return i;
            return -1;
        }

        /// <summary>
        /// Собирает пачку из знакомого и нового: не больше двух треков одного артиста, без уже звучавших
        /// и без «забаненных» пропусками артистов. Знакомое вставляется через равные промежутки.
        /// </summary>
        public List<YaTrack> Compose(IEnumerable<YaTrack> fresh, int size)
        {
            var perArtist = new Dictionary<string, int>();
            bool Take(YaTrack t, bool discovery)
            {
                if (!t.Available || t.Id.Length == 0 || Seen.Contains(t.Id) || Blocked(t)) return false;
                if (discovery && ExcludeLiked && LikedIds.Contains(t.Id)) return false;
                var key = MainArtist(t);
                if (perArtist.GetValueOrDefault(key) >= 2) return false;
                perArtist[key] = perArtist.GetValueOrDefault(key) + 1;
                Seen.Add(t.Id);
                return true;
            }

            var familiarTarget = (int)Math.Round(size * Math.Clamp(FamiliarRatio, 0, 1));
            var familiar = new List<YaTrack>();
            foreach (var t in FamiliarPool.OrderBy(_ => Rng.Next()))
            {
                if (familiar.Count >= familiarTarget) break;
                if (Take(t, discovery: false)) familiar.Add(t);
            }

            var discovered = new List<YaTrack>();
            foreach (var t in fresh)
            {
                if (discovered.Count >= size - familiar.Count) break;
                if (Take(t, discovery: true)) discovered.Add(t);
            }

            // нового не хватило — добираем знакомым, чтобы волна не обрывалась
            foreach (var t in FamiliarPool.OrderBy(_ => Rng.Next()))
            {
                if (familiar.Count + discovered.Count >= size) break;
                if (Take(t, discovery: false)) familiar.Add(t);
            }

            // перемешиваем: знакомое — через равные промежутки среди нового
            var result = new List<YaTrack>(discovered);
            if (familiar.Count > 0)
            {
                var step = Math.Max(1, (result.Count + familiar.Count) / familiar.Count);
                for (var i = 0; i < familiar.Count; i++)
                    result.Insert(Math.Min(result.Count, i * step + Rng.Next(0, Math.Max(1, step / 2))), familiar[i]);
            }
            return result;
        }
    }

    public static class SpotifyMixes
    {
        /// <summary>Стабильное в течение дня зерно: микс не меняется при каждом открытии, а завтра — новый.</summary>
        public static int DaySeed(DateTime now, string mixId) =>
            mixId.Aggregate(now.Year * 400 + now.DayOfYear, (h, c) => unchecked(h * 31 + c));

        /// <summary>Какие миксы показать, исходя из того, что о вкусе известно.</summary>
        public static List<SpotifyMix> Catalog(SpotifyTaste taste)
        {
            var list = new List<SpotifyMix>
            {
                new("daily", "Ежедневный микс", "Любимое вперемешку с новым · обновляется каждый день", ""),
                new("discover", "Открытия", "Новые для тебя треки по твоим жанрам и артистам", ""),
            };
            if (taste.TopShort.Count > 0) list.Add(new("top-short", "Твой топ месяца", "Что ты слушаешь последние 4 недели", ""));
            if (taste.Recent.Count > 0) list.Add(new("recent", "Недавно играли", "Последнее, что звучало в Spotify", ""));
            if (taste.TopLong.Count > 0) list.Add(new("top-long", "Любимое за всё время", "Треки, которые с тобой дольше всех", ""));
            foreach (var (genre, _) in taste.Genres().Take(3))
                list.Add(new("genre:" + genre, "Микс: " + Capitalize(genre), "Знакомое и новое в этом жанре", ""));
            foreach (var (id, name, _) in taste.ArtistWeights().Take(3))
                list.Add(new("artist:" + id, name + " и похожие", "Артист, его соавторы и близкие по жанру", ""));
            return list;
        }

        internal static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
