using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using Undertone.Audio;
using Undertone.Core;

namespace Undertone.Tests;

public class DjAnalyzerTests
{
    private const int Sr = DjAnalyzer.PreferredSampleRate;

    /// <summary>
    /// Синтетический «трек»: бочка на каждую долю (сильная громче), хэт на слабых, аккорды поверх.
    /// firstBeat — где первая сильная доля (после тишины в начале).
    /// </summary>
    internal static float[] Beat(double bpm, double seconds, double firstBeat, int[]? chord = null, double silence = 0,
        double fadeOut = 0, int seed = 1)
    {
        var rng = new Random(seed);
        int n = (int)(seconds * Sr);
        var x = new float[n];
        var beat = 60 / bpm;
        int k = 0;
        for (var t = firstBeat; t < seconds - 0.3; t += beat, k++)
        {
            var amp = k % 4 == 0 ? 1.0 : 0.7;
            int start = (int)(t * Sr);
            for (int i = 0; i < Sr / 4 && start + i < n; i++)
            {
                var tt = i / (double)Sr;
                x[start + i] += (float)(amp * Math.Sin(2 * Math.PI * (50 + 80 * Math.Exp(-tt * 30)) * tt) * Math.Exp(-tt * 12));
            }
            int hat = (int)((t + beat / 2) * Sr);
            for (int i = 0; i < Sr / 20 && hat + i < n; i++)
                x[hat + i] += (float)((rng.NextDouble() * 2 - 1) * 0.2 * Math.Exp(-i / (double)Sr * 80));
        }
        if (chord != null) AddChord(x, 0, n, chord, 0.08);
        for (int i = 0; i < Math.Min(n, (int)(silence * Sr)); i++) x[i] = 0;
        if (fadeOut > 0)
        {
            int fo = (int)(fadeOut * Sr);
            for (int i = 0; i < fo; i++) x[n - fo + i] *= 1 - i / (float)fo;
        }
        for (int i = 0; i < n; i++) x[i] += (float)((rng.NextDouble() * 2 - 1) * 0.002);
        return x;
    }

    /// <summary>Ноты (номера MIDI) с тремя гармониками — похоже на синтезатор, а не на чистый синус.</summary>
    private static void AddChord(float[] x, int from, int to, int[] midi, double amp)
    {
        foreach (var m in midi)
        {
            var f = 440 * Math.Pow(2, (m - 69) / 12.0);
            for (int i = from; i < to; i++)
            {
                var t = i / (double)Sr;
                x[i] += (float)(amp * (Math.Sin(2 * Math.PI * f * t) + 0.5 * Math.Sin(4 * Math.PI * f * t) + 0.25 * Math.Sin(6 * Math.PI * f * t)));
            }
        }
    }

    /// <summary>Расстояние от t до ближайшей точки сетки с шагом step и началом origin.</summary>
    private static double GridError(double t, double origin, double step)
    {
        var d = (t - origin) / step;
        return Math.Abs(d - Math.Round(d)) * step;
    }

    [Theory]
    [InlineData(124, 1.37)]
    [InlineData(87.5, 1.1)]
    [InlineData(140, 1.2)]
    [InlineData(128, 1.42)]
    public void Tempo_And_Downbeat_AreFound(double bpm, double firstBeat)
    {
        var a = DjAnalyzer.Analyze(Beat(bpm, 60, firstBeat), Sr);

        Assert.True(a.HasBeat);
        Assert.InRange(a.Bpm, bpm - 0.3, bpm + 0.3);
        // сильная доля — на сетке тактов исходника с точностью ~1/40 доли
        Assert.True(GridError(a.Downbeat, firstBeat, 4 * 60 / bpm) < 0.03,
            $"сильная доля {a.Downbeat:0.000}, ждали {firstBeat} + k·{4 * 60 / bpm:0.000}");
    }

