using System;
using NAudio.Wave;

namespace Undertone.Audio
{
    /// <summary>
    /// Эффекты «как в TikTok»: скорость (как у пластинки — медленнее и ниже, быстрее и выше),
    /// реверберация (Freeverb) и «8D» — звук медленно кружит вокруг головы.
    /// Настройки меняются из UI-потока, звук читает их на лету: переключение без щелчков и без перезапуска трека.
    /// </summary>
    public sealed class SoundEffects
    {
        public const float MinSpeed = 0.6f, MaxSpeed = 1.5f;

        public sealed record Preset(string Id, string Name, string Hint, float Speed, float Reverb, bool Spin);

        public static readonly Preset[] Presets =
        {
            new("off", "Обычный", "Как записано", 1f, 0f, false),
            new("slowed", "Slowed + reverb", "Медленнее, ниже и с эхом", 0.85f, 0.45f, false),
            new("superslowed", "Super slowed", "Совсем медленно, как во сне", 0.72f, 0.6f, false),
            new("speedup", "Speed up", "Быстрее и выше", 1.2f, 0f, false),
            new("nightcore", "Nightcore", "Ещё быстрее и звонче", 1.32f, 0.1f, false),
            new("8d", "8D", "Звук кружит вокруг головы — в наушниках", 1f, 0.25f, true),
        };

        private volatile float _speed = 1f;
        private volatile float _reverb;
        private volatile bool _spin;
        private volatile bool _lockSpeed;

        /// <summary>Скорость воспроизведения, 1 — как записано.</summary>
        public float Speed
        {
            get => _speed;
            set => _speed = Math.Clamp(float.IsFinite(value) ? value : 1f, MinSpeed, MaxSpeed);
        }

        /// <summary>Сколько эха, 0..1.</summary>
        public float Reverb
        {
            get => _reverb;
            set => _reverb = Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 1f);
        }

        /// <summary>«8D»: звук ездит по кругу слева направо.</summary>
        public bool Spin
        {
            get => _spin;
            set => _spin = value;
        }

        /// <summary>
        /// Совместное прослушивание: у всех в комнате трек должен идти секунда в секунду,
        /// поэтому скорость временно 1 (эхо и 8D остаются — на время они не влияют).
        /// </summary>
        public bool LockSpeed
        {
            get => _lockSpeed;
            set => _lockSpeed = value;
        }

        public float EffectiveSpeed => _lockSpeed ? 1f : _speed;

        public bool IsActive => Math.Abs(EffectiveSpeed - 1f) > 0.001f || _reverb > 0.001f || _spin;

        public void Apply(Preset preset)
        {
            Speed = preset.Speed;
            Reverb = preset.Reverb;
            Spin = preset.Spin;
        }

        /// <summary>Какому пресету соответствуют текущие настройки (null — настроено вручную).</summary>
        public Preset? CurrentPreset()
        {
            foreach (var p in Presets)
                if (Math.Abs(p.Speed - _speed) < 0.005f && Math.Abs(p.Reverb - _reverb) < 0.005f && p.Spin == _spin)
                    return p;
            return null;
        }

        /// <summary>Встраивает эффекты в цепочку после source. Один экземпляр — на один открытый трек.</summary>
        public Provider Attach(ISampleProvider source) => new(source, this);

        public sealed class Provider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly SoundEffects _fx;
            private readonly int _channels;
            private readonly int _sampleRate;

            // ── скорость: входной буфер и дробная позиция в нём (в кадрах) ──
            private float[] _in = new float[4096];
            private int _inFrames;
            private double _pos;
            private bool _ended;
            private volatile bool _flushRequested;

            private readonly Freeverb _reverb;
            private double _spinPhase;

            internal Provider(ISampleProvider source, SoundEffects fx)
            {
                _source = source;
                _fx = fx;
                _channels = source.WaveFormat.Channels;
                _sampleRate = source.WaveFormat.SampleRate;
                _reverb = new Freeverb(_sampleRate);
            }

            public WaveFormat WaveFormat => _source.WaveFormat;

            /// <summary>Перемотали — накопленное больше не к месту. Безопасно звать из любого потока.</summary>
            public void Flush() => _flushRequested = true;

            public int Read(float[] buffer, int offset, int count)
            {
                if (_flushRequested)
                {
                    _flushRequested = false;
                    _inFrames = 0;
                    _pos = 0;
                    _ended = false;
                    _reverb.Clear();
                }

                count -= count % _channels;
                var speed = _fx.EffectiveSpeed;
                int written = Math.Abs(speed - 1f) < 0.001f
                    ? ReadNormal(buffer, offset, count)
                    : ReadResampled(buffer, offset, count, speed);
                if (written <= 0) return written;

                var reverb = _fx._reverb;
                if (reverb > 0.001f) _reverb.Process(buffer, offset, written, _channels, reverb);
                else _reverb.Idle();

                if (_fx._spin && _channels == 2) Spin(buffer, offset, written);
                return written;
            }

