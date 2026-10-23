using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace Undertone.Audio
{
    /// <summary>
    /// 10-полосный эквалайзер на BiQuad-фильтрах NAudio. Полосы — стандартные октавы 31 Гц … 16 кГц.
    /// Настройки меняются из UI-потока: новые фильтры строятся заранее и подменяются одной ссылкой,
    /// поток вывода звука никогда не видит наполовину обновлённый набор.
    /// </summary>
    public sealed class Equalizer
    {
        public static readonly float[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        public const float MaxGainDb = 12f;
        private const float Q = 1.1f;

        private readonly float[] _gains = new float[Frequencies.Length];
        private bool _enabled;
        private int _version;

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; _version++; }
        }

        /// <summary>Усиление полос в дБ (-12..+12).</summary>
        public float[] Gains => (float[])_gains.Clone();

        public void SetGains(float[] gains)
        {
            for (int i = 0; i < _gains.Length; i++)
                _gains[i] = i < gains.Length ? Math.Clamp(gains[i], -MaxGainDb, MaxGainDb) : 0;
            _version++;
        }

        public void SetGain(int band, float db)
        {
            if (band < 0 || band >= _gains.Length) return;
            _gains[band] = Math.Clamp(db, -MaxGainDb, MaxGainDb);
            _version++;
        }

        /// <summary>Встраивает эквалайзер в цепочку после source.</summary>
        public ISampleProvider Attach(ISampleProvider source) => new Provider(source, this);

        /// <summary>Готовые пресеты (дБ на 10 полос).</summary>
        public static readonly (string Name, float[] Gains)[] Presets =
        {
            ("Ровно", new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            ("Больше баса", new float[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 }),
            ("Рок", new float[] { 4, 3, 2, 0, -1, -1, 0, 2, 3, 4 }),
            ("Поп", new float[] { -1, 1, 3, 4, 3, 0, -1, -1, 0, 1 }),
            ("Электроника", new float[] { 5, 4, 1, 0, -2, 1, 0, 2, 4, 5 }),
            ("Голос", new float[] { -3, -2, 0, 2, 4, 4, 3, 1, 0, -2 }),
            ("Тише верхов", new float[] { 0, 0, 0, 0, 0, 0, -1, -3, -5, -7 }),
        };

        private sealed class Provider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly Equalizer _eq;
            private readonly int _channels;
            private readonly int _sampleRate;
            private BiQuadFilter[,]? _filters; // [канал, полоса]
            private int _builtVersion = -1;
            private float _preamp = 1f;

            public Provider(ISampleProvider source, Equalizer eq)
            {
                _source = source;
                _eq = eq;
                _channels = source.WaveFormat.Channels;
                _sampleRate = source.WaveFormat.SampleRate;
            }

            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);
                if (read <= 0 || !_eq._enabled) return read;

                var version = _eq._version;
                if (version != _builtVersion) Rebuild(version);
                var filters = _filters;
                if (filters == null) return read;

                var bands = filters.GetLength(1);
                for (int i = 0; i < read; i++)
                {
                    var ch = i % _channels;
                    var s = buffer[offset + i] * _preamp;
                    for (int b = 0; b < bands; b++) s = filters[ch, b].Transform(s);
                    buffer[offset + i] = s;
                }
                return read;
            }

            private void Rebuild(int version)
            {
                _builtVersion = version;
                var gains = _eq._gains;
                var any = false;
                float max = 0;
                foreach (var g in gains)
                {
                    if (Math.Abs(g) > 0.05f) any = true;
                    if (g > max) max = g;
                }
                if (!any)
                {
                    _filters = null;
                    _preamp = 1f;
                    return;
                }

                var nyquist = _sampleRate / 2f;
                var filters = new BiQuadFilter[_channels, Frequencies.Length];
                for (int ch = 0; ch < _channels; ch++)
                    for (int b = 0; b < Frequencies.Length; b++)
                    {
                        // полоса выше Найквиста (16 кГц при 22 кГц дискретизации) — ставим на край
                        var f = Math.Min(Frequencies[b], nyquist * 0.9f);
                        filters[ch, b] = BiQuadFilter.PeakingEQ(_sampleRate, f, Q, gains[b]);
                    }
                // запас по громкости, чтобы подъём полос не давал перегруз
                _preamp = (float)Math.Pow(10, -max / 20.0 * 0.8);
                _filters = filters;
            }
        }
    }
}