    [Fact]
    public void Noise_HasNoBeat()
    {
        var rng = new Random(7);
        var x = new float[Sr * 30];
        for (int i = 0; i < x.Length; i++) x[i] = (float)((rng.NextDouble() * 2 - 1) * 0.1);
        var a = DjAnalyzer.Analyze(x, Sr);
        Assert.False(a.HasBeat);
    }

    [Fact]
    public void Silence_And_Fade_AreFound()
    {
        var a = DjAnalyzer.Analyze(Beat(120, 60, 2.0, silence: 2.0, fadeOut: 10), Sr);
        Assert.InRange(a.FirstSound, 1.2, 2.1);
        // затухание — последние 10 секунд: громко до ~55 с (половина громкости — на середине затухания)
        Assert.InRange(a.LastLoud, 49, 57);
        Assert.InRange(a.Duration, 59.9, 60.1);
    }

    [Fact]
    public void Key_MinorProgression()
    {
        // ля минор: Am – Dm – E (с соль-диезом) – Am
        int n = Sr * 24;
        var x = new float[n];
        var chords = new[] { new[] { 57, 60, 64 }, new[] { 62, 65, 69 }, new[] { 64, 68, 71 }, new[] { 57, 60, 64 } };
        for (int c = 0; c < 4; c++) AddChord(x, c * n / 4, (c + 1) * n / 4, chords[c], 0.1);
        var a = DjAnalyzer.Analyze(x, Sr);
        Assert.Equal(9, a.KeyRoot);
        Assert.True(a.KeyMinor);
        Assert.Equal("8A", a.Camelot);
    }

    [Fact]
    public void Key_MajorProgression()
    {
        // до мажор: C – F – G – C
        int n = Sr * 24;
        var x = new float[n];
        var chords = new[] { new[] { 60, 64, 67 }, new[] { 65, 69, 72 }, new[] { 67, 71, 74 }, new[] { 60, 64, 67 } };
        for (int c = 0; c < 4; c++) AddChord(x, c * n / 4, (c + 1) * n / 4, chords[c], 0.1);
        var a = DjAnalyzer.Analyze(x, Sr);
        Assert.Equal(0, a.KeyRoot);
        Assert.False(a.KeyMinor);
        Assert.Equal("8B", a.Camelot);
    }

    [Fact]
    public void TooShort_IsEmpty()
    {
        var a = DjAnalyzer.Analyze(new float[Sr], Sr);
        Assert.False(a.HasBeat);
        Assert.Equal(-1, a.KeyRoot);
    }

    [Fact]
    public void Fft_FindsSine()
    {
        const int size = 1024;
        var re = new double[size];
        var im = new double[size];
        for (int i = 0; i < size; i++) re[i] = Math.Sin(2 * Math.PI * 37 * i / size);
        Fft.Transform(re, im);
        var mags = Enumerable.Range(0, size / 2).Select(k => Math.Sqrt(re[k] * re[k] + im[k] * im[k])).ToArray();
        Assert.Equal(37, Array.IndexOf(mags, mags.Max()));
        Assert.Equal(size / 2.0, mags[37], 3);
    }

    [Fact]
    public void Downbeat_Grid()
    {
        var a = new TrackAnalysis { Bpm = 120, Downbeat = 0.5 }; // такт — 2 секунды
        Assert.Equal(10.5, a.DownbeatAtOrAfter(9), 6);
        Assert.Equal(8.5, a.DownbeatAtOrBefore(9), 6);
        Assert.Equal(8.5, a.DownbeatAtOrAfter(8.5), 6);
        Assert.Equal(0.5, a.DownbeatAtOrAfter(0), 6);
    }
}

public class CamelotTests
{
    [Theory]
    [InlineData(0, false, "8B")]  // C
    [InlineData(9, true, "8A")]   // Am
    [InlineData(7, false, "9B")]  // G
    [InlineData(5, false, "7B")]  // F
    [InlineData(4, true, "9A")]   // Em
    [InlineData(1, true, "12A")]  // C#m
    [InlineData(8, true, "1A")]   // G#m
    [InlineData(11, false, "1B")] // B
    public void Wheel(int root, bool minor, string expected) => Assert.Equal(expected, Camelot.Of(root, minor));