            /// <summary>Скорость 1: сначала отдаём, что осталось в буфере от изменённой скорости, дальше — напрямую.</summary>
            private int ReadNormal(float[] buffer, int offset, int count)
            {
                int written = 0;
                if (_inFrames > 0)
                {
                    var start = (int)Math.Round(_pos);
                    var frames = Math.Max(0, Math.Min(_inFrames - start, count / _channels));
                    Array.Copy(_in, start * _channels, buffer, offset, frames * _channels);
                    written = frames * _channels;
                    _pos = start + frames;
                    if (_pos >= _inFrames)
                    {
                        _inFrames = 0;
                        _pos = 0;
                    }
                    if (written == count) return written;
                    if (_ended) return written;
                }
                return written + _source.Read(buffer, offset + written, count - written);
            }

            /// <summary>Изменённая скорость — интерполяция Катмулла — Рома (мягче линейной, без «металла»).</summary>
            private int ReadResampled(float[] buffer, int offset, int count, double speed)
            {
                var ch = _channels;
                var outFrames = count / ch;
                int f = 0;
                while (f < outFrames)
                {
                    var i = (int)_pos;
                    if (i + 2 >= _inFrames && !_ended)
                    {
                        Refill((int)Math.Ceiling((outFrames - f) * speed) + 4);
                        continue;
                    }
                    if (i >= _inFrames) break; // трек кончился

                    var t = (float)(_pos - i);
                    var o = offset + f * ch;
                    for (int c = 0; c < ch; c++)
                    {
                        var p0 = Sample(i - 1, c);
                        var p1 = Sample(i, c);
                        var p2 = Sample(i + 1, c);
                        var p3 = Sample(i + 2, c);
                        buffer[o + c] = CatmullRom(p0, p1, p2, p3, t);
                    }
                    _pos += speed;
                    f++;
                }
                return f * ch;
            }

            private float Sample(int frame, int channel)
            {
                if (frame < 0) frame = 0;
                if (frame >= _inFrames) return 0f;
                return _in[frame * _channels + channel];
            }

            internal static float CatmullRom(float p0, float p1, float p2, float p3, float t)
            {
                var t2 = t * t;
                var t3 = t2 * t;
                return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
            }

            /// <summary>Сдвигает прочитанное к началу (оставляя кадр истории) и дочитывает из источника.</summary>
            private void Refill(int wantFrames)
            {
                var ch = _channels;
                var keep = Math.Max(0, (int)_pos - 1);
                if (keep > 0)
                {
                    keep = Math.Min(keep, _inFrames);
                    Array.Copy(_in, keep * ch, _in, 0, (_inFrames - keep) * ch);
                    _inFrames -= keep;
                    _pos -= keep;
                }

                var need = (_inFrames + wantFrames) * ch;
                if (_in.Length < need) Array.Resize(ref _in, Math.Max(need, _in.Length * 2));
                var read = _source.Read(_in, _inFrames * ch, wantFrames * ch);
                read -= read % ch;
                if (read <= 0)
                {
                    _ended = true;
                    return;
                }
                _inFrames += read / ch;
            }

