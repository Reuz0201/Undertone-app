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

        /// <summary>Сейчас играет внешний плеер (Spotify) — команды уходят ему.</summary>
        private IExternalPlayer? _external;

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

        public bool HasMedia => _reader != null || _external != null;

        /// <summary>Звук идёт не через движок, а через внешний плеер (Spotify).</summary>
        public bool IsExternal => _external != null;

        /// <summary>Следующий трек уже подготовлен для бесшовного перехода.</summary>
        public bool HasPreparedNext => _mixer?.HasNext == true;

        /// <summary>Файл текущего трека (null — играет поток идущей закачки).</summary>
        public string? CurrentPath => _readerPath;
        public bool IsPlaying => _external?.IsPlaying ?? _output?.PlaybackState == PlaybackState.Playing;

        /// <summary>Трек играет потоком и ждёт данных из сети.</summary>
        public bool IsBuffering => (_reader as StreamingMp3Reader)?.IsStarved == true;

        /// <summary>Сколько трека скачано (0..1). Для готового файла — 1.</summary>
        public double BufferedFraction =>
            _external != null ? 1 : _reader is StreamingMp3Reader s ? s.BufferedFraction : _reader != null ? 1 : 0;

        public TimeSpan? NaturalDuration => _external != null ? _external.Duration : _reader?.TotalTime;

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
                if (_external != null) return _external.Position;
                var reader = _reader;
                if (reader == null) return TimeSpan.Zero;
                TimeSpan pos;
                lock (_sync) pos = _mixer != null ? TimeSpan.FromSeconds(_mixer.CurrentTime) : reader.CurrentTime;
                if (IsPlaying && !IsBuffering) pos -= TimeSpan.FromMilliseconds(LatencyMs * Effects.EffectiveSpeed); // буфер вывода звучит с учётом скорости
                return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
            }
            set
            {
                if (_external != null)
                {
                    _external.Seek(value < TimeSpan.Zero ? TimeSpan.Zero : value);
                    return;
                }
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
                    _mixer?.OnSeek();
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
        /// <summary>Читается потоком звука в микшере; при переходе микшер сам ставит громкость нового трека.</summary>
        private volatile float _trackGain = 1f;

        /// <summary>Задержка между анализом и звуком, в сэмплах — визуализатор компенсирует её.</summary>
        public int AnalyzerDelaySamples => Analyzer.SampleRate * LatencyMs / 1000;

        private void ApplyVolume()
        {
            // кубическая кривая — слайдер ощущается линейно на слух
            // громкость трека (нормализация) применяет микшер — у каждого трека во время перехода своя
            if (_volume != null) _volume.Volume = _muted ? 0f : _level * _level * _level;
            _external?.SetVolume(_muted ? 0 : _level * _level * _level);
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

        /// <summary>Трек играет внешний плеер (Spotify): движок только пересылает ему команды и события.</summary>
        public void OpenExternal(IExternalPlayer player)
        {
            Close();
            _external = player;
            player.Ended += OnExternalEnded;
            player.Failed += OnExternalFailed;
            ApplyVolume();
            Analyzer.Reset(44100);
            MediaOpened?.Invoke(this, EventArgs.Empty);
        }

        private void OnExternalEnded(object? sender, EventArgs e)
        {
            if (ReferenceEquals(sender, _external)) MediaEnded?.Invoke(this, EventArgs.Empty);
        }

        private void OnExternalFailed(object? sender, Exception ex)
        {
            if (ReferenceEquals(sender, _external)) MediaFailed?.Invoke(this, ex);
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
            if (_external != null)
            {
                _external.Play();
                return;
            }
            if (_output == null) return;
            _stopRequested = false;
            _wantPlaying = true;
            _output.Play();
        }

        public void Pause()
        {
            _wantPlaying = false;
            _external?.Pause();
            _output?.Pause();
        }

        /// <summary>Как MediaPlayer.Stop: стоп и перемотка в начало. MediaEnded не вызывается.</summary>
        public void Stop()
        {
            if (_external != null)
            {
                _external.Stop();
                return;
            }
            if (_output == null || _reader == null) return;
            _stopRequested = true;
            _wantPlaying = false;
            _output.Stop();
            lock (_sync) _reader.CurrentTime = TimeSpan.Zero;
            _effects?.Flush();
        }

        public void Close()
        {
            if (_external is { } external)
            {
                // сам плеер не закрываем — он общий на все треки Spotify; только отписываемся
                external.Ended -= OnExternalEnded;
                external.Failed -= OnExternalFailed;
                _external = null;
            }
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
            var reader = _reader;
            if (reader == null) return false;
            var fade = Math.Max(0, fadeSeconds);
            var plan = new TransitionPlan(reader.TotalTime.TotalSeconds - fade, fade, 0, 1, BassSwap: false, BeatMatched: false);
            return PrepareCore(path, plan, cut: false, gain);
        }

        /// <summary>
        /// DJ-переход по плану: следующий трек вступает в plan.MixOutAt текущего, к концу перехода текущий
        /// обрывается (дальше — только новый). Поздно (текущий уже дальше точки входа) — false.
        /// </summary>
        public bool PrepareTransition(string path, TransitionPlan plan, float gain = 1f)
        {
            if (_reader == null || plan.Length <= 0) return false;
            if (Position.TotalSeconds > plan.MixOutAt - 0.2) return false;
            return PrepareCore(path, plan, cut: true, gain);
        }

        private bool PrepareCore(string path, TransitionPlan plan, bool cut, float gain)
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
                lock (_sync) mixer.SetNext(next, path, plan, cut, Math.Clamp(gain, 0.1f, 4f));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Следующий трек не подошёл для бесшовного перехода", ex);
                next.Dispose();
                return false;
            }
        }

        /// <summary>Идёт переход к следующему треку: 0..1; иначе -1.</summary>
        public double MixProgress => _mixer?.Progress ?? -1;

        /// <summary>Скорость текущего трека в микшере (после DJ-перехода — пока плавно возвращается к 1).</summary>
        public double DeckRate => _mixer?.CurrentRate ?? 1;

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
        /// Источник цепочки: «деки» — текущий трек и (если подготовлен) следующий. С точки перехода
        /// подмешивает следующий: обычный кроссфейд (равномощный) или DJ-сведение — следующий заходит в такт,
        /// разогнанный под темп текущего, а посередине бас уходящего трека меняется на бас нового.
        /// Когда переход кончился (или текущий трек), следующий становится текущим в том же Read.
        /// Формат цепочки фиксируется по первому треку; следующий при необходимости приводится к нему.
        /// </summary>
        private sealed class Mixer : ISampleProvider
        {
            /// <summary>За сколько секунд после DJ-перехода скорость нового трека возвращается к записанной.</summary>
            private const double GlideSeconds = 8;

            private readonly AudioEngine _engine;
            private Deck _cur;
            private Deck? _next;
            private TransitionPlan? _plan;
            /// <summary>true — DJ-переход: уходящий трек обрывается в конце перехода, не доигрывая хвост.</summary>
            private bool _cut;
            private float[] _temp = Array.Empty<float>();

            public Mixer(AudioEngine engine, WaveStream reader)
            {
                _engine = engine;
                var source = reader.ToSampleProvider();
                WaveFormat = source.WaveFormat;
                _cur = new Deck(reader, source, null, 1f);
            }

            public WaveFormat WaveFormat { get; }
            public bool HasNext => _next != null;

            /// <summary>Идёт переход: 0..1, иначе -1.</summary>
            public double Progress { get; private set; } = -1;

            public double CurrentRate => _cur.Speed?.Rate ?? 1;

            /// <summary>Сколько текущего трека прозвучало, секунд (без буфера подгона темпа).</summary>
            public double CurrentTime => Math.Max(0, _cur.Time);

            public void SetNext(WaveStream reader, string path, TransitionPlan plan, bool cut, float gain)
            {
                CancelNext();
                if (plan.NextStartAt > 0.01 && plan.NextStartAt < reader.TotalTime.TotalSeconds - 1)
                    reader.CurrentTime = TimeSpan.FromSeconds(plan.NextStartAt);
                var source = Conform(reader.ToSampleProvider());
                Varispeed? speed = null;
                if (Math.Abs(plan.Rate - 1) > 0.0005)
                {
                    speed = new Varispeed(source, plan.Rate);
                    source = speed;
                }
                var next = new Deck(reader, source, path, gain) { Speed = speed };
                if (plan.BassSwap)
                {
                    // новый трек — с фильтров с первого сэмпла (он входит из тишины, щелчка нет);
                    // уходящему — плавно, чтобы смена фазы баса не щёлкнула
                    next.Split = new Crossover(WaveFormat.SampleRate, WaveFormat.Channels, warm: false);
                    _cur.Split ??= new Crossover(WaveFormat.SampleRate, WaveFormat.Channels, warm: true);
                }
                _next = next;
                _plan = plan;
                _cut = cut;
            }

            public void CancelNext()
            {
                _next?.Reader.Dispose();
                _next = null;
                _plan = null;
                _cut = false;
                Progress = -1;
            }

            /// <summary>Перемотка: накопленное в деке до перемотки больше не нужно.</summary>
            public void OnSeek() => _cur.Speed?.Reset();

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
                var channels = WaveFormat.Channels;
                var t0 = _cur.Time;
                int read = _cur.Source.Read(buffer, offset, count);
                var t1 = _cur.Time;
                var frames = read / channels;
                var curGain = _engine._trackGain;
                var plan = _plan;

                bool mixing = _next != null && plan != null && plan.Length > 0 && frames > 0 && t1 >= plan.MixOutAt;
                if (!mixing)
                {
                    // вне перехода — только громкость трека и (если остались после перехода) фильтры баса
                    _cur.Process(buffer, offset, frames, channels, curGain, 1f, 1f);
                }
                else
                {
                    // с какого кадра буфера начинается переход (время внутри буфера — линейно между t0 и t1)
                    var dt = frames > 0 ? (t1 - t0) / frames : 0;
                    int f0 = 0;
                    if (t0 < plan!.MixOutAt && dt > 0) f0 = Math.Clamp((int)Math.Ceiling((plan.MixOutAt - t0) / dt), 0, frames);
                    // до перехода — как обычно
                    if (f0 > 0) _cur.Process(buffer, offset, f0, channels, curGain, 1f, 1f);

                    var mixFrames = frames - f0;
                    var need = mixFrames * channels;
                    if (_temp.Length < need) _temp = new float[need];
                    int got = _next!.Source.Read(_temp, 0, need) / channels;
                    if (got < mixFrames) Array.Clear(_temp, got * channels, (mixFrames - got) * channels);

                    for (int f = 0; f < mixFrames; f++)
                    {
                        var t = t0 + (f0 + f) * dt;
                        var p = Math.Clamp((t - plan.MixOutAt) / plan.Length, 0, 1);
                        DjFades.Gains(p, plan.BassSwap, out var outLo, out var outHi, out var inLo, out var inHi);
                        var b = offset + (f0 + f) * channels;
                        var n = f * channels;
                        _cur.ProcessFrame(buffer, b, channels, curGain, outLo, outHi);
                        _next.ProcessFrame(_temp, n, channels, _next.Gain, inLo, inHi);
                        for (int c = 0; c < channels; c++) buffer[b + c] += _temp[n + c];
                    }
                    Progress = Math.Clamp((t1 - plan.MixOutAt) / plan.Length, 0, 1);

                    // DJ-переход кончился — уходящий трек больше не играет, следующий становится текущим
                    if (_cut && t1 >= plan.MixOutEnd)
                    {
                        Advance();
                        return read;
                    }
                }

                if (read >= count || _next == null) return read;

                // текущий кончился — следующий становится текущим, остаток буфера дочитываем из него
                Advance();
                int more = _cur.Source.Read(buffer, offset + read, count - read);
                _cur.Process(buffer, offset + read, more / channels, channels, _engine._trackGain, 1f, 1f);
                return read + more;
            }

            private void Advance()
            {
                var old = _cur;
                var next = _next!;
                _cur = next;
                _next = null;
                _plan = null;
                _cut = false;
                Progress = -1;
                // громкость нового трека — сразу здесь, в потоке звука; UI потом выставит то же значение
                _engine._trackGain = next.Gain;
                next.Gain = 1f;
                // разогнанный под прошлый трек — плавно возвращаем к записанной скорости
                next.Speed?.GlideTo(1, (int)(GlideSeconds * WaveFormat.SampleRate));
                _engine.OnMixerAdvanced(_cur.Reader, next.Path!, old.Reader, _engine._trackGain);
            }

        }

        /// <summary>Дека микшера: трек, его цепочка до микшера и фильтры баса для DJ-перехода.</summary>
        private sealed class Deck
        {
            public Deck(WaveStream reader, ISampleProvider source, string? path, float gain)
            {
                Reader = reader;
                Source = source;
                Path = path;
                Gain = gain;
            }

            public WaveStream Reader { get; }
            public ISampleProvider Source { get; }
            public string? Path { get; }
            /// <summary>Громкость трека, пока он «следующий» (у текущего — общий TrackGain).</summary>
            public float Gain { get; set; }
            public Varispeed? Speed { get; init; }

            /// <summary>Сколько трека уже прозвучало (секунд от начала файла) — без того, что сидит в буфере скорости.</summary>
            public double Time => Reader.CurrentTime.TotalSeconds - (Speed?.BufferedSeconds ?? 0);
            public Crossover? Split { get; set; }

            public void Process(float[] buffer, int offset, int frames, int channels, float gain, float lo, float hi)
            {
                if (Split == null)
                {
                    if (Math.Abs(gain - 1f) < 1e-6f) return;
                    for (int i = 0, n = frames * channels; i < n; i++) buffer[offset + i] *= gain;
                    return;
                }
                for (int f = 0; f < frames; f++) ProcessFrame(buffer, offset + f * channels, channels, gain, lo, hi);
            }

            public void ProcessFrame(float[] buffer, int offset, int channels, float gain, float lo, float hi)
            {
                if (Split == null)
                {
                    // без фильтров низ и верх неразделимы — берём среднее (так и задуман обычный кроссфейд: lo == hi)
                    var g = gain * (lo + hi) * 0.5f;
                    for (int c = 0; c < channels; c++) buffer[offset + c] *= g;
                    return;
                }
                Split.Process(buffer, offset, channels, gain * lo, gain * hi);
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
