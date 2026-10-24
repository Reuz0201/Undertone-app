using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Undertone.Core
{
    public sealed record RecapTrack(YaTrack Track, int Plays, int Minutes);

    public sealed record RecapArtist(string Name, int Plays, int Minutes, YaTrack TopTrack);

    /// <summary>Период для итогов: месяц, год или всё время.</summary>
    public sealed record RecapPeriod(string Id, string Title, DateTime From, DateTime To)
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public static RecapPeriod Month(DateTime anyDay)
        {
            var from = new DateTime(anyDay.Year, anyDay.Month, 1);
            var name = Ru.DateTimeFormat.MonthNames[anyDay.Month - 1]; // «октябрь»
            return new RecapPeriod($"m{from:yyyy-MM}", $"{char.ToUpper(name[0])}{name[1..]} {anyDay.Year}", from, from.AddMonths(1));
        }

        public static RecapPeriod Year(int year) =>
            new($"y{year}", $"{year} год", new DateTime(year, 1, 1), new DateTime(year + 1, 1, 1));

        public static readonly RecapPeriod AllTime = new("all", "Всё время", DateTime.MinValue, DateTime.MaxValue);

        public bool Contains(DateTime t) => t >= From && t < To;
    }

    /// <summary>
    /// «Итоги» как Spotify Wrapped: минуты, топ треков и артистов, время суток, рекордный день, серия дней подряд.
    /// Месяц и год считаются по журналу прослушиваний (точно), «всё время» — по статистике (PlayStats),
    /// которая копилась и до появления журнала; там минуты примерные (прослушивания × длительность).
    /// </summary>
    public sealed class Recap
    {
        public RecapPeriod Period { get; init; } = RecapPeriod.AllTime;
        public int Minutes { get; init; }
        public int Plays { get; init; }
        public int DistinctTracks { get; init; }
        public int DistinctArtists { get; init; }
        /// <summary>Треки, впервые сыгранные в этом периоде.</summary>
        public int NewTracks { get; init; }
        public IReadOnlyList<RecapTrack> TopTracks { get; init; } = Array.Empty<RecapTrack>();
        public IReadOnlyList<RecapArtist> TopArtists { get; init; } = Array.Empty<RecapArtist>();
        /// <summary>Минуты по часам суток (0..23).</summary>
        public int[] Hours { get; init; } = new int[24];
        public DateTime? BestDay { get; init; }
        public int BestDayMinutes { get; init; }
        /// <summary>Самая длинная серия дней подряд с музыкой.</summary>
        public int Streak { get; init; }
        public int ActiveDays { get; init; }
        /// <summary>false — «всё время» по статистике: дни и серии неизвестны, минуты примерные.</summary>
        public bool Exact { get; init; }

        public bool IsEmpty => Plays == 0;

        public const int Top = 5;

        public static Recap FromLog(IEnumerable<PlayEvent> log, RecapPeriod period,
            Func<string, YaTrack?> lookup, Func<string, DateTime?> firstPlayed)
        {
            var events = log.Where(e => period.Contains(e.At) && e.Seconds >= PlayLog.MinSeconds).ToList();
            var hours = new int[24];
            var trackSec = new Dictionary<string, (int Plays, int Seconds)>();
            var daySec = new Dictionary<DateTime, int>();
            foreach (var e in events)
            {
                trackSec.TryGetValue(e.TrackId, out var t);
                trackSec[e.TrackId] = (t.Plays + 1, t.Seconds + e.Seconds);
                daySec.TryGetValue(e.At.Date, out var d);
                daySec[e.At.Date] = d + e.Seconds;
                SpreadOverHours(hours, e.At, e.Seconds);
            }

            var tracks = trackSec
                .Select(kv => (Track: lookup(kv.Key), kv.Value.Plays, kv.Value.Seconds))
                .Where(x => x.Track != null)
                .Select(x => new RecapTrack(x.Track!, x.Plays, (int)Math.Round(x.Seconds / 60.0)))
                .ToList();

            var best = daySec.Count == 0 ? default : daySec.MaxBy(kv => kv.Value);
            var totalSec = events.Sum(e => (long)e.Seconds);
            return new Recap
            {
                Period = period,
                Minutes = (int)(totalSec / 60),
                Plays = events.Count,
                DistinctTracks = trackSec.Count,
                DistinctArtists = tracks.SelectMany(t => t.Track.Artists).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                NewTracks = trackSec.Keys.Count(id => firstPlayed(id) is { } f && period.Contains(f)),
                TopTracks = RankTracks(tracks),
                TopArtists = RankArtists(tracks),
                Hours = hours.Select(s => s / 60).ToArray(),
                BestDay = daySec.Count == 0 ? null : best.Key,
                BestDayMinutes = best.Value / 60,
                Streak = LongestStreak(daySec.Keys),
                ActiveDays = daySec.Count,
                Exact = true,
            };
        }

        public static Recap FromStats(PlayStats stats)
        {
            var tracks = stats.Tracks.Values
                .Where(s => s.Plays > 0 && s.Track.Id.Length > 0)
                .Select(s => new RecapTrack(s.Track, s.Plays, (int)Math.Round(s.Plays * s.Track.Duration.TotalMinutes)))
                .ToList();
            var hours = new int[24];
            foreach (var s in stats.Tracks.Values)
                if (s.HourCounts is { Length: 24 } h)
                    for (int i = 0; i < 24; i++)
                        hours[i] += (int)Math.Round(h[i] * s.Track.Duration.TotalMinutes);
            return new Recap
            {
                Period = RecapPeriod.AllTime,
                Minutes = tracks.Sum(t => t.Minutes),
                Plays = tracks.Sum(t => t.Plays),
                DistinctTracks = tracks.Count,
                DistinctArtists = tracks.SelectMany(t => t.Track.Artists).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                NewTracks = tracks.Count,
                TopTracks = RankTracks(tracks),
                TopArtists = RankArtists(tracks),
                Hours = hours,
                Exact = false,
            };
        }

        private static IReadOnlyList<RecapTrack> RankTracks(IEnumerable<RecapTrack> tracks) =>
            tracks.OrderByDescending(t => t.Plays).ThenByDescending(t => t.Minutes).ThenBy(t => t.Track.Title, StringComparer.Ordinal)
                .Take(Top).ToList();

        /// <summary>Артисты — по минутам (честнее, чем по числу треков); у трека на двоих минуты идут обоим.</summary>
        private static IReadOnlyList<RecapArtist> RankArtists(IEnumerable<RecapTrack> tracks) =>
            tracks.SelectMany(t => t.Track.Artists.Select(a => (Artist: a, t)))
                .GroupBy(x => x.Artist, StringComparer.OrdinalIgnoreCase)
                .Select(g => new RecapArtist(g.First().Artist, g.Sum(x => x.t.Plays), g.Sum(x => x.t.Minutes),
                    g.OrderByDescending(x => x.t.Plays).First().t.Track))
                .OrderByDescending(a => a.Minutes).ThenByDescending(a => a.Plays).ThenBy(a => a.Name, StringComparer.Ordinal)
                .Take(Top).ToList();

        /// <summary>Длинный трек, начатый в 23:58, — большая часть его минут уже в следующем часе.</summary>
        private static void SpreadOverHours(int[] hours, DateTime start, int seconds)
        {
            var t = start;
            var left = seconds;
            while (left > 0)
            {
                var toNextHour = (int)Math.Ceiling((t.Date.AddHours(t.Hour + 1) - t).TotalSeconds);
                var chunk = Math.Min(left, Math.Max(1, toNextHour));
                hours[t.Hour] += chunk;
                left -= chunk;
                t = t.AddSeconds(chunk);
            }
        }

        internal static int LongestStreak(IEnumerable<DateTime> days)
        {
            var sorted = days.Select(d => d.Date).Distinct().OrderBy(d => d).ToList();
            int best = 0, run = 0;
            DateTime? prev = null;
            foreach (var d in sorted)
            {
                run = prev is { } p && (d - p).TotalDays == 1 ? run + 1 : 1;
                best = Math.Max(best, run);
                prev = d;
            }
            return best;
        }

        /// <summary>Главный час (0..23) — середина самого «музыкального» трёхчасового окна.</summary>
        public int PeakHour()
        {
            int bestHour = 0, bestSum = -1;
            for (int h = 0; h < 24; h++)
            {
                var sum = Hours[(h + 23) % 24] + Hours[h] + Hours[(h + 1) % 24];
                if (sum > bestSum) { bestSum = sum; bestHour = h; }
            }
            return bestHour;
        }

        /// <summary>Кто ты по времени суток.</summary>
        public (string Title, string Text) Chronotype()
        {
            var h = PeakHour();
            return h switch
            {
                >= 5 and < 11 => ("Жаворонок", "Твоя музыка просыпается вместе с тобой — больше всего ты слушаешь утром."),
                >= 11 and < 17 => ("Дневной слушатель", "Музыка — твой саундтрек к делам: пик приходится на день."),
                >= 17 and < 23 => ("Вечерний мечтатель", "Вечер — твоё время: больше всего музыки после пяти."),
                _ => ("Ночная сова", "Пока все спят, у тебя играет музыка. Пик — глубокой ночью."),
            };
        }

        /// <summary>Однолюб или исследователь — по тому, сколько раз в среднем звучит один трек.</summary>
        public (string Title, string Text) Taste()
        {
            if (DistinctTracks == 0) return ("", "");
            var repeat = Plays / (double)DistinctTracks;
            if (repeat >= 3) return ("Однолюб", $"В среднем каждый трек звучал {repeat:0.#} раза. Нашёл своё — и на повтор.");
            if (NewTracks >= DistinctTracks * 0.6 && Exact)
                return ("Исследователь", $"{NewTracks} {Plural(NewTracks, "трек", "трека", "треков")} ты услышал впервые. Вечно в поиске нового.");
            return ("Золотая середина", "Любимое на повторе, но и новому место находится.");
        }

        /// <summary>Сравнение минут с чем-нибудь понятным.</summary>
        public string MinutesComparison()
        {
            var hours = Minutes / 60.0;
            if (hours >= 48) return $"Это {hours / 24:0.#} суток без перерыва.";
            if (hours >= 3) return $"Это примерно {Math.Round(hours / 2.2)} {Plural((int)Math.Round(hours / 2.2), "фильм", "фильма", "фильмов")} подряд.";
            if (Minutes >= 1) return $"Это {Math.Max(1, Minutes / 4)} {Plural(Math.Max(1, Minutes / 4), "трек", "трека", "треков")} по 4 минуты.";
            return "Всё только начинается.";
        }

        internal static string Plural(int n, string one, string few, string many)
        {
            n = Math.Abs(n) % 100;
            if (n is >= 11 and <= 14) return many;
            return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
        }
    }
}
