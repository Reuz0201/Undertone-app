using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Undertone.Core;

namespace Undertone.Audio
{
    /// <summary>
    /// Воспроизведение на NAudio с API в духе WPF MediaPlayer.
    /// Цепочка: MediaFoundationReader → сэмплы → «отвод» в анализатор → громкость → WASAPI (запасной вариант — WaveOut).
    /// Анализатор получает сигнал ДО регулятора громкости — визуализация не «сжимается» при тихом звуке, как в Winamp.
    /// Все события приходят в UI-поток (NAudio постит их в SynchronizationContext, где создан плеер).
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        public const int LatencyMs = 100;

        private IWavePlayer? _output;
        private MediaFoundationReader? _reader;
        private VolumeSampleProvider? _volume;
        private bool _stopRequested;
        private float _level = 0.7f;
        private bool _muted;

        public SpectrumAnalyzer Analyzer { get; } = new();

        public event EventHandler? MediaOpened;
        public event EventHandler? MediaEnded;
        public event EventHandler<Exception>? MediaFailed;

        public bool HasMedia => _reader != null;
        public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

        public TimeSpan? NaturalDuration => _reader?.TotalTime;

        /// <summary>Позиция с поправкой на буфер вывода — то, что сейчас реально звучит.</summary>
        public TimeSpan Position
        {
            get
            {
                if (_reader == null) return TimeSpan.Zero;
                var pos = _reader.CurrentTime;
                if (IsPlaying) pos -= TimeSpan.FromMilliseconds(LatencyMs);
                return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
            }
            set
            {
                if (_reader == null) return;
                var max = _reader.TotalTime - TimeSpan.FromMilliseconds(200);
                if (value > max) value = max;
                if (value < TimeSpan.Zero) value = TimeSpan.Zero;
                // MediaFoundationReader переставляет позицию в своём Read — потокобезопасно для нашего сценария
                _reader.CurrentTime = value;
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

        /// <summary>Задержка между анализом и звуком, в сэмплах — визуализатор компенсирует её.</summary>
        public int AnalyzerDelaySamples => Analyzer.SampleRate * LatencyMs / 1000;

        private void ApplyVolume()
        {
            // кубическая кривая — слайдер ощущается линейно на слух
            if (_volume != null) _volume.Volume = _muted ? 0f : _level * _level * _level;
        }

        public void Open(string path)
        {
            Close();
            try
            {
                var reader = new MediaFoundationReader(path);
                var samples = reader.ToSampleProvider();
                var tap = new AnalyzerTap(samples, Analyzer);
                _volume = new VolumeSampleProvider(tap);
                ApplyVolume();

                _output = CreateOutput(_volume);
                _output.PlaybackStopped += OnPlaybackStopped;
                _reader = reader;
                Analyzer.Reset(reader.WaveFormat.SampleRate);
                MediaOpened?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Close();
                MediaFailed?.Invoke(this, ex);
            }
        }

        private static IWavePlayer CreateOutput(ISampleProvider source)
        {
            // WASAPI shared — основной вариант; WaveOut — если WASAPI не завёлся (старые драйверы и т.п.)
            try
            {
                var wasapi = new WasapiOut(AudioClientShareMode.Shared, false, LatencyMs);
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
            catch (Exception)
            {
                var waveOut = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
                waveOut.Init(source);
                return waveOut;
            }
        }

        public void Play()
        {
            if (_output == null) return;
            _stopRequested = false;
            _output.Play();
        }

        public void Pause() => _output?.Pause();

        /// <summary>Как MediaPlayer.Stop: стоп и перемотка в начало. MediaEnded не вызывается.</summary>
        public void Stop()
        {
            if (_output == null || _reader == null) return;
            _stopRequested = true;
            _output.Stop();
            _reader.CurrentTime = TimeSpan.Zero;
        }

        public void Close()
        {
            var output = _output;
            var reader = _reader;
            _output = null;
            _reader = null;
            _volume = null;
            if (output != null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                _stopRequested = true;
                try { output.Stop(); } catch (Exception) { }
                output.Dispose();
            }
            reader?.Dispose();
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (!ReferenceEquals(sender, _output)) return; // событие от уже закрытого трека
            if (e.Exception != null)
                MediaFailed?.Invoke(this, e.Exception);
            else if (!_stopRequested)
                MediaEnded?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => Close();

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
    }
}