    [Fact]
    public void Distance()
    {
        Assert.Equal(0, Camelot.Distance(9, true, 9, true));   // Am → Am
        Assert.Equal(1, Camelot.Distance(9, true, 4, true));   // 8A → 9A
        Assert.Equal(1, Camelot.Distance(9, true, 0, false));  // Am → C (параллельные)
        Assert.Equal(1, Camelot.Distance(8, true, 1, true));   // 1A → 12A (через круг)
        Assert.True(Camelot.Distance(0, false, 6, false) >= 5); // C → F#: тритон, хуже некуда
        Assert.Equal(1.5, Camelot.Distance(-1, false, 0, false));
    }
}

public class DjPlannerTests
{
    private static TrackAnalysis Track(double bpm, double downbeat, double duration, double lastLoud, double firstSound = 0,
        int key = 9, bool minor = true, double loud = 0.2) => new()
    {
        Bpm = bpm, Downbeat = downbeat, Duration = duration, LastLoud = lastLoud, FirstSound = firstSound,
        KeyRoot = key, KeyMinor = minor, Loudness = loud, BeatConfidence = 0.8,
    };

    [Fact]
    public void MatchRate_FoldsOctaves()
    {
        Assert.Equal(128 / 126.0, DjPlanner.MatchRate(128, 126)!.Value, 6);
        Assert.Equal(1, DjPlanner.MatchRate(174, 87)!.Value, 6);
        Assert.Equal(1, DjPlanner.MatchRate(85, 170)!.Value, 6);
        Assert.Null(DjPlanner.MatchRate(128, 100));
        Assert.Null(DjPlanner.MatchRate(0, 120));
    }

    [Fact]
    public void BeatMatched_PlanSitsOnTheGrid()
    {
        var cur = Track(128, 0.5, 200, 190);
        var next = Track(126, 0.4, 210, 200, firstSound: 0.3);
        var plan = DjPlanner.Plan(cur, 200, next, DjBlend.Medium, matchTempo: true);

        Assert.True(plan.BeatMatched);
        Assert.True(plan.BassSwap);
        Assert.Equal(128 / 126.0, plan.Rate, 6);
        var bar = 4 * 60 / 128.0;
        Assert.Equal(8 * bar, plan.Length, 6);
        // начало перехода — на сильной доле текущего
        var bars = (plan.MixOutAt - 0.5) / bar;
        Assert.Equal(Math.Round(bars), bars, 6);
        // заканчивается до конца трека и недалеко от затухания
        Assert.True(plan.MixOutEnd <= 200);
        Assert.InRange(plan.MixOutEnd, 175, 200);
        // следующий входит с сильной доли, где уже есть звук
        Assert.Equal(0.4, plan.NextStartAt, 6);
    }

    [Fact]
    public void NextStart_SkipsSilenceOnTheGrid()
    {
        var cur = Track(120, 0, 180, 170);
        var next = Track(120, 0.25, 180, 170, firstSound: 3.1); // такт 2 с: доли 0.25, 2.25, 4.25…
        var plan = DjPlanner.Plan(cur, 180, next, DjBlend.Short, matchTempo: true);
        Assert.Equal(4.25, plan.NextStartAt, 6);
    }

    [Fact]
    public void FarTempos_FallBackToPlainFade()
    {
        var cur = Track(128, 0.5, 200, 190);
        var next = Track(90, 0.1, 200, 190, firstSound: 1.2);
        var plan = DjPlanner.Plan(cur, 200, next, DjBlend.Long, matchTempo: true);

        Assert.False(plan.BeatMatched);
        Assert.Equal(1, plan.Rate);
        Assert.Equal(1.2, plan.NextStartAt, 6);
        Assert.True(plan.MixOutEnd <= 200);
    }

