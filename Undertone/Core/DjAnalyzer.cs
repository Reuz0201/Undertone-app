using System;
using System.Collections.Generic;
using System.Linq;

namespace Undertone.Core
{
    /// <summary>
    /// Что DJ-режим знает о треке: темп и сетка битов, тональность, громкость по ходу трека.
    /// Считается один раз по скачанному файлу и хранится в <see cref="DjStore"/>.
    /// Все времена — в секундах от начала файла.
    /// </summary>
    public sealed class TrackAnalysis
    {
        /// <summary>Версия алгоритма: поменялась — старые результаты пересчитываются.</summary>
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public double Duration { get; set; }
        /// <summary>Темп; 0 — бита нет (эмбиент, речь) — такой трек сводим простым кроссфейдом.</summary>
        public double Bpm { get; set; }
        /// <summary>Насколько уверенно найден бит, 0..1.</summary>
        public double BeatConfidence { get; set; }
        /// <summary>Время какой-нибудь сильной доли (первой доли такта). Остальные — через 4 удара.</summary>
        public double Downbeat { get; set; }
        /// <summary>Тональность, 0..11 от до; -1 — не определена.</summary>
        public int KeyRoot { get; set; } = -1;
        public bool KeyMinor { get; set; }
        /// <summary>Средняя громкость звучащей части (RMS), чтобы не сводить тихое с громким вслепую.</summary>
        public double Loudness { get; set; }
        /// <summary>Где заканчивается тишина в начале.</summary>
        public double FirstSound { get; set; }
        /// <summary>После этого места трек затихает (аутро, затухание) — переход лучше закончить около него.</summary>
        public double LastLoud { get; set; }

        public bool HasBeat => Bpm > 0;
        public double BeatSeconds => HasBeat ? 60 / Bpm : 0;
        public double BarSeconds => BeatSeconds * 4;

        /// <summary>Тональность по колесу Камелота («8A», «11B»), пусто — не определена.</summary>
        public string Camelot => KeyRoot < 0 ? "" : Undertone.Core.Camelot.Of(KeyRoot, KeyMinor);

        /// <summary>Сильная доля не раньше t (первая при t ≤ первой доли).</summary>
        public double DownbeatAtOrAfter(double t)
        {
            if (!HasBeat) return t;
            var bar = BarSeconds;
            var k = Math.Ceiling((t - Downbeat) / bar - 1e-6);
            return Downbeat + k * bar;
        }

        /// <summary>Сильная доля не позже t.</summary>
        public double DownbeatAtOrBefore(double t)
        {
            if (!HasBeat) return t;
            var bar = BarSeconds;
            var k = Math.Floor((t - Downbeat) / bar + 1e-6);
            return Downbeat + k * bar;
        }
    }

    /// <summary>Колесо Камелота: тональности, которые хорошо сводятся, стоят рядом.</summary>
    public static class Camelot
    {
        private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>Номер на колесе 1..12.</summary>
        public static int Number(int root, bool minor)
        {
            var major = minor ? (root + 3) % 12 : root; // минор стоит на одном номере с параллельным мажором
            return ((7 * major) % 12 + 7) % 12 + 1;
        }

        public static string Of(int root, bool minor) => $"{Number(root, minor)}{(minor ? 'A' : 'B')}";

        public static string Name(int root, bool minor) => root < 0 ? "" : Names[root % 12] + (minor ? "m" : "");

        /// <summary>
        /// Насколько плохо звучит переход между тональностями: 0 — та же, 1 — соседняя (±1 или параллельная),
        /// 2 — через одну, 3 и больше — диссонанс. Неизвестная тональность — нейтральные 1.5.
        /// </summary>
        public static double Distance(int rootA, bool minorA, int rootB, bool minorB)
        {
            if (rootA < 0 || rootB < 0) return 1.5;
            var a = Number(rootA, minorA);
            var b = Number(rootB, minorB);
            var steps = Math.Abs(a - b);
            steps = Math.Min(steps, 12 - steps);
            if (minorA == minorB) return steps;
            return steps == 0 ? 1 : steps + 1.5;
        }
    }

    /// <summary>
    /// Анализ трека для сведения: темп и сетка (спектральный поток + автокорреляция + гребёнка),
    /// сильная доля (по басу), тональность (хрома и профили Крумхансла), тишина в начале и затухание в конце.
    /// Работает с моно-сигналом любой частоты; удобнее всего 22050 Гц.
    /// </summary>
    public static class DjAnalyzer
    {
        public const int PreferredSampleRate = 22050;

