using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Undertone.Core;

namespace Undertone.Audio
{
    /// <summary>Файл не удалось открыть или декодировать — скорее всего он битый, его стоит перекачать.</summary>
    public sealed class AudioDecodeException : Exception
    {
        public AudioDecodeException(Exception inner) : base(inner.Message, inner) { }
    }

    /// <summary>Не работает устройство вывода (отключили наушники, нет звуковой карты). С файлом всё в порядке.</summary>
    public sealed class AudioOutputException : Exception
    {
        public AudioOutputException(Exception inner)
            : base("нет устройства вывода звука: " + inner.Message, inner) { }
    }

    public sealed record AudioDevice(string Id, string Name);

    /// <summary>
    /// Воспроизведение на NAudio с API в духе WPF MediaPlayer.
    /// Цепочка: источник (MediaFoundationReader для файла или StreamingMp3Reader для идущей закачки)
    /// → сэмплы (под блокировкой) → эффекты (скорость, эхо, 8D) → «отвод» в анализатор → эквалайзер → громкость → WASAPI (запасной вариант — WaveOut).
    /// Анализатор получает сигнал ДО регулятора громкости — визуализация не «сжимается» при тихом звуке, как в Winamp.
    /// Все события приходят в UI-поток: NAudio постит их в SynchronizationContext, где создан плеер,
    /// а уведомления о смене устройства мы постим туда сами.
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        public const int LatencyMs = 100;

        /// <summary>
        /// Источники звука не потокобезопасны: поток вывода читает их, а UI меняет позицию.
        /// Все обращения к источнику идут под этой блокировкой.
        /// </summary>
        private readonly object _sync = new();
        private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

        private IWavePlayer? _output;
        private WaveStream? _reader;
        private string? _readerPath;
        private Mixer? _mixer;
        private ISampleProvider? _chainEnd;
        private VolumeSampleProvider? _volume;
        private bool _stopRequested;
        private bool _wantPlaying;
        private float _level = 0.7f;
        private bool _muted;
        private Exception? _decodeError;
        private DateTime _lastRecovery = DateTime.MinValue;
        private string? _deviceId;

        private MMDeviceEnumerator? _enumerator;
        private DeviceWatcher? _watcher;

