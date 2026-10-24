using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using Undertone.Audio;
using Undertone.Core;
using Xunit;

namespace Undertone.Tests;

public class SoundEffectsTests
{
    /// <summary>Источник: стерео-пила заданной длины.</summary>
    private sealed class Saw : ISampleProvider
    {
        private readonly int _frames;
        private int _pos;
        public Saw(int frames) => _frames = frames;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = 0;
            while (n < count && _pos < _frames * 2)
            {
                buffer[offset + n] = (_pos / 2 % 100) / 100f - 0.5f; // без постоянной составляющей, как в музыке
                n++;
                _pos++;
            }
            return n;
        }
    }

    private static List<float> ReadAll(ISampleProvider p)
    {
        var all = new List<float>();
        var buf = new float[1024];
        int read;
        while ((read = p.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(read));
        return all;
    }

    [Fact]
    public void Off_IsBitExactPassthrough()
    {
        var fx = new SoundEffects();
        var expected = ReadAll(new Saw(5000));
        var actual = ReadAll(fx.Attach(new Saw(5000)));
        Assert.Equal(expected, actual);
        Assert.False(fx.IsActive);
    }

    [Theory]
    [InlineData(0.85f)]
    [InlineData(0.72f)]
    [InlineData(1.25f)]
    public void Speed_ChangesLengthProportionally(float speed)
    {
        var fx = new SoundEffects { Speed = speed };
        var frames = ReadAll(fx.Attach(new Saw(44100))).Count / 2;
        Assert.InRange(frames, 44100 / speed - 8, 44100 / speed + 8);
    }

    [Fact]
    public void BackToNormalSpeed_DrainsBufferWithoutLosingAudio()
    {
        var fx = new SoundEffects { Speed = 0.8f };
        var p = fx.Attach(new Saw(20000));
        var buf = new float[2048];
        Assert.Equal(2048, p.Read(buf, 0, buf.Length));
        fx.Speed = 1f;
        var rest = ReadAll(p).Count / 2;
        // 1024 кадра на 0.8× съели ~819 кадров источника — остальное должно дойти целиком
        Assert.InRange(rest, 20000 - 825, 20000 - 812);
    }

    [Fact]
    public void LockSpeed_ForcesNormalSpeedInRooms()
    {
        var fx = new SoundEffects { Speed = 0.8f, LockSpeed = true };
        Assert.Equal(1f, fx.EffectiveSpeed);
        Assert.Equal(44100, ReadAll(fx.Attach(new Saw(44100))).Count / 2);
    }

    [Fact]
    public void Reverb_And8D_StayFiniteAndBounded()
    {
        var fx = new SoundEffects { Reverb = 1f, Spin = true };
        var output = ReadAll(fx.Attach(new Saw(44100 * 3)));
        Assert.All(output, s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 4f));
        Assert.True(fx.IsActive);
    }

    [Fact]
    public void Presets_AreRecognised()
    {
        var fx = new SoundEffects();
        foreach (var preset in SoundEffects.Presets)
        {
            fx.Apply(preset);
            Assert.Equal(preset.Id, fx.CurrentPreset()?.Id);
        }
        fx.Speed = 0.93f;
        Assert.Null(fx.CurrentPreset());
    }

    [Fact]
    public void CatmullRom_HitsKnotsExactly()
    {
        Assert.Equal(0.3f, SoundEffects.Provider.CatmullRom(0.1f, 0.3f, 0.7f, 0.2f, 0f), 5);
        Assert.Equal(0.7f, SoundEffects.Provider.CatmullRom(0.1f, 0.3f, 0.7f, 0.2f, 1f), 5);
    }

    [Fact]
    public void Speed_IsClamped()
    {
        var fx = new SoundEffects { Speed = 10f, Reverb = -1f };
        Assert.Equal(SoundEffects.MaxSpeed, fx.Speed);
        Assert.Equal(0f, fx.Reverb);
        fx.Speed = float.NaN;
        Assert.Equal(1f, fx.Speed);
    }
}

