using System;
using NAudio.Wave;
using Undertone.Audio;

namespace Undertone.Tests;

public class EqualizerTests
{
    private sealed class Constant : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = 0.25f;
            return count;
        }
    }

    [Fact]
    public void FlatOrDisabled_PassesSignalUntouched()
    {
        var eq = new Equalizer();
        var chain = eq.Attach(new Constant());
        var buf = new float[512];

        chain.Read(buf, 0, buf.Length);
        Assert.All(buf, s => Assert.Equal(0.25f, s));

        eq.Enabled = true; // все полосы 0 дБ — фильтры не строятся
        chain.Read(buf, 0, buf.Length);
        Assert.All(buf, s => Assert.Equal(0.25f, s));
    }

    [Fact]
    public void Gains_AreClamped()
    {
        var eq = new Equalizer();
        eq.SetGains(new[] { 100f, -100f });
        Assert.Equal(Equalizer.MaxGainDb, eq.Gains[0]);
        Assert.Equal(-Equalizer.MaxGainDb, eq.Gains[1]);
        Assert.Equal(0f, eq.Gains[9]);
    }
}
