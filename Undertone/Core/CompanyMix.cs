using System;
using System.Collections.Generic;
using System.Linq;

namespace Undertone.Core
{
    /// <summary>
    /// «Микс компании»: общие любимые треки + понемногу из вкуса каждого друга + чуть-чуть своего.
    /// Один и тот же день — один и тот же микс (seed от даты), назавтра — новый.
    /// </summary>
    public static class CompanyMix
    {
        public sealed record Result(List<string> Ids, int Shared, int FromFriends);

        /// <param name="mine">Свои лайки (id:albumId).</param>
        /// <param name="friends">Лайки друзей: участник → его лайки.</param>
        public static Result Build(IReadOnlyList<string> mine, IReadOnlyDictionary<string, List<string>> friends, int size, int seed)
        {
            static string Id(string likeId) => likeId.Split(':')[0];
            var rng = new Random(seed);
            var full = new Dictionary<string, string>(); // id → id:albumId
            var fans = new Dictionary<string, int>();
            void Count(IEnumerable<string> list)
            {
                foreach (var like in list.Distinct())
                {
                    var id = Id(like);
                    full.TryAdd(id, like);
                    fans[id] = fans.GetValueOrDefault(id) + 1;
                }
            }
            Count(mine);
            foreach (var list in friends.Values) Count(list);

            var chosen = new List<string>();
            var used = new HashSet<string>();
            void Take(string id)
            {
                if (used.Add(id)) chosen.Add(full[id]);
            }

            // 1) общее — то, что любят хотя бы двое; чем больше фанатов, тем выше шанс попасть
            var shared = fans.Where(kv => kv.Value >= 2).Select(kv => kv.Key)
                .OrderByDescending(id => fans[id] + rng.NextDouble()).Take((int)(size * 0.4)).ToList();
            shared.ForEach(Take);
            var sharedCount = chosen.Count;

            // 2) открытия: по очереди у каждого друга то, чего у нас нет
            var mineIds = mine.Select(Id).ToHashSet();
            var pools = friends.Values
                .Select(l => l.Select(Id).Where(id => !mineIds.Contains(id)).Distinct().OrderBy(_ => rng.Next()).ToList())
                .Where(p => p.Count > 0).ToList();
            var target = (int)(size * 0.85);
            for (int round = 0; chosen.Count < target && pools.Any(p => p.Count > round); round++)
                foreach (var pool in pools)
                    if (round < pool.Count && chosen.Count < target) Take(pool[round]);
            var fromFriends = chosen.Count - sharedCount;

            // 3) своё — добить до размера
            foreach (var id in mine.Select(Id).Distinct().OrderBy(_ => rng.Next()))
            {
                if (chosen.Count >= size) break;
                Take(id);
            }

            // перемешиваем, чтобы общие и чужие не шли кучками
            var ids = chosen.OrderBy(_ => rng.Next()).ToList();
            return new Result(ids, sharedCount, fromFriends);
        }

        /// <summary>Seed на сегодня: день один — микс один.</summary>
        public static int TodaySeed(DateTime now) => now.Year * 1000 + now.DayOfYear;
    }
}