        public AudioEngine()
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _watcher = new DeviceWatcher(this);
                _enumerator.RegisterEndpointNotificationCallback(_watcher);
            }
            catch (Exception ex)
            {
                // без Core Audio (очень старые системы) просто не следим за сменой устройства
                Log.Warn("Не удалось подписаться на смену аудиоустройств", ex);
                _watcher = null;
            }
        }

        public SpectrumAnalyzer Analyzer { get; } = new();

        /// <summary>Эквалайзер в цепочке (между анализатором и громкостью). Настраивается снаружи.</summary>
        public Equalizer Equalizer { get; } = new();

        /// <summary>Скорость / эхо / 8D — до анализатора, чтобы визуализатор плясал под то, что слышно.</summary>
        public SoundEffects Effects { get; } = new();
        private SoundEffects.Provider? _effects;

        public event EventHandler? MediaOpened;
        public event EventHandler? MediaEnded;
        /// <summary>Аргумент — <see cref="AudioDecodeException"/>, <see cref="AudioOutputException"/> или <see cref="StreamInterruptedException"/>.</summary>
        public event EventHandler<Exception>? MediaFailed;
        /// <summary>Вывод пересоздан на другом устройстве — воспроизведение продолжается. Аргумент — имя устройства.</summary>
        public event EventHandler<string>? OutputRecovered;
        /// <summary>
        /// Переход без паузы / кроссфейд: подготовленный следующий трек стал текущим (в UI-потоке).
        /// Аргумент — путь к его файлу. После этого события сразу приходит MediaOpened.
        /// </summary>
        public event EventHandler<string>? AdvancedToNext;

        public bool HasMedia => _reader != null;

        /// <summary>Следующий трек уже подготовлен для бесшовного перехода.</summary>
        public bool HasPreparedNext => _mixer?.HasNext == true;

        /// <summary>Файл текущего трека (null — играет поток идущей закачки).</summary>
        public string? CurrentPath => _readerPath;
        public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

        /// <summary>Трек играет потоком и ждёт данных из сети.</summary>
        public bool IsBuffering => (_reader as StreamingMp3Reader)?.IsStarved == true;

        /// <summary>Сколько трека скачано (0..1). Для готового файла — 1.</summary>
        public double BufferedFraction => _reader is StreamingMp3Reader s ? s.BufferedFraction : _reader != null ? 1 : 0;

        public TimeSpan? NaturalDuration => _reader?.TotalTime;

        /// <summary>id устройства вывода; null — системное устройство по умолчанию (и следовать за его сменой).</summary>
        public string? DeviceId
        {
            get => _deviceId;
            set
            {
                var id = string.IsNullOrEmpty(value) ? null : value;
                if (id == _deviceId) return;
                _deviceId = id;
                if (_output != null && TryRecreateOutput())
                    OutputRecovered?.Invoke(this, CurrentDeviceName());
            }
        }

        /// <summary>Позиция с поправкой на буфер вывода — то, что сейчас реально звучит.</summary>
        public TimeSpan Position
        {
            get
            {
                var reader = _reader;
                if (reader == null) return TimeSpan.Zero;
                TimeSpan pos;
                lock (_sync) pos = reader.CurrentTime;
                if (IsPlaying && !IsBuffering) pos -= TimeSpan.FromMilliseconds(LatencyMs * Effects.EffectiveSpeed); // буфер вывода звучит с учётом скорости
                return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
            }
            set
            {
                var reader = _reader;
                if (reader == null) return;
                var max = reader.TotalTime - TimeSpan.FromMilliseconds(200);
                if (value > max) value = max;
                if (value < TimeSpan.Zero) value = TimeSpan.Zero;
                lock (_sync)
                {
                    // перемотали — подготовленный переход больше не к месту, UI подготовит его заново у конца
                    _mixer?.CancelNext();
                    reader.CurrentTime = value;
                }
                _effects?.Flush();
            }
        }

        /// <summary>0..1</summary>
        public double Volume
        {
            get => _level;
            set
            {
                _level = (float)Math.Clamp(value, 0, 1);
                ApplyVolume();
            }
        }

        public bool IsMuted
        {
            get => _muted;
            set
            {
                _muted = value;
                ApplyVolume();
            }
        }

        /// <summary>Множитель громкости трека (нормализация), 1 — без изменений.</summary>
        public float TrackGain
        {
            get => _trackGain;
            set
            {
                _trackGain = Math.Clamp(value, 0.1f, 4f);
                ApplyVolume();
            }
        }
        private float _trackGain = 1f;

        /// <summary>Задержка между анализом и звуком, в сэмплах — визуализатор компенсирует её.</summary>
        public int AnalyzerDelaySamples => Analyzer.SampleRate * LatencyMs / 1000;

        private void ApplyVolume()
        {
            // кубическая кривая — слайдер ощущается линейно на слух
            if (_volume != null) _volume.Volume = _muted ? 0f : _level * _level * _level * _trackGain;
        }

        /// <summary>Открыть готовый файл из кэша.</summary>
        public void Open(string path)
        {
            Close();
            MediaFoundationReader reader;
            try
            {
                reader = new MediaFoundationReader(path);
            }
            catch (Exception ex)
            {
                MediaFailed?.Invoke(this, new AudioDecodeException(ex));
                return;
            }
            OpenCore(reader);
            if (_reader == reader) _readerPath = path;
        }

        /// <summary>Открыть поток (трек ещё качается). Движок становится владельцем reader.</summary>
        public void Open(WaveStream reader)
        {
            Close();
            OpenCore(reader);
        }

        private void OpenCore(WaveStream reader)
        {
            _decodeError = null;
            var mixer = new Mixer(this, reader);
            var guarded = new GuardedSource(mixer, this);
            var effects = Effects.Attach(guarded);
            var tap = new AnalyzerTap(effects, Analyzer);
            var eq = Equalizer.Attach(tap);
            var volume = new VolumeSampleProvider(eq);

            IWavePlayer output;
            try
            {
                output = CreateOutput(volume);
            }
            catch (Exception ex)
            {
                reader.Dispose();
                MediaFailed?.Invoke(this, new AudioOutputException(ex));
                return;
            }

            _chainEnd = volume;
            _mixer = mixer;
            _effects = effects;
            _volume = volume;
            ApplyVolume();
            _output = output;
            _output.PlaybackStopped += OnPlaybackStopped;
            _reader = reader;
            Analyzer.Reset(reader.WaveFormat.SampleRate);
            MediaOpened?.Invoke(this, EventArgs.Empty);
        }

        // ────────── устройства ──────────

        /// <summary>Активные устройства вывода.</summary>
        public IReadOnlyList<AudioDevice> GetDevices()
        {
            var list = new List<AudioDevice>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (d) list.Add(new AudioDevice(d.ID, d.FriendlyName));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось получить список аудиоустройств", ex);
            }
            return list;
        }

        /// <summary>Имя устройства, на котором сейчас звук (выбранного или системного).</summary>
        public string ActiveDeviceName => CurrentDeviceName();

        private string CurrentDeviceName()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = _deviceId != null
                    ? enumerator.GetDevice(_deviceId)
                    : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return device.FriendlyName;
            }
            catch (Exception)
            {
                return "устройство по умолчанию";
            }
        }

        private IWavePlayer CreateOutput(ISampleProvider source)
        {
            // WASAPI shared — основной вариант; WaveOut — если WASAPI не завёлся (старые драйверы и т.п.)
            try
            {
                WasapiOut wasapi;
                MMDevice? device = null;
                if (_deviceId != null)
                {
                    try
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        device = enumerator.GetDevice(_deviceId);
                        if (device.State != DeviceState.Active) { device.Dispose(); device = null; }
                    }
                    catch (Exception ex)
                    {
                        // выбранное устройство отключено — играем на системном
                        Log.Warn("Выбранное устройство вывода недоступно, использую системное", ex);
                        device = null;
                    }
                }
                wasapi = device != null
                    ? new WasapiOut(device, AudioClientShareMode.Shared, false, LatencyMs)
                    : new WasapiOut(AudioClientShareMode.Shared, false, LatencyMs);
                try
                {
                    wasapi.Init(source);
                    return wasapi;
                }
                catch
                {
                    wasapi.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("WASAPI не запустился, пробую WaveOut", ex);
                var waveOut = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
                try
                {
                    waveOut.Init(source);
                    return waveOut;
                }
                catch
                {
                    waveOut.Dispose();
                    throw;
                }
            }
        }

        /// <summary>Сменилось системное устройство по умолчанию (вызывается в UI-потоке).</summary>
        /// <summary>Системное устройство по умолчанию сменилось (в UI-потоке) — даже если сейчас ничего не играет.</summary>
        public event EventHandler? DefaultDeviceChanged;

        private void OnDefaultDeviceChanged()
        {
            DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
            if (_deviceId != null || _output == null) return; // играем на выбранном вручную — не трогаем
            if (TryRecreateOutput())
                OutputRecovered?.Invoke(this, CurrentDeviceName());
        }

        // ────────── управление ──────────

        public void Play()
        {
            if (_output == null) return;
            _stopRequested = false;
            _wantPlaying = true;
            _output.Play();
        }

        public void Pause()
        {
            _wantPlaying = false;
            _output?.Pause();
        }

        /// <summary>Как MediaPlayer.Stop: стоп и перемотка в начало. MediaEnded не вызывается.</summary>
        public void Stop()
        {
            if (_output == null || _reader == null) return;
            _stopRequested = true;
            _wantPlaying = false;
            _output.Stop();
            lock (_sync) _reader.CurrentTime = TimeSpan.Zero;
            _effects?.Flush();
        }

        public void Close()
        {
            var output = _output;
            var reader = _reader;
            var mixer = _mixer;
            _output = null;
            _reader = null;
            _readerPath = null;
            _mixer = null;
            _effects = null;
            _volume = null;
            _chainEnd = null;
            _wantPlaying = false;
            if (output != null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                _stopRequested = true;
                try { output.Stop(); } catch (Exception) { }
                try { output.Dispose(); } catch (Exception) { }
            }
            if (reader != null || mixer != null)
                lock (_sync)
                {
                    mixer?.CancelNext();
                    reader?.Dispose();
                }
        }

        // ────────── переход без паузы и кроссфейд ──────────

        /// <summary>
        /// Готовит следующий трек (готовый файл): за fadeSeconds до конца текущего он начнёт подмешиваться,
        /// а когда текущий кончится — станет текущим без паузы. fadeSeconds = 0 — просто без паузы.
        /// </summary>
        public bool PrepareNext(string path, double fadeSeconds, float gain = 1f)
        {
            var mixer = _mixer;
            if (mixer == null) return false;
            MediaFoundationReader next;
            try
            {
                next = new MediaFoundationReader(path);
            }
            catch (Exception ex)
            {
                Log.Warn("Следующий трек не открылся для бесшовного перехода", ex);
                return false;
            }
            try
            {
                lock (_sync) mixer.SetNext(next, path, Math.Max(0, fadeSeconds), gain);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Следующий трек не подошёл для бесшовного перехода", ex);
                next.Dispose();
                return false;
            }
        }

        public void CancelNext()
        {
            if (_mixer == null) return;
            lock (_sync) _mixer.CancelNext();
        }

        /// <summary>Вызывается из Mixer в потоке звука, под блокировкой _sync.</summary>
        private void OnMixerAdvanced(WaveStream newReader, string path, WaveStream oldReader, float gain)
        {
            _reader = newReader;
            _readerPath = path;
            void Ui()
            {
                // старый источник закрываем в UI-потоке: MediaFoundation не любит освобождение из потока звука
                try { lock (_sync) oldReader.Dispose(); } catch (Exception) { }
                TrackGain = gain;
                AdvancedToNext?.Invoke(this, path);
                MediaOpened?.Invoke(this, EventArgs.Empty);
            }
            if (_ui != null) _ui.Post(_ => Ui(), null);
            else Ui();
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (!ReferenceEquals(sender, _output)) return; // событие от уже закрытого трека

            if (e.Exception == null)
            {
                if (!_stopRequested) MediaEnded?.Invoke(this, EventArgs.Empty);
                return;
            }

            // упало чтение источника — файл битый или оборвалась закачка
            var decode = Interlocked.Exchange(ref _decodeError, null);
            if (decode != null)
            {
                MediaFailed?.Invoke(this, decode is StreamInterruptedException ? decode : new AudioDecodeException(decode));
                return;
            }

            // упал вывод (отключили наушники, сменилось устройство) — пересоздаём его на новом
            // устройстве по умолчанию с той же позиции. Не чаще раза в 2 с, чтобы не зациклиться без устройств.
            if (DateTime.UtcNow - _lastRecovery > TimeSpan.FromSeconds(2) && TryRecreateOutput())
            {
                _lastRecovery = DateTime.UtcNow;
                OutputRecovered?.Invoke(this, CurrentDeviceName());
                return;
            }
            MediaFailed?.Invoke(this, new AudioOutputException(e.Exception));
        }

        private bool TryRecreateOutput()
        {
            var old = _output;
            var source = _chainEnd;
            if (old == null || source == null) return false;

            old.PlaybackStopped -= OnPlaybackStopped;
            try { old.Stop(); } catch (Exception) { }
            try { old.Dispose(); } catch (Exception) { }
            _output = null;

            IWavePlayer fresh;
            try
            {
                fresh = CreateOutput(source);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось пересоздать вывод звука", ex);
                return false;
            }

            _output = fresh;
            _output.PlaybackStopped += OnPlaybackStopped;
            if (_wantPlaying)
            {
                _stopRequested = false;
                try { _output.Play(); }
                catch (Exception) { return false; }
            }
            return true;
        }

        public void Dispose()
        {
            Close();
            try
            {
                if (_watcher != null) _enumerator?.UnregisterEndpointNotificationCallback(_watcher);
            }
            catch (Exception) { }
            _enumerator?.Dispose();
            _enumerator = null;
        }

        /// <summary>
        /// Источник цепочки: текущий трек и (если подготовлен) следующий. Под конец текущего подмешивает
        /// следующий с равномощным кроссфейдом, а когда текущий кончится — переключается на следующий в том же Read.
        /// Формат цепочки фиксируется по первому треку; следующий при необходимости приводится к нему.
        /// </summary>
        private sealed class Mixer : ISampleProvider
        {
            private readonly AudioEngine _engine;
            private WaveStream _reader;
            private ISampleProvider _current;
            private WaveStream? _nextReader;
            private ISampleProvider? _next;
            private string? _nextPath;
            private float _nextGain = 1f;
            private double _fadeSeconds;
            private float[] _temp = Array.Empty<float>();

            public Mixer(AudioEngine engine, WaveStream reader)
            {
                _engine = engine;
                _reader = reader;
                _current = reader.ToSampleProvider();
                WaveFormat = _current.WaveFormat;
            }

            public WaveFormat WaveFormat { get; }
            public bool HasNext => _next != null;

            public void SetNext(WaveStream reader, string path, double fadeSeconds, float gain)
            {
                CancelNext();
                _next = Conform(reader.ToSampleProvider());
                _nextReader = reader;
                _nextPath = path;
                _fadeSeconds = fadeSeconds;
                _nextGain = gain;
            }

            public void CancelNext()
            {
                _nextReader?.Dispose();
                _nextReader = null;
                _next = null;
                _nextPath = null;
            }

            /// <summary>Приводит частоту и число каналов следующего трека к формату цепочки.</summary>
            private ISampleProvider Conform(ISampleProvider source)
            {
                var target = WaveFormat;
                if (source.WaveFormat.Channels == 1 && target.Channels == 2) source = new MonoToStereoSampleProvider(source);
                else if (source.WaveFormat.Channels == 2 && target.Channels == 1) source = new StereoToMonoSampleProvider(source);
                else if (source.WaveFormat.Channels != target.Channels)
                    throw new NotSupportedException($"{source.WaveFormat.Channels} каналов вместо {target.Channels}");
                if (source.WaveFormat.SampleRate != target.SampleRate)
                    source = new WdlResamplingSampleProvider(source, target.SampleRate);
                return source;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _current.Read(buffer, offset, count);

                // кроссфейд: последние секунды текущего смешиваем с началом следующего
                if (_next != null && _fadeSeconds > 0 && read > 0)
                {
                    var channels = WaveFormat.Channels;
                    var rate = (double)WaveFormat.SampleRate;
                    var remaining = (_reader.Length - _reader.Position) / (double)_reader.WaveFormat.AverageBytesPerSecond;
                    // remaining — после этого чтения; начало буфера было на read/channels/rate раньше
                    var bufferSeconds = read / channels / rate;
                    if (remaining <= _fadeSeconds + bufferSeconds)
                    {
                        if (_temp.Length < read) _temp = new float[read];
                        int got = _next.Read(_temp, 0, read);
                        for (int i = 0; i < read; i++)
                        {
                            var frameTime = remaining + bufferSeconds - (i / channels) / rate; // сколько осталось до конца текущего
                            var x = Math.Clamp(frameTime / _fadeSeconds, 0, 1);              // 1 — начало перехода, 0 — конец
                            // равномощный переход: сумма квадратов коэффициентов = 1, громкость не проваливается
                            var outGain = (float)Math.Sin(x * Math.PI / 2);
                            var inGain = (float)Math.Cos(x * Math.PI / 2);
                            var n = i < got ? _temp[i] : 0f;
                            buffer[offset + i] = buffer[offset + i] * outGain + n * inGain;
                        }
                    }
                }

                if (read >= count || _next == null) return read;

                // текущий кончился — следующий становится текущим, остаток буфера дочитываем из него
                var oldReader = _reader;
                _reader = _nextReader!;
                _current = _next;
                var path = _nextPath!;
                var gain = _nextGain;
                _next = null;
                _nextReader = null;
                _nextPath = null;
                _engine.OnMixerAdvanced(_reader, path, oldReader, gain);

                // выравнивание громкости нового трека применит UI через TrackGain (события постятся в UI-поток)
                int more = _current.Read(buffer, offset + read, count - read);
                return read + more;
            }
        }

        /// <summary>Читает источник под блокировкой и запоминает ошибку чтения, чтобы отличить её от ошибки вывода.</summary>
        private sealed class GuardedSource : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly AudioEngine _engine;

            public GuardedSource(ISampleProvider source, AudioEngine engine)
            {
                _source = source;
                _engine = engine;
            }

            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                lock (_engine._sync)
                {
                    try
                    {
                        return _source.Read(buffer, offset, count);
                    }
                    catch (Exception ex)
                    {
                        _engine._decodeError = ex;
                        throw;
                    }
                }
            }
        }

        /// <summary>Пропускает звук без изменений и копирует его в анализатор.</summary>
        private sealed class AnalyzerTap : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly SpectrumAnalyzer _analyzer;

            public AnalyzerTap(ISampleProvider source, SpectrumAnalyzer analyzer)
            {
                _source = source;
                _analyzer = analyzer;
            }

            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);
                if (read > 0) _analyzer.Write(buffer, offset, read, WaveFormat.Channels);
                return read;
            }
        }

        /// <summary>Уведомления Core Audio приходят в чужом потоке — перекладываем в UI-поток.</summary>
        private sealed class DeviceWatcher : IMMNotificationClient
        {
            private readonly AudioEngine _engine;
            private int _pending;

            public DeviceWatcher(AudioEngine engine) => _engine = engine;

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow != DataFlow.Render || role != Role.Multimedia) return;
                // Windows шлёт по событию на каждую роль и иногда пачкой — схлопываем в одно
                if (Interlocked.Exchange(ref _pending, 1) == 1) return;
                void Run()
                {
                    Thread.Sleep(300);
                    Interlocked.Exchange(ref _pending, 0);
                    if (_engine._ui != null) _engine._ui.Post(_ => _engine.OnDefaultDeviceChanged(), null);
                }
                ThreadPool.QueueUserWorkItem(_ => Run());
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
            public void OnDeviceAdded(string pwstrDeviceId) { }
            public void OnDeviceRemoved(string deviceId) { }
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }
    }
}
