using System;

namespace Undertone.Core
{
    /// <summary>
    /// Анализатор для визуализации. Аудиопоток пишет сэмплы (Write), поток интерфейса
    /// забирает спектр по полосам (ComputeBands) или форму волны (ComputeWave).
    /// FFT своя (radix-2), без зависимостей.
    /// </summary>
    public sealed class SpectrumAnalyzer
    {
        public const int FftSize = 4096;
        private const int RingSize = 1 << 16; // ~1.4 с при 48 кГц — с запасом на задержку вывода
        private const float MinFreq = 30f;
        private const float MaxFreq = 16000f;
        private const float FloorDb = -62f;
        private const float SlopeDbPerOctave = 3.5f; // поднимаем верха, как в Winamp — иначе справа всегда пусто

        private readonly float[] _ring = new float[RingSize];
        private readonly object _lock = new();
        private long _written;

        private readonly float[] _window = new float[FftSize];
        private readonly float[] _re = new float[FftSize];
        private readonly float[] _im = new float[FftSize];
        private readonly float[] _mags = new float[FftSize / 2];
        private readonly float _windowGain;

        public int SampleRate { get; private set; } = 44100;

        public SpectrumAnalyzer()
        {
            double sum = 0;
            for (int i = 0; i < FftSize; i++)
            {
                _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                sum += _window[i];
            }
            _windowGain = (float)sum;
        }

        public void Reset(int sampleRate)
        {
            lock (_lock)
            {
                SampleRate = sampleRate > 0 ? sampleRate : 44100;
                Array.Clear(_ring);
                _written = 0;
            }
        }

        /// <summary>Вызывается из аудиопотока: interleaved-сэмплы, сводим в моно.</summary>
        public void Write(float[] buffer, int offset, int count, int channels)
        {
            if (channels < 1) channels = 1;
            int frames = count / channels;
            lock (_lock)
            {
                long w = _written;
                for (int f = 0; f < frames; f++)
                {
                    float s = 0;
                    int b = offset + f * channels;
                    for (int c = 0; c < channels; c++) s += buffer[b + c];
                    _ring[(int)(w & (RingSize - 1))] = s / channels;
                    w++;
                }
                _written = w;
            }
        }

        /// <summary>Копирует последние n сэмплов, закончившиеся delaySamples назад (компенсация буфера вывода).</summary>
        private bool Snapshot(float[] dest, int n, int delaySamples)
        {
            lock (_lock)
            {
                long end = _written - Math.Max(0, delaySamples);
                if (end < n) end = Math.Min(_written, n);
                if (end < n) return false;
                long start = end - n;
                for (int i = 0; i < n; i++)
                    dest[i] = _ring[(int)((start + i) & (RingSize - 1))];
                return true;
            }
        }

        /// <summary>
        /// Заполняет bands значениями 0..1 (логарифмические полосы 30 Гц – 16 кГц, шкала в дБ).
        /// false — данных пока нет.
        /// </summary>
        public bool ComputeBands(float[] bands, int delaySamples = 0)
        {
            if (!Snapshot(_re, FftSize, delaySamples))
            {
                Array.Clear(bands);
                return false;
            }
            for (int i = 0; i < FftSize; i++)
            {
                _re[i] *= _window[i];
                _im[i] = 0;
            }
            Fft(_re, _im);

            float norm = 2f / _windowGain;
            for (int i = 0; i < _mags.Length; i++)
                _mags[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) * norm;

            int n = bands.Length;
            float maxFreq = Math.Min(MaxFreq, SampleRate / 2f);
            float binHz = (float)SampleRate / FftSize;
            float ratio = maxFreq / MinFreq;

            for (int b = 0; b < n; b++)
            {
                float f0 = MinFreq * MathF.Pow(ratio, (float)b / n);
                float f1 = MinFreq * MathF.Pow(ratio, (float)(b + 1) / n);
                int i0 = Math.Max(1, (int)MathF.Floor(f0 / binHz));
                int i1 = Math.Min(_mags.Length - 1, Math.Max(i0, (int)MathF.Ceiling(f1 / binHz)));

                float peak = 0;
                for (int i = i0; i <= i1; i++) peak = Math.Max(peak, _mags[i]);

                float center = MathF.Sqrt(f0 * f1);
                float db = 20f * MathF.Log10(peak + 1e-9f) + SlopeDbPerOctave * MathF.Log2(center / 1000f);
                bands[b] = Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);
            }
            return true;
        }

        /// <summary>Последние wave.Length сэмплов (−1..1) для осциллографа.</summary>
        public bool ComputeWave(float[] wave, int delaySamples = 0)
        {
            if (Snapshot(wave, wave.Length, delaySamples)) return true;
            Array.Clear(wave);
            return false;
        }

        /// <summary>Итеративная radix-2 FFT на месте.</summary>
        internal static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
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
                double ang = -2 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                int half = len >> 1;
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        float xr = re[b] * cr - im[b] * ci;
                        float xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
