using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Undertone.Core;

namespace Undertone.Audio
{
    /// <summary>Раскодирует файл в моно с частотой для анализа — для DJ-режима.</summary>
    public static class DjDecoder
    {
        /// <summary>Дольше не анализируем: это уже не трек, а сет или подкаст — сводить его по тактам незачем.</summary>
        public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(15);

        /// <summary>null — файл слишком длинный или не раскодировался.</summary>
        public static float[]? DecodeMono(string path, CancellationToken ct)
        {
            try
            {
                using var reader = new MediaFoundationReader(path);
                if (reader.TotalTime > MaxDuration) return null;
                ISampleProvider source = reader.ToSampleProvider();
                source = source.WaveFormat.Channels switch
                {
                    1 => source,
                    2 => new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f },
                    _ => new DownmixProvider(source),
                };
                if (source.WaveFormat.SampleRate != DjAnalyzer.PreferredSampleRate)
                    source = new WdlResamplingSampleProvider(source, DjAnalyzer.PreferredSampleRate);

                var expected = (int)(reader.TotalTime.TotalSeconds * DjAnalyzer.PreferredSampleRate) + DjAnalyzer.PreferredSampleRate;
                var result = new List<float>(Math.Max(expected, DjAnalyzer.PreferredSampleRate));
                var buffer = new float[DjAnalyzer.PreferredSampleRate];
                var limit = (int)(MaxDuration.TotalSeconds * DjAnalyzer.PreferredSampleRate);
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < read; i++) result.Add(buffer[i]);
                    if (result.Count > limit) return null;
                }
                return result.ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"DJ-режим: не удалось раскодировать {System.IO.Path.GetFileName(path)} для анализа", ex);
                return null;
            }
        }

        /// <summary>Многоканальный звук (5.1 во FLAC бывает) — в моно средним по каналам.</summary>
        private sealed class DownmixProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _channels;
            private float[] _temp = Array.Empty<float>();

            public DownmixProvider(ISampleProvider source)
            {
                _source = source;
                _channels = source.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                var need = count * _channels;
                if (_temp.Length < need) _temp = new float[need];
                var read = _source.Read(_temp, 0, need) / _channels;
                for (int i = 0; i < read; i++)
                {
                    float s = 0;
                    for (int c = 0; c < _channels; c++) s += _temp[i * _channels + c];
                    buffer[offset + i] = s / _channels;
                }
                return read;
            }
        }
    }
}
