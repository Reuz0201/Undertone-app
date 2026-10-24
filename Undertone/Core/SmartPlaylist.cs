using System;
using System.Collections.Generic;
using System.Linq;

namespace Undertone.Core
{
    public enum SmartSource { Liked, Played, Local, All }
    public enum DayPart { Any, Night, Morning, Day, Evening }
    public enum SmartSort { Random, MostPlayed, RecentlyPlayed, LongAgo, Newest }

    /// <summary>Умный плейлист: набор правил, по которым треки отбираются заново при каждом открытии.</summary>
    public sealed class SmartPlaylist
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
        public string Name { get; set; } = "Новая подборка";
        public SmartSource Source { get; set; } = SmartSource.Liked;
        /// <summary>Не играл N дней (0 — не важно).</summary>
        public int NotPlayedDays { get; set; }
        /// <summary>Играл хотя бы N раз (0 — не важно).</summary>
        public int MinPlays { get; set; }
        /// <summary>Играл за последние N дней (0 — не важно).</summary>
        public int PlayedWithinDays { get; set; }
        /// <summary>Чаще всего слушаю в это время суток.</summary>
        public DayPart DayPart { get; set; }
        public string ArtistContains { get; set; } = "";
        public SmartSort Sort { get; set; } = SmartSort.Random;
        public int Limit { get; set; } = 50;

        public string Describe()
        {
            var parts = new List<string>
            {
                Source switch { SmartSource.Liked => "из лайков", SmartSource.Played => "из прослушанного", SmartSource.Local => "из своих файлов", _ => "из всего" },
            };
            if (NotPlayedDays > 0) parts.Add($"не играли {NotPlayedDays} дн.");
            if (PlayedWithinDays > 0) parts.Add($"играли за {PlayedWithinDays} дн.");
            if (MinPlays > 0) parts.Add($"от {MinPlays} раз");
            if (DayPart != DayPart.Any)
                parts.Add(DayPart switch { DayPart.Night => "ночью", DayPart.Morning => "утром", DayPart.Day => "днём", _ => "вечером" });
            if (ArtistContains.Trim().Length > 0) parts.Add($"артист «{ArtistContains.Trim()}»");
            return string.Join(" · ", parts);
        }

        public static List<SmartPlaylist> Defaults() => new()
        {
            new() { Name = "Забытые лайки", Source = SmartSource.Liked, NotPlayedDays = 30, Sort = SmartSort.Random, Limit = 50 },
            new() { Name = "На повторе", Source = SmartSource.Played, PlayedWithinDays = 14, MinPlays = 3, Sort = SmartSort.MostPlayed, Limit = 40 },
            new() { Name = "Ночное", Source = SmartSource.All, DayPart = DayPart.Night, MinPlays = 2, Sort = SmartSort.MostPlayed, Limit = 50 },
            new() { Name = "Свежие лайки", Source = SmartSource.Liked, Sort = SmartSort.Newest, Limit = 50 },
        };
    }

    /// <summary>Отбор треков по правилам. Чистая функция — проверяется тестами.</summary>
    public static class SmartEngine
    {
        public static bool InPart(int hour, DayPart part) => part switch
        {
            DayPart.Night => hour >= 0 && hour < 5 || hour >= 23,
            DayPart.Morning => hour >= 5 && hour < 11,
            DayPart.Day => hour >= 11 && hour < 18,
            DayPart.Evening => hour >= 18 && hour < 23,
            _ => true,
        };

        /// <param name="liked">Лайки, свежие первыми.</param>
        public static List<YaTrack> Evaluate(SmartPlaylist rule, IReadOnlyList<YaTrack> liked, IReadOnlyList<YaTrack> local,
            PlayStats stats, DateTime now, int seed)
        {
            // источник; у лайков помним порядок — «новые» = выше в списке
            var likedRank = new Dictionary<string, int>();
            for (int i = 0; i < liked.Count; i++) likedRank.TryAdd(liked[i].Id, i);
            IEnumerable<YaTrack> pool = rule.Source switch
            {
                SmartSource.Liked => liked,
                SmartSource.Played => stats.Tracks.Values.Select(s => s.Track),
                SmartSource.Local => local,
                _ => liked.Concat(local).Concat(stats.Tracks.Values.Select(s => s.Track)),
            };
            var seen = new HashSet<string>();
            var list = new List<(YaTrack T, TrackStat? S)>();
            foreach (var t in pool)
            {
                if (t.Id.Length == 0 || !t.Available || !seen.Add(t.Id)) continue;
                var s = stats.Get(t.Id);
                if (rule.NotPlayedDays > 0 && s != null && (now - s.Last).TotalDays < rule.NotPlayedDays) continue;
                if (rule.PlayedWithinDays > 0 && (s == null || (now - s.Last).TotalDays > rule.PlayedWithinDays)) continue;
                if (rule.MinPlays > 0 && (s?.Plays ?? 0) < rule.MinPlays) continue;
                if (rule.DayPart != DayPart.Any)
                {
                    // «ночное» — больше половины прослушиваний пришлось на это время суток
                    var hours = s?.HourCounts;
                    if (hours == null) continue;
                    var inPart = Enumerable.Range(0, 24).Where(h => InPart(h, rule.DayPart)).Sum(h => hours[h]);
                    if (inPart * 2 < hours.Sum() || inPart == 0) continue;
                }
                var artist = rule.ArtistContains.Trim();
                if (artist.Length > 0 && !t.ArtistsText.Contains(artist, StringComparison.CurrentCultureIgnoreCase)) continue;
                list.Add((t, s));
            }

            var rng = new Random(seed);
            IEnumerable<(YaTrack T, TrackStat? S)> sorted = rule.Sort switch
            {
                SmartSort.MostPlayed => list.OrderByDescending(x => x.S?.Plays ?? 0).ThenByDescending(x => x.S?.Last),
                SmartSort.RecentlyPlayed => list.OrderByDescending(x => x.S?.Last ?? DateTime.MinValue),
                SmartSort.LongAgo => list.OrderBy(x => x.S?.Last ?? DateTime.MinValue),
                SmartSort.Newest => list.OrderBy(x => likedRank.TryGetValue(x.T.Id, out var r) ? r : int.MaxValue),
                _ => list.OrderBy(_ => rng.Next()),
            };
            return sorted.Take(Math.Clamp(rule.Limit, 1, 500)).Select(x => x.T).ToList();
        }
    }
}
