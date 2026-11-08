using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace Undertone.Audio
{
    /// <summary>
    /// Плавная смена скорости «как на вертушке» (темп и высота вместе) — для подгона темпа в DJ-режиме.
    /// Интерполяция Катмулла — Рома по 4 соседним кадрам; скорость может плавно ехать к цели.
    /// </summary>
    public sealed class Varispeed : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _buf;          // кадры источника, чередование каналов
        private int _count;            // сколько кадров в _buf
        private double _pos;           // дробная позиция в кадрах _buf
        private bool _ended;
        private double _target;
        private double _step;          // изменение скорости за выходной кадр

        public Varispeed(ISampleProvider source, double rate)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            _buf = new float[4096 * _channels];
            Rate = _target = Math.Clamp(rate, 0.5, 2);
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        /// <summary>Текущая скорость (1 — как записано).</summary>
        public double Rate { get; private set; }

        /// <summary>Сколько секунд источника уже прочитано, но ещё не прозвучало (сидит в буфере интерполяции).</summary>
        public double BufferedSeconds => Math.Max(0, _count - _pos) / WaveFormat.SampleRate;

        /// <summary>Плавно прийти к скорости rate за frames выходных кадров.</summary>
        public void GlideTo(double rate, int frames)
        {
            _target = Math.Clamp(rate, 0.5, 2);
            _step = frames <= 0 ? 0 : (_target - Rate) / frames;
            if (frames <= 0) Rate = _target;
        }

        /// <summary>После перемотки источника: забыть накопленные кадры.</summary>
        public void Reset()
        {
            _count = 0;
            _pos = 0;
            _ended = false;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / _channels;
            int produced = 0;
            for (; produced < frames; produced++)
            {
                int i = (int)_pos;
                if (i + 2 >= _count)
                {
                    Fill(i);
                    i = (int)_pos;
                    if (i >= _count) break; // источник кончился
                }
                var frac = (float)(_pos - i);
                int i0 = Math.Max(0, i - 1), i2 = Math.Min(_count - 1, i + 1), i3 = Math.Min(_count - 1, i + 2);
                var o = offset + produced * _channels;
                for (int c = 0; c < _channels; c++)
                {
                    float y0 = _buf[i0 * _channels + c], y1 = _buf[i * _channels + c];
                    float y2 = _buf[i2 * _channels + c], y3 = _buf[i3 * _channels + c];
                    buffer[o + c] = CatmullRom(y0, y1, y2, y3, frac);
                }
                _pos += Rate;
                if (_step != 0)
                {
                    Rate += _step;
                    if ((_step > 0 && Rate >= _target) || (_step < 0 && Rate <= _target))
                    {
                        Rate = _target;
                        _step = 0;
                    }
                }
            }
            return produced * _channels;
        }

        /// <summary>Сдвигает буфер (оставляя кадр до текущего для интерполяции) и дочитывает источник.</summary>
        private void Fill(int i)
        {
            if (_ended) return;
            int keep = Math.Max(0, i - 1);
            if (keep > 0)
            {
                Array.Copy(_buf, keep * _channels, _buf, 0, (_count - keep) * _channels);
                _count -= keep;
                _pos -= keep;
            }
            var free = _buf.Length / _channels - _count;
            if (free < 1024)
            {
                Array.Resize(ref _buf, _buf.Length * 2);
                free = _buf.Length / _channels - _count;
            }
            int read = _source.Read(_buf, _count * _channels, free * _channels) / _channels;
            if (read <= 0) _ended = true;
            else _count += read;
        }

        private static float CatmullRom(float y0, float y1, float y2, float y3, float t)
        {
            var a = -0.5f * y0 + 1.5f * y1 - 1.5f * y2 + 0.5f * y3;
            var b = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            var c = -0.5f * y0 + 0.5f * y2;
            return ((a * t + b) * t + c) * t + y1;
        }
    }

    /// <summary>
    /// Делит звук на низ и верх (Линквиц — Райли 4-го порядка, ~180 Гц) и собирает обратно с разной громкостью.
    /// Низ + верх при единичных громкостях — тот же звук (сдвиг фазы баса на слух не заметен).
    /// warm — первые ~80 мс плавно переходит от необработанного звука к фильтрам, чтобы не щёлкнуло.
    /// </summary>
    public sealed class Crossover
    {
        public const float Frequency = 180f;

        private readonly BiQuadFilter[] _lp1, _lp2, _hp1, _hp2;
        private readonly int _warmFrames;
        private int _warmDone;

        public Crossover(int sampleRate, int channels, bool warm)
        {
            _lp1 = new BiQuadFilter[channels];
            _lp2 = new BiQuadFilter[channels];
            _hp1 = new BiQuadFilter[channels];
            _hp2 = new BiQuadFilter[channels];
            const float q = 0.70710678f;
            for (int c = 0; c < channels; c++)
            {
                _lp1[c] = BiQuadFilter.LowPassFilter(sampleRate, Frequency, q);
                _lp2[c] = BiQuadFilter.LowPassFilter(sampleRate, Frequency, q);
                _hp1[c] = BiQuadFilter.HighPassFilter(sampleRate, Frequency, q);
                _hp2[c] = BiQuadFilter.HighPassFilter(sampleRate, Frequency, q);
            }
            _warmFrames = warm ? sampleRate * 80 / 1000 : 0;
        }

        /// <summary>Один кадр (по сэмплу на канал) на месте: низ × lo + верх × hi.</summary>
        public void Process(float[] buffer, int offset, int channels, float lo, float hi)
        {
            var w = 1f;
            if (_warmDone < _warmFrames)
            {
                w = (float)_warmDone / _warmFrames;
                _warmDone++;
            }
            for (int c = 0; c < channels; c++)
            {
                var x = buffer[offset + c];
                var low = _lp2[c].Transform(_lp1[c].Transform(x));
                var high = _hp2[c].Transform(_hp1[c].Transform(x));
                var y = low * lo + high * hi;
                // во время «прогрева» громкости lo и hi одинаковые — смешиваем с исходным
                buffer[offset + c] = w >= 1f ? y : y * w + x * (lo + hi) * 0.5f * (1 - w);
            }
        }
    }

    /// <summary>Кривые громкости DJ-перехода.</summary>
    public static class DjFades
    {
        /// <summary>
        /// Громкость низа и верха обоих треков в точке перехода p (0..1).
        /// Обычный кроссфейд — равномощный по всему сигналу. DJ: новый трек входит без баса (верх нарастает
        /// до середины), посередине за ~1/10 перехода басы меняются местами, потом уходящий трек затихает.
        /// </summary>
        public static void Gains(double p, bool bassSwap, out float outLo, out float outHi, out float inLo, out float inHi)
        {
            if (!bassSwap)
            {
                var o = (float)Math.Cos(p * Math.PI / 2);
                var i = (float)Math.Sin(p * Math.PI / 2);
                outLo = outHi = o;
                inLo = inHi = i;
                return;
            }
            inHi = (float)Math.Sin(Ramp(p, 0, 0.45) * Math.PI / 2);
            var swap = Ramp(p, 0.45, 0.55);
            inLo = (float)Math.Sin(swap * Math.PI / 2);
            outLo = (float)Math.Cos(swap * Math.PI / 2);
            outHi = (float)Math.Cos(Ramp(p, 0.55, 1) * Math.PI / 2);
        }

        private static double Ramp(double p, double from, double to) => Math.Clamp((p - from) / (to - from), 0, 1);
    }
}