    [Fact]
    public void NoTempoMatch_WhenTurnedOff()
    {
        var plan = DjPlanner.Plan(Track(128, 0.5, 200, 190), 200, Track(126, 0.4, 200, 190), DjBlend.Medium, matchTempo: false);
        Assert.False(plan.BeatMatched);
        Assert.Equal(1, plan.Rate);
    }

    [Fact]
    public void WithoutAnalysis_FadesAtTheEnd()
    {
        var plan = DjPlanner.Plan(null, 200, null, DjBlend.Medium, matchTempo: true);
        Assert.False(plan.BeatMatched);
        Assert.Equal(200 - DjPlanner.PlainFadeSeconds, plan.MixOutAt, 6);
        Assert.Equal(0, plan.NextStartAt);
    }

    [Fact]
    public void Plan_NeverStartsBeforePosition()
    {
        var cur = Track(128, 0.5, 200, 190);
        var next = Track(128, 0.5, 200, 190);
        var plan = DjPlanner.Plan(cur, 200, next, DjBlend.Long, matchTempo: true, position: 185);
        Assert.True(plan.MixOutAt >= 186.5 - 1e-9, $"{plan.MixOutAt}");
        Assert.True(plan.MixOutEnd <= 200);
    }

    [Fact]
    public void ShortTrack_ShrinksTransition()
    {
        var cur = Track(120, 0, 40, 38); // 40 секунд — 16 тактов (32 с) не влезут после 35%
        var next = Track(120, 0, 200, 190);
        var plan = DjPlanner.Plan(cur, 40, next, DjBlend.Long, matchTempo: true);
        Assert.True(plan.MixOutAt >= 40 * 0.35 - 1e-9);
        Assert.True(plan.MixOutEnd <= 40);
    }

    [Fact]
    public void PickNext_PrefersCompatibleTrack()
    {
        var cur = Track(128, 0, 200, 190, key: 9, minor: true);               // 8A
        var candidates = new List<TrackAnalysis?>
        {
            Track(95, 0, 200, 190, key: 6, minor: false),                       // далеко по темпу и тональности
            Track(127, 0, 200, 190, key: 9, minor: true),                       // почти идеально
            Track(129, 0, 200, 190, key: 4, minor: true),                       // 9A, тоже хорошо
        };
        Assert.Equal(1, DjPlanner.PickNext(cur, candidates));
    }

    [Fact]
    public void PickNext_KeepsOrderWhenFirstIsFine()
    {
        var cur = Track(128, 0, 200, 190);
        var candidates = new List<TrackAnalysis?> { Track(127, 0, 200, 190), Track(128, 0, 200, 190), null };
        Assert.Equal(0, DjPlanner.PickNext(cur, candidates));
        Assert.Equal(0, DjPlanner.PickNext(cur, new List<TrackAnalysis?> { null, Track(128, 0, 200, 190) }));
    }
}

