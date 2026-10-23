using System;
using Undertone.Core;
using WinMedia = global::Windows.Media;
using WinStreams = global::Windows.Storage.Streams;

namespace Undertone.UI
{
    /// <summary>
    /// System Media Transport Controls: обложка и название в оверлее громкости Windows и на экране блокировки,
    /// управление с Bluetooth-наушников и медиаклавиш. Заменяет MediaKeyHook, если Windows его поддерживает.
    /// События кнопок приходят в чужом потоке — подписчик сам перекладывает их в UI-поток.
    /// </summary>
    internal sealed class MediaControls : IDisposable
    {
        private readonly WinMedia.SystemMediaTransportControls _smtc;
        private string? _trackId;
        private DateTime _lastTimeline = DateTime.MinValue;

        public event Action? PlayRequested;
        public event Action? PauseRequested;
        public event Action? NextRequested;
        public event Action? PreviousRequested;
        public event Action<TimeSpan>? SeekRequested;

        private MediaControls(WinMedia.SystemMediaTransportControls smtc)
        {
            _smtc = smtc;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.IsStopEnabled = false;
            _smtc.PlaybackStatus = WinMedia.MediaPlaybackStatus.Closed;
            _smtc.ButtonPressed += OnButtonPressed;
            _smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;
        }

        /// <summary>null — Windows старше 10 1809 или SMTC недоступен; тогда работают обычные медиаклавиши.</summary>
        public static MediaControls? TryCreate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return null;
            try
            {
                var smtc = WinMedia.SystemMediaTransportControlsInterop.GetForWindow(hwnd);
                return new MediaControls(smtc);
            }
            catch (Exception ex)
            {
                Log.Warn("SMTC недоступен, остаются обычные медиаклавиши", ex);
                return null;
            }
        }

        private void OnButtonPressed(WinMedia.SystemMediaTransportControls sender,
            WinMedia.SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            switch (args.Button)
            {
                case WinMedia.SystemMediaTransportControlsButton.Play: PlayRequested?.Invoke(); break;
                case WinMedia.SystemMediaTransportControlsButton.Pause: PauseRequested?.Invoke(); break;
                case WinMedia.SystemMediaTransportControlsButton.Next: NextRequested?.Invoke(); break;
                case WinMedia.SystemMediaTransportControlsButton.Previous: PreviousRequested?.Invoke(); break;
            }
        }

        private void OnPositionChangeRequested(WinMedia.SystemMediaTransportControls sender,
            WinMedia.PlaybackPositionChangeRequestedEventArgs args) =>
            SeekRequested?.Invoke(args.RequestedPlaybackPosition);

        /// <summary>Обновляет всё сразу; дорогие части (обложка) — только при смене трека.</summary>
        public void Update(YaTrack? track, bool playing, TimeSpan position, TimeSpan duration, bool force = false)
        {
            try
            {
                if (track == null)
                {
                    _trackId = null;
                    _smtc.PlaybackStatus = WinMedia.MediaPlaybackStatus.Closed;
                    _smtc.DisplayUpdater.ClearAll();
                    _smtc.DisplayUpdater.Update();
                    return;
                }

                if (track.Id != _trackId)
                {
                    _trackId = track.Id;
                    var u = _smtc.DisplayUpdater;
                    u.ClearAll();
                    u.Type = WinMedia.MediaPlaybackType.Music;
                    u.MusicProperties.Title = track.FullTitle;
                    u.MusicProperties.Artist = track.ArtistsText;
                    u.MusicProperties.AlbumTitle = track.Album;
                    if (track.CoverUrl(400) is { } cover)
                        u.Thumbnail = WinStreams.RandomAccessStreamReference.CreateFromUri(new Uri(cover));
                    u.Update();
                    force = true;
                }

                var status = playing ? WinMedia.MediaPlaybackStatus.Playing : WinMedia.MediaPlaybackStatus.Paused;
                if (_smtc.PlaybackStatus != status)
                {
                    _smtc.PlaybackStatus = status;
                    force = true;
                }

                // ползунок в оверлее Windows сам двигается, пока статус Playing — шлём позицию изредка
                if (!force && DateTime.UtcNow - _lastTimeline < TimeSpan.FromSeconds(5)) return;
                _lastTimeline = DateTime.UtcNow;
                var end = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);
                var timeline = new WinMedia.SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    Position = position < end ? position : end,
                    MaxSeekTime = end,
                    EndTime = end,
                };
                _smtc.UpdateTimelineProperties(timeline);
            }
            catch (Exception ex)
            {
                Log.Warn("SMTC: не удалось обновить", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                _smtc.ButtonPressed -= OnButtonPressed;
                _smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
                _smtc.PlaybackStatus = WinMedia.MediaPlaybackStatus.Closed;
                _smtc.DisplayUpdater.ClearAll();
                _smtc.DisplayUpdater.Update();
                _smtc.IsEnabled = false;
            }
            catch (Exception) { }
        }
    }
}