        private const int FrameSize = 1024;
        private const double MinBpm = 60, MaxBpm = 200;

        public static TrackAnalysis Analyze(float[] mono, int sampleRate)
        {
            var result = new TrackAnalysis { Duration = mono.Length / (double)sampleRate };
            if (mono.Length < sampleRate * 4) return result; // меньше 4 секунд — нечего сводить

            var hop = Math.Max(1, sampleRate / 100); // ~100 кадров в секунду
            var fps = sampleRate / (double)hop;
            var features = Spectral(mono, sampleRate, hop);

            Levels(features.Rms, fps, result);
            Tempo(features, fps, sampleRate, result);
            Key(mono, sampleRate, result);
            return result;
        }

        // ────────── спектральные признаки ──────────

        private sealed class Features
        {
            public double[] Flux = Array.Empty<double>();     // рост громкости по всем частотам
            public double[] LowFlux = Array.Empty<double>();  // рост в басу (бочка)
            public double[] LowEnergy = Array.Empty<double>(); // энергия баса
            public double[] Rms = Array.Empty<double>();
        }

        private static Features Spectral(float[] x, int sampleRate, int hop)
        {
            int frames = (x.Length - FrameSize) / hop;
            var f = new Features
            {
                Flux = new double[frames],
                LowFlux = new double[frames],
                LowEnergy = new double[frames],
                Rms = new double[frames],
            };
            var window = Hann(FrameSize);
            var re = new double[FrameSize];
            var im = new double[FrameSize];
            var half = FrameSize / 2;
            var prev = new double[half];
            var cur = new double[half];
            int lowBin = Math.Max(2, (int)(150.0 * FrameSize / sampleRate) + 1);
            int fluxLowBin = Math.Max(2, (int)(250.0 * FrameSize / sampleRate) + 1);

            for (int i = 0; i < frames; i++)
            {
                int start = i * hop;
                double sq = 0;
                for (int n = 0; n < FrameSize; n++)
                {
                    var s = x[start + n];
                    sq += s * s;
                    re[n] = s * window[n];
                    im[n] = 0;
                }
                f.Rms[i] = Math.Sqrt(sq / FrameSize);
                Fft.Transform(re, im);

                double flux = 0, lowFlux = 0, low = 0;
                for (int k = 1; k < half; k++)
                {
                    var mag2 = re[k] * re[k] + im[k] * im[k];
                    var mag = Math.Sqrt(mag2);
                    cur[k] = Math.Log(1 + 100 * mag);
                    if (k < lowBin) low += mag2;
                    if (i > 0)
                    {
                        var d = cur[k] - prev[k];
                        if (d > 0)
                        {
                            flux += d;
                            if (k < fluxLowBin) lowFlux += d;
                        }
                    }
                }
                f.Flux[i] = flux;
                f.LowFlux[i] = lowFlux;
                f.LowEnergy[i] = low;
                (prev, cur) = (cur, prev);
            }
            return f;
        }

        // ────────── темп и сетка ──────────

        private static void Tempo(Features f, double fps, int sampleRate, TrackAnalysis result)
        {
            var env = Novelty(f.Flux, fps);
            var lowEnv = Novelty(f.LowFlux, fps);
            int n = env.Length;
            if (n < fps * 4) return;

            // автокорреляция огибающей атак по задержкам 60..200 BPM, с мягким предпочтением ~120 BPM
            int minLag = (int)Math.Floor(fps * 60 / MaxBpm);
            int maxLag = (int)Math.Ceiling(fps * 60 / MinBpm);
            if (maxLag >= n / 2) return;
            var score = new double[maxLag + 2];
            double bestScore = 0;
            int bestLag = 0;
            double mean0 = 0;
            for (int i = 0; i < n; i++) mean0 += env[i] * env[i];
            mean0 /= n;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < n; i++) s += env[i] * env[i + lag];
                s /= n - lag;
                var bpm = 60 * fps / lag;
                var octaves = Math.Log2(bpm / 120);
                s *= Math.Exp(-0.5 * octaves * octaves / (0.9 * 0.9));
                score[lag] = s;
                if (s > bestScore)
                {
                    bestScore = s;
                    bestLag = lag;
                }
            }
            if (bestLag == 0 || mean0 <= 0) return;

