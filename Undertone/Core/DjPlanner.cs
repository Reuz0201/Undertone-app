using System;
using System.Collections.Generic;

namespace Undertone.Core
{
    /// <summary>
    /// Как свести два трека. Времена — в секундах внутри файлов.
    /// С <see cref="MixOutAt"/> текущего трека начинает звучать следующий (с <see cref="NextStartAt"/>),
    /// через <see cref="Length"/> секунд текущий умолкает, следующий становится текущим.
    /// </summary>
    public sealed record TransitionPlan(
        double MixOutAt,
        double Length,
        double NextStartAt,
        double Rate,          // скорость следующего на время перехода (подгон темпа); после — плавно к 1
        bool BassSwap,        // бас уходящего меняется на бас нового посередине перехода
        bool BeatMatched)     // темп подогнан, вход — в такт
    {
        public double MixOutEnd => MixOutAt + Length;
    }

    /// <summary>Длина DJ-перехода.</summary>
    public enum DjBlend { Short = 4, Medium = 8, Long = 16 }

    /// <summary>Подбирает точки и параметры перехода по анализу обоих треков. Чистая логика — тестируется без звука.</summary>
    public static class DjPlanner
    {
        /// <summary>Сильнее не разгоняем и не тормозим: дальше слышно, что песня «поплыла».</summary>
        public const double MaxRateChange = 0.08;

        /// <summary>Длина перехода без бита (или если бит не найден) — секунд.</summary>
        public const double PlainFadeSeconds = 8;

        /// <summary>
        /// Во сколько раз ускорить следующий трек, чтобы его бит совпал с текущим. Темп одного из треков
        /// мог определиться вдвое (87 вместо 174) — сравниваем и с половиной, и с удвоением.
        /// null — темпы слишком разные, подгон будет слышен.
        /// </summary>
        public static double? MatchRate(double currentBpm, double nextBpm)
        {
            if (currentBpm <= 0 || nextBpm <= 0) return null;
            double best = double.MaxValue;
            foreach (var mul in new[] { 0.5, 1, 2 })
            {
                var rate = currentBpm / (nextBpm * mul);
                if (Math.Abs(Math.Log(rate)) < Math.Abs(Math.Log(best))) best = rate;
            }
            return Math.Abs(best - 1) <= MaxRateChange ? best : null;
        }

        /// <summary>
        /// План перехода. current — анализ играющего трека (его длина — duration), next — следующего
        /// (может быть null, если ещё не посчитан). position — где сейчас текущий: переход не может начаться раньше.
        /// </summary>
        public static TransitionPlan Plan(TrackAnalysis? current, double duration, TrackAnalysis? next, DjBlend blend,
            bool matchTempo, double position = 0)
        {
            if (current != null && current.Duration > 0) duration = Math.Min(duration, current.Duration);
            var nextStart = next != null ? Math.Max(0, next.FirstSound) : 0;
            var earliest = Math.Max(position + 1.5, duration * 0.35);

            // нет бита у одного из треков — обычный плавный переход, но с умными точками
            var rate = matchTempo && current is { HasBeat: true } && next is { HasBeat: true }
                ? MatchRate(current.Bpm, next.Bpm)
                : null;
            if (current == null || !current.HasBeat || rate == null)
                return Plain(current, duration, nextStart, earliest, beatsKnown: current?.HasBeat == true && next?.HasBeat == true);

            // переход в тактах текущего трека
            var bars = (int)blend;
            var bar = current.BarSeconds;
            var length = bars * bar;
            // уходящий трек в конце перехода уже почти не слышен — пусть переход заходит в затухание на четверть
            var endTarget = Math.Min(duration - 0.3, Math.Max(current.LastLoud, duration * 0.5) + length * 0.25);
            // если трек кончается раньше, чем влезает переход нужной длины — укорачиваем по тактам
            while (bars > 2 && endTarget - bars * bar < earliest) bars /= 2;
            length = bars * bar;
            var mixOut = current.DownbeatAtOrBefore(endTarget - length);
            if (mixOut < earliest) mixOut = current.DownbeatAtOrAfter(earliest);
            if (mixOut + length > duration - 0.05)
                return Plain(current, duration, nextStart, earliest, beatsKnown: true);

            // следующий заходит с сильной доли, где уже есть звук
            var nextIn = next!.DownbeatAtOrAfter(Math.Max(0, next.FirstSound - 0.05));
            // сильная доля «до начала» трека (сетка уходит в минус) — берём следующую
            if (nextIn < 0) nextIn += next.BarSeconds;
            // интро, которое длиннее всего трека, — не бывает; если вышло за половину — что-то не так с сеткой
            if (nextIn > next.Duration * 0.5) nextIn = nextStart;

            return new TransitionPlan(mixOut, length, nextIn, rate.Value, BassSwap: true, BeatMatched: true);
        }