            /// <summary>8D: равномощная панорама по синусу, полный круг за 8 секунд; часть стерео сохраняем.</summary>
            private void Spin(float[] buffer, int offset, int count)
            {
                const double period = 8.0;
                var step = 2 * Math.PI / (period * _sampleRate);
                for (int i = 0; i < count; i += 2)
                {
                    var l = buffer[offset + i];
                    var r = buffer[offset + i + 1];
                    var mid = (l + r) * 0.5f;
                    var pan = Math.Sin(_spinPhase);                  // -1 слева … +1 справа
                    var angle = (pan + 1) * Math.PI / 4;              // 0 … π/2
                    // 1.2 вместо √2: в центре чуть тише, зато с краю не перегружает
                    var gl = (float)(Math.Cos(angle) * 1.2);
                    var gr = (float)(Math.Sin(angle) * 1.2);
                    buffer[offset + i] = (0.3f * l + 0.7f * mid) * gl;
                    buffer[offset + i + 1] = (0.3f * r + 0.7f * mid) * gr;
                    _spinPhase += step;
                }
                if (_spinPhase > 2 * Math.PI) _spinPhase -= 2 * Math.PI;
            }
        }

        /// <summary>
        /// Классический Freeverb (Jezar at Dreampoint, public domain): 8 гребенчатых и 4 всепропускающих фильтра на канал.
        /// Длины задержек — для 44,1 кГц, для другой частоты масштабируются.
        /// </summary>
        internal sealed class Freeverb
        {
            private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
            private static readonly int[] AllpassTuning = { 556, 441, 341, 225 };
            private const int StereoSpread = 23;
            private const float FixedGain = 0.015f;

            private readonly Comb[] _combL, _combR;
            private readonly Allpass[] _apL, _apR;
            private bool _dirty;

            public Freeverb(int sampleRate)
            {
                var scale = sampleRate / 44100.0;
                int Len(int n) => Math.Max(1, (int)(n * scale));
                _combL = Array.ConvertAll(CombTuning, n => new Comb(Len(n)));
                _combR = Array.ConvertAll(CombTuning, n => new Comb(Len(n + StereoSpread)));
                _apL = Array.ConvertAll(AllpassTuning, n => new Allpass(Len(n)));
                _apR = Array.ConvertAll(AllpassTuning, n => new Allpass(Len(n + StereoSpread)));
            }

            /// <summary>amount 0..1: и количество эха, и размер «зала».</summary>
            public void Process(float[] buffer, int offset, int count, int channels, float amount)
            {
                _dirty = true;
                var feedback = 0.7f + 0.28f * (0.72f + 0.25f * amount); // roomsize 0.72…0.97
                var damp = 0.45f;
                var wet = 0.75f * amount;
                var dry = 1f - 0.35f * amount;
                foreach (var c in _combL) c.Set(feedback, damp);
                foreach (var c in _combR) c.Set(feedback, damp);

                if (channels == 2)
                {
                    for (int i = 0; i < count; i += 2)
                    {
                        var l = buffer[offset + i];
                        var r = buffer[offset + i + 1];
                        var input = (l + r) * FixedGain;
                        float outL = 0, outR = 0;
                        for (int k = 0; k < _combL.Length; k++)
                        {
                            outL += _combL[k].Process(input);
                            outR += _combR[k].Process(input);
                        }
                        for (int k = 0; k < _apL.Length; k++)
                        {
                            outL = _apL[k].Process(outL);
                            outR = _apR[k].Process(outR);
                        }
                        buffer[offset + i] = l * dry + outL * wet;
                        buffer[offset + i + 1] = r * dry + outR * wet;
                    }
                }
                else
                {
                    for (int i = 0; i < count; i += channels)
                    {
                        var x = buffer[offset + i];
                        var input = x * 2 * FixedGain;
                        float o = 0;
                        foreach (var c in _combL) o += c.Process(input);
                        foreach (var a in _apL) o = a.Process(o);
                        for (int c = 0; c < channels; c++) buffer[offset + i + c] = buffer[offset + i + c] * dry + o * wet;
                    }
                }
            }

            /// <summary>Эхо выключили — хвост больше не нужен, чтобы при включении не всплыл старый звук.</summary>
            public void Idle()
            {
                if (_dirty) Clear();
            }

            public void Clear()
            {
                _dirty = false;
                foreach (var c in _combL) c.Clear();
                foreach (var c in _combR) c.Clear();
                foreach (var a in _apL) a.Clear();
                foreach (var a in _apR) a.Clear();
            }

            private sealed class Comb
            {
                private readonly float[] _buf;
                private int _idx;
                private float _store, _feedback, _damp1, _damp2;

                public Comb(int size) => _buf = new float[size];

                public void Set(float feedback, float damp)
                {
                    _feedback = feedback;
                    _damp1 = damp;
                    _damp2 = 1 - damp;
                }

                public float Process(float input)
                {
                    var output = _buf[_idx];
                    _store = output * _damp2 + _store * _damp1;
                    _buf[_idx] = input + _store * _feedback;
                    if (++_idx >= _buf.Length) _idx = 0;
                    return output;
                }

                public void Clear()
                {
                    Array.Clear(_buf);
                    _store = 0;
                }
            }

            private sealed class Allpass
            {
                private readonly float[] _buf;
                private int _idx;

                public Allpass(int size) => _buf = new float[size];

                public float Process(float input)
                {
                    var bufout = _buf[_idx];
                    var output = -input + bufout;
                    _buf[_idx] = input + bufout * 0.5f;
                    if (++_idx >= _buf.Length) _idx = 0;
                    return output;
                }

                public void Clear() => Array.Clear(_buf);
            }
        }
    }
}
