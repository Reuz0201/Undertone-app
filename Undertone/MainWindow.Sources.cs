using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Undertone.Core;

namespace Undertone
{
    /// <summary>
    /// Откуда брать звук трека в выбранном качестве: кэш, MP3 (download-info) или lossless (get-file-info),
    /// плюс отчёты о прослушиваниях для Яндекса и локальная история.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly string[] LosslessExts = { "flac", "mp4", "m4a", "aac" };

        /// <summary>Сколько раз подряд не получилось lossless; после двух — до перезапуска играем MP3.</summary>
        private int _losslessFailures;
        private bool _losslessWarned;

        /// <summary>Треки, у которых нет lossless-версии: для них в режиме «без потерь» годится MP3.</summary>
        private readonly HashSet<string> _noLossless = new();

        /// <summary>Громкость трека (LUFS), если Яндекс её сообщил, — для выравнивания.</summary>
        private readonly Dictionary<string, double> _loudness = new();

        private AudioQuality EffectiveQuality =>
            _settings.Quality == AudioQuality.Lossless && (_losslessFailures >= 2 || _account is { HasPlus: false })
                ? AudioQuality.High
                : _settings.Quality;

        private string Mp3Ext => EffectiveQuality == AudioQuality.Economy ? "lq.mp3" : "mp3";

        private Func<Task<string>> Mp3Resolver(YaApi api, YaTrack track)
        {
            var lowest = EffectiveQuality == AudioQuality.Economy;
            return () => api.GetDirectUrlAsync(track.Id, lowest);
        }

        /// <summary>Готовый файл трека в кэше для текущего качества. В «Экономии» годится и обычный MP3 — он лучше.</summary>
        private string? FindCachedFile(YaTrack track) => EffectiveQuality switch
        {
            AudioQuality.Lossless => _cache.FindCached(track.Id, LosslessExts)
                                     ?? (_noLossless.Contains(track.Id) ? _cache.FindCached(track.Id, "mp3") : null),
            AudioQuality.Economy => _cache.FindCached(track.Id, "lq.mp3", "mp3"),
            _ => _cache.FindCached(track.Id, "mp3"),
        };

        private static bool IsLosslessFile(string path)
        {
            var ext = Path.GetExtension(path).TrimStart('.');
            return Array.IndexOf(LosslessExts, ext.ToLowerInvariant()) >= 0;
        }

        private static string ExtFor(string codec) => codec switch
        {
            "flac" => "flac",
            "flac-mp4" => "mp4",
            "aac" or "he-aac" => "aac",
            "aac-mp4" or "he-aac-mp4" => "m4a",
            _ => "mp3",
        };

        private void MarkLosslessFailed()
        {
            _losslessFailures++;
            if (_losslessWarned) return;
            _losslessWarned = true;
            ShowToast("Качество «без потерь» сейчас недоступно — играю MP3", 4);
        }

        /// <summary>Скачивает трек без потерь целиком. null — не получилось, играем MP3.</summary>
        private async Task<string?> DownloadLosslessAsync(YaApi api, YaTrack track, CancellationToken ct)
        {
            try
            {
                var first = await api.GetFileInfoAsync(track.Id, "lossless", ct);
                var ext = ExtFor(first.Codec);
                // для трека нет lossless-версии — сервер отдал MP3/AAC: такой файл возьмём обычным путём
                if (ext is "mp3")
                {
                    _noLossless.Add(track.Id);
                    return null;
                }
                if (first.LoudnessLufs is { } lufs) _loudness[track.Id] = lufs;

                var used = false;
                var path = await _cache.GetAsync(track.Id, async () =>
                {
                    // первую попытку — по уже полученной ссылке, повторы — со свежей
                    var info = used ? await api.GetFileInfoAsync(track.Id, "lossless") : first;
                    used = true;
                    return new CacheSource(info.Url, info.Key);
                }, ext, ct);
                _losslessFailures = 0;
                return path;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"Lossless для {track.Id} не получен", ex);
                MarkLosslessFailed();
                return null;
            }
        }

        /// <summary>Множитель громкости для выравнивания: к −14 LUFS, не громче +6 дБ.</summary>
        private float GainFor(YaTrack track)
        {
            if (!_settings.Normalize || !_loudness.TryGetValue(track.Id, out var lufs)) return 1f;
            var db = Math.Clamp(-14.0 - lufs, -12.0, 6.0);
            return (float)Math.Pow(10, db / 20.0);
        }

        // ────────── отчёты о прослушиваниях ──────────

        private YaTrack? _reportTrack;
        private string _reportPlayId = "";

        private void BeginPlayReport(YaTrack track)
        {
            _reportTrack = track;
            _reportPlayId = Guid.NewGuid().ToString();
            AddToHistory(track);
        }

        /// <summary>Трек закончился или его переключили — сообщаем Яндексу, сколько прослушано.</summary>
        private void FlushPlayReport(bool atEnd = false)
        {
            var track = _reportTrack;
            _reportTrack = null;
            if (track == null || _api == null || !_settings.ReportPlays) return;
            var end = atEnd ? track.Duration.TotalSeconds : _hasMedia ? _player.Position.TotalSeconds : 0;
            if (end < 3) return; // случайный клик — не считаем
            _ = _api.ReportPlayAsync(track, end, end, _reportPlayId);
        }
    }
}