        private static TransitionPlan Plain(TrackAnalysis? current, double duration, double nextStart, double earliest, bool beatsKnown)
        {
            var length = PlainFadeSeconds;
            var end = current != null && current.LastLoud > 0
                ? Math.Min(duration - 0.3, current.LastLoud + length * 0.4)
                : duration;
            var mixOut = Math.Max(earliest, end - length);
            if (mixOut + length > duration) length = Math.Max(1, duration - mixOut);
            // бит есть, но темпы далеко: переход покороче и тоже по сильной доле
            if (beatsKnown && current != null)
            {
                var aligned = current.DownbeatAtOrBefore(mixOut);
                if (aligned >= earliest) mixOut = aligned;
            }
            return new TransitionPlan(mixOut, length, nextStart, 1, BassSwap: beatsKnown, BeatMatched: false);
        }

        // ────────── умный порядок ──────────

        /// <summary>
        /// Насколько плохо next идёт после current: 0 — идеально; темп важнее всего, потом тональность,
        /// потом скачок громкости. Неизвестный анализ — средняя оценка.
        /// </summary>
        public static double Cost(TrackAnalysis current, TrackAnalysis next)
        {
            double tempo;
            if (!current.HasBeat || !next.HasBeat) tempo = current.HasBeat == next.HasBeat ? 1 : 3;
            else
            {
                var rate = current.Bpm / next.Bpm;
                var folded = new[] { rate, rate * 2, rate / 2 };
                var diff = double.MaxValue;
                foreach (var r in folded) diff = Math.Min(diff, Math.Abs(Math.Log(r)));
                var percent = diff * 100;
                tempo = percent <= 3 ? percent / 3 : percent <= 8 ? 1 + (percent - 3) / 2.5 : 3 + (percent - 8) / 4;
            }
            var key = Camelot.Distance(current.KeyRoot, current.KeyMinor, next.KeyRoot, next.KeyMinor);
            var loud = current.Loudness > 0 && next.Loudness > 0 ? Math.Abs(Math.Log(next.Loudness / current.Loudness)) : 0.3;
            return tempo * 1.0 + Math.Min(key, 4) * 0.6 + loud * 0.8;
        }

        /// <summary>
        /// Какой из ближайших треков очереди поставить следующим. candidates — по порядку очереди
        /// (null — ещё не проанализирован). Возвращает индекс; 0 — порядок менять не нужно.
        /// Переставляем только при заметном выигрыше: очередь — всё-таки выбор человека.
        /// </summary>
        public static int PickNext(TrackAnalysis current, IReadOnlyList<TrackAnalysis?> candidates)
        {
            if (candidates.Count == 0 || candidates[0] == null) return 0;
            var baseline = Cost(current, candidates[0]!);
            int best = 0;
            var bestCost = baseline;
            for (int i = 1; i < candidates.Count; i++)
            {
                if (candidates[i] is not { } c) continue;
                var cost = Cost(current, c) + 0.25 * i; // дальше по очереди — чуть дороже
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = i;
                }
            }
            return best != 0 && baseline - bestCost >= 0.8 ? best : 0;
        }
    }
}