public class DjDspTests
{
    private sealed class Sine : ISampleProvider
    {
        private readonly double _freq;
        private long _n;
        public Sine(double freq, int channels = 2) { _freq = freq; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, channels); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            var ch = WaveFormat.Channels;
            for (int i = 0; i < count / ch; i++, _n++)
                for (int c = 0; c < ch; c++) buffer[offset + i * ch + c] = (float)(0.5 * Math.Sin(2 * Math.PI * _freq * _n / 44100));
            return count / ch * ch;
        }
    }

    private sealed class Ramp : ISampleProvider
    {
        private readonly int _length;
        private int _n;
        public Ramp(int length) => _length = length;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int i = 0;
            for (; i < count && _n < _length; i++, _n++) buffer[offset + i] = _n / 1000f;
            return i;
        }
    }

    private static double Rms(float[] x, int from) =>
        Math.Sqrt(x.Skip(from).Select(v => (double)v * v).Average());

    [Fact]
    public void Varispeed_AtOne_PassesThrough()
    {
        var v = new Varispeed(new Ramp(5000), 1);
        var buf = new float[6000];
        int total = 0, read;
        while ((read = v.Read(buf, total, 700)) > 0) total += read;
        Assert.Equal(5000, total);
        for (int i = 0; i < total; i++) Assert.Equal(i / 1000f, buf[i], 4);
    }

    [Fact]
    public void Varispeed_Faster_IsShorter_AndInterpolatesLinearRamp()
    {
        var v = new Varispeed(new Ramp(10000), 1.25);
        var buf = new float[10000];
        int total = 0, read;
        while ((read = v.Read(buf, total, 512)) > 0) total += read;
        Assert.InRange(total, 7990, 8010);
        // интерполяция линейного сигнала — точная
        for (int i = 10; i < total - 10; i++) Assert.True(Math.Abs(i * 1.25 / 1000 - buf[i]) < 1e-3, $"{i}: {buf[i]}");
    }

    [Fact]
    public void Varispeed_GlidesToTarget()
    {
        var v = new Varispeed(new Sine(440, 1), 1.06);
        v.GlideTo(1, 4410);
        var buf = new float[2000];
        for (int i = 0; i < 3; i++) v.Read(buf, 0, buf.Length);
        Assert.Equal(1, v.Rate, 6);
        Assert.InRange(v.BufferedSeconds, 0, 0.2);
    }

    [Fact]
    public void Crossover_Unity_KeepsLevel_AndCutsBass()
    {
        foreach (var (freq, lo, hi, min, max) in new[]
                 {
                     (1000.0, 1f, 1f, 0.33, 0.38),   // 0.5/√2 ≈ 0.354 — без изменений
                     (50.0, 1f, 1f, 0.33, 0.38),
                     (50.0, 0f, 1f, 0.0, 0.04),      // бас убран
                     (2000.0, 0f, 1f, 0.33, 0.38),   // верх не тронут
                     (2000.0, 1f, 0f, 0.0, 0.03),
                 })
        {
            var x = new Crossover(44100, 1, warm: false);
            var src = new Sine(freq, 1);
            var buf = new float[44100];
            src.Read(buf, 0, buf.Length);
            for (int i = 0; i < buf.Length; i++) x.Process(buf, i, 1, lo, hi);
            Assert.InRange(Rms(buf, 22050), min, max);
        }
    }

    [Fact]
    public void Fades_Endpoints()
    {
        foreach (var swap in new[] { false, true })
        {
            DjFades.Gains(0, swap, out var oLo, out var oHi, out var iLo, out var iHi);
            Assert.Equal(1, oLo, 4); Assert.Equal(1, oHi, 4); Assert.Equal(0, iLo, 4); Assert.Equal(0, iHi, 4);
            DjFades.Gains(1, swap, out oLo, out oHi, out iLo, out iHi);
            Assert.Equal(0, oLo, 4); Assert.Equal(0, oHi, 4); Assert.Equal(1, iLo, 4); Assert.Equal(1, iHi, 4);
        }
        // DJ: до середины новый трек без баса, старый — с басом
        DjFades.Gains(0.3, true, out var outLo, out _, out var inLo, out var inHi);
        Assert.Equal(1, outLo, 4);
        Assert.Equal(0, inLo, 4);
        Assert.True(inHi > 0.5);
        // после смены — наоборот
        DjFades.Gains(0.6, true, out outLo, out _, out inLo, out _);
        Assert.Equal(0, outLo, 4);
        Assert.Equal(1, inLo, 4);
    }
}

public class DjStoreTests
{
    [Fact]
    public void Store_RoundTrip_AndVersion()
    {
        var store = new DjStore(null);
        Assert.Null(store.Get("1"));
        store.Put("1", new TrackAnalysis { Bpm = 125, KeyRoot = 2 });
        Assert.Equal(125, store.Get("1")!.Bpm);
        store.Put("2", new TrackAnalysis { Version = TrackAnalysis.CurrentVersion - 1 });
        Assert.Null(store.Get("2")); // старая версия анализа — пересчитать
        Assert.Equal(2, store.Count);
    }
}