public class RecapTests
{
    private static YaTrack T(string id, string artist, int minutes = 3) =>
        new() { Id = id, Title = "Трек " + id, Artists = new[] { artist }, DurationMs = minutes * 60_000 };

    [Fact]
    public void PlayLog_RoundTrips()
    {
        var e = new PlayEvent(new DateTime(2026, 10, 24, 20, 31, 5), "12345", 187);
        Assert.True(PlayLog.TryParse(PlayLog.Format(e), out var back));
        Assert.Equal(e, back);
        Assert.False(PlayLog.TryParse("мусор", out _));
        Assert.False(PlayLog.TryParse("2026-10-24T20:31:05\t\t10", out _));
    }

    [Fact]
    public void FromLog_CountsPeriodTopsDaysAndStreak()
    {
        var tracks = new Dictionary<string, YaTrack>
        {
            ["a"] = T("a", "Артист 1"),
            ["b"] = T("b", "Артист 2"),
            ["c"] = T("c", "Артист 1"),
        };
        var log = new List<PlayEvent>
        {
            new(new DateTime(2026, 10, 1, 23, 0, 0), "a", 180),
            new(new DateTime(2026, 10, 2, 23, 10, 0), "a", 180),
            new(new DateTime(2026, 10, 3, 1, 0, 0), "b", 240),
            new(new DateTime(2026, 10, 3, 1, 5, 0), "c", 120),
            new(new DateTime(2026, 10, 10, 12, 0, 0), "a", 10),  // пролистал — не считается
            new(new DateTime(2026, 9, 30, 12, 0, 0), "b", 200),  // другой месяц
        };
        var first = new Dictionary<string, DateTime> { ["a"] = new(2026, 9, 1), ["b"] = new(2026, 9, 30), ["c"] = new(2026, 10, 3) };

        var r = Recap.FromLog(log, RecapPeriod.Month(new DateTime(2026, 10, 15)), id => tracks.GetValueOrDefault(id),
            id => first.TryGetValue(id, out var f) ? f : null);

        Assert.Equal("Октябрь 2026", r.Period.Title);
        Assert.Equal(4, r.Plays);
        Assert.Equal((180 + 180 + 240 + 120) / 60, r.Minutes);
        Assert.Equal("a", r.TopTracks[0].Track.Id);
        Assert.Equal("Артист 1", r.TopArtists[0].Name); // 3 + 3 + 2 минуты против 4
        Assert.Equal(3, r.Streak);
        Assert.Equal(3, r.ActiveDays);
        Assert.Equal(new DateTime(2026, 10, 3), r.BestDay);
        Assert.Equal(1, r.NewTracks);
        Assert.Equal("Ночная сова", r.Chronotype().Title);
    }

    [Fact]
    public void FromStats_EstimatesAllTime()
    {
        var stats = new PlayStats();
        var now = new DateTime(2026, 10, 24, 9, 0, 0);
        for (int i = 0; i < 4; i++) stats.Count(T("x", "Утренний", 4), now);
        stats.Count(T("y", "Другой", 2), now);
        var r = Recap.FromStats(stats);
        Assert.False(r.Exact);
        Assert.Equal(5, r.Plays);
        Assert.Equal(18, r.Minutes);
        Assert.Equal("Утренний", r.TopArtists[0].Name);
        Assert.Equal("Жаворонок", r.Chronotype().Title);
    }

    [Fact]
    public void Empty_IsEmpty()
    {
        var r = Recap.FromLog(Array.Empty<PlayEvent>(), RecapPeriod.Year(2026), _ => null, _ => null);
        Assert.True(r.IsEmpty);
        Assert.Equal(0, r.Streak);
    }

    [Theory]
    [InlineData(1, "минута")]
    [InlineData(3, "минуты")]
    [InlineData(11, "минут")]
    [InlineData(21, "минута")]
    [InlineData(25, "минут")]
    public void Plural_Russian(int n, string expected) =>
        Assert.Equal(expected, Recap.Plural(n, "минута", "минуты", "минут"));
}