            double lagF = bestLag;
            if (bestLag > minLag && bestLag < maxLag)
            {
                double y0 = score[bestLag - 1], y1 = score[bestLag], y2 = score[bestLag + 1];
                var denom = y0 - 2 * y1 + y2;
                if (Math.Abs(denom) > 1e-12) lagF += Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5);
            }
            var coarse = 60 * fps / lagF;

            // точный темп и фаза: гребёнка по всему треку (бас весит больше — это бочка, а не хэты на слабых долях)
            var phaseEnv = new double[n];
            for (int i = 0; i < n; i++) phaseEnv[i] = lowEnv[i] + 0.3 * env[i];
            double best = -1, bestBpm = coarse, bestPhase = 0;
            for (var bpm = coarse * 0.985; bpm <= coarse * 1.015; bpm += 0.01)
            {
                var period = 60 * fps / bpm;
                for (double phase = 0; phase < period; phase += 0.5)
                {
                    var s = Comb(phaseEnv, phase, period);
                    if (s > best)
                    {
                        best = s;
                        bestBpm = bpm;
                        bestPhase = phase;
                    }
                }
            }

            // уверенность: нормированная автокорреляция атак на найденном периоде (шум даёт ~0, бит — 0.5..1)
            var confidence = Periodicity(env, 60 * fps / bestBpm);
            if (confidence < 0.12) return; // бита нет — сводим как обычно

            // сильная доля: из четырёх сдвигов на долю выбираем тот, где бас сильнее
            var beatPeriod = 60 * fps / bestBpm;
            int bestK = 0;
            double bestLow = -1;
            for (int k = 0; k < 4; k++)
            {
                var s = Comb(f.LowEnergy, bestPhase + k * beatPeriod, beatPeriod * 4);
                if (s > bestLow)
                {
                    bestLow = s;
                    bestK = k;
                }
            }

            result.Bpm = Math.Round(bestBpm, 2);
            result.BeatConfidence = Math.Round(confidence, 3);
            // поток пика атаки приходится чуть раньше середины кадра — поправка подобрана на синтетике
            var frameShift = 0.8 * FrameSize / sampleRate;
            result.Downbeat = (bestPhase + bestK * beatPeriod) / fps + frameShift;
        }

        private static double Periodicity(double[] env, double period)
        {
            int n = env.Length;
            double mean = env.Average();
            double zero = 0;
            for (int i = 0; i < n; i++) zero += (env[i] - mean) * (env[i] - mean);
            zero /= n;
            if (zero <= 0) return 0;
            double best = 0;
            int center = (int)Math.Round(period);
            for (int lag = Math.Max(1, center - 1); lag <= center + 1 && lag < n / 2; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < n; i++) s += (env[i] - mean) * (env[i + lag] - mean);
                best = Math.Max(best, s / (n - lag));
            }
            return Math.Clamp(best / zero, 0, 1);
        }

        /// <summary>Атаки без медленного фона: поток минус скользящее среднее за полсекунды, нормировано к 1.</summary>
        private static double[] Novelty(double[] flux, double fps)
        {
            int n = flux.Length;
            var result = new double[n];
            int half = Math.Max(1, (int)(fps * 0.25));
            double sum = 0;
            int lo = 0, hi = -1;
            double max = 0;
            for (int i = 0; i < n; i++)
            {
                while (hi < Math.Min(n - 1, i + half)) sum += flux[++hi];
                while (lo < i - half) sum -= flux[lo++];
                var mean = sum / (hi - lo + 1);
                var v = Math.Max(0, flux[i] - mean);
                result[i] = v;
                if (v > max) max = v;
            }
            if (max > 0)
                for (int i = 0; i < n; i++) result[i] /= max;
            return result;
        }

        /// <summary>Средняя величина сигнала в точках phase, phase+period, … (с линейной интерполяцией).</summary>
        private static double Comb(double[] env, double phase, double period)
        {
            double s = 0;
            int count = 0;
            for (var p = phase; p < env.Length - 1; p += period)
            {
                int i = (int)p;
                var frac = p - i;
                s += env[i] * (1 - frac) + env[i + 1] * frac;
                count++;
            }
            return count == 0 ? 0 : s / count;
        }

        // ────────── громкость: тишина в начале и затухание в конце ──────────

        private static void Levels(double[] rms, double fps, TrackAnalysis result)
        {
            int n = rms.Length;
            if (n == 0) return;
            // сглаживаем за секунду
            int half = Math.Max(1, (int)(fps / 2));
            var smooth = new double[n];
            double sum = 0;
            int lo = 0, hi = -1;
            for (int i = 0; i < n; i++)
            {
                while (hi < Math.Min(n - 1, i + half)) sum += rms[++hi];
                while (lo < i - half) sum -= rms[lo++];
                smooth[i] = sum / (hi - lo + 1);
            }
            var sorted = (double[])smooth.Clone();
            Array.Sort(sorted);
            var reference = sorted[(int)(n * 0.7)];
            if (reference <= 1e-5)
            {
                result.LastLoud = result.Duration;
                return;
            }

            int first = 0;
            while (first < n - 1 && smooth[first] < reference * 0.1) first++;
            int last = n - 1;
            while (last > first && smooth[last] < reference * 0.5) last--;

            double loud = 0;
            int count = 0;
            for (int i = first; i <= last; i++)
            {
                loud += rms[i] * rms[i];
                count++;
            }
            result.Loudness = count == 0 ? 0 : Math.Round(Math.Sqrt(loud / count), 5);
            result.FirstSound = Math.Max(0, first / fps);
            result.LastLoud = Math.Min(result.Duration, last / fps);
        }

        // ────────── тональность ──────────

        // профили Крумхансла — Кесслера: «насколько каждая ступень звучит своей» в мажоре и миноре
        private static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        private static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

        private static void Key(float[] x, int sampleRate, TrackAnalysis result)
        {
            const int size = 4096;
            int hop = size * 2; // для тональности хватит каждого второго окна — так вдвое быстрее
            if (x.Length < size * 4) return;
            var window = Hann(size);
            var re = new double[size];
            var im = new double[size];
            var chroma = new double[12];
            // для каждого бина — его нота (бины вне 55..2000 Гц пропускаем: там бочка и шум тарелок)
            var pitchClass = new int[size / 2];
            for (int k = 0; k < size / 2; k++)
            {
                var freq = k * (double)sampleRate / size;
                if (freq < 55 || freq > 2000) { pitchClass[k] = -1; continue; }
                var midi = 69 + 12 * Math.Log2(freq / 440);
                pitchClass[k] = ((int)Math.Round(midi) % 12 + 12) % 12;
            }

            for (int start = 0; start + size <= x.Length; start += hop)
            {
                for (int n = 0; n < size; n++)
                {
                    re[n] = x[start + n] * window[n];
                    im[n] = 0;
                }
                Fft.Transform(re, im);
                var frame = new double[12];
                double total = 0;
                for (int k = 1; k < size / 2; k++)
                {
                    var pc = pitchClass[k];
                    if (pc < 0) continue;
                    var mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                    frame[pc] += mag;
                    total += mag;
                }
                if (total <= 1e-9) continue;
                // каждое окно — с равным весом, чтобы громкий припев не перевешивал всё остальное
                for (int i = 0; i < 12; i++) chroma[i] += frame[i] / total;
            }

            if (chroma.Sum() <= 0) return;
            double best = double.NegativeInfinity, second = double.NegativeInfinity;
            int bestRoot = -1;
            bool bestMinor = false;
            for (int root = 0; root < 12; root++)
                foreach (var minor in new[] { false, true })
                {
                    var r = Correlate(chroma, minor ? MinorProfile : MajorProfile, root);
                    if (r > best)
                    {
                        second = best;
                        best = r;
                        bestRoot = root;
                        bestMinor = minor;
                    }
                    else if (r > second) second = r;
                }
            if (best < 0.3) return; // хрома ни на что не похожа — шум, речь
            result.KeyRoot = bestRoot;
            result.KeyMinor = bestMinor;
        }

        private static double Correlate(double[] chroma, double[] profile, int root)
        {
            double ma = chroma.Average(), mb = profile.Average();
            double num = 0, da = 0, db = 0;
            for (int i = 0; i < 12; i++)
            {
                var a = chroma[(i + root) % 12] - ma;
                var b = profile[i] - mb;
                num += a * b;
                da += a * a;
                db += b * b;
            }
            return da <= 0 || db <= 0 ? 0 : num / Math.Sqrt(da * db);
        }

        private static double[] Hann(int size)
        {
            var w = new double[size];
            for (int i = 0; i < size; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1));
            return w;
        }
    }

    /// <summary>Быстрое преобразование Фурье (радикс-2, на месте). Длина — степень двойки.</summary>
    public static class Fft
    {
        public static void Transform(double[] re, double[] im)
        {
            int n = re.Length;
            if (n != im.Length || (n & (n - 1)) != 0) throw new ArgumentException("Длина должна быть степенью двойки");
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                var ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        var tr = re[b] * cr - im[b] * ci;
                        var ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                        var ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
