using System;
using System.Windows;
using System.Windows.Interop;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// Состояние «сейчас играет» для всего, что живёт вне окна: SMTC (Windows), трей, мини-плеер, Discord.
    /// Окно публикует состояние в одном месте (PublishNowPlaying), а не дёргает каждую интеграцию по отдельности
    /// из десятка мест — так новые интеграции подключаются одной строкой.
    /// </summary>
    public partial class MainWindow
    {
        private MediaControls? _media;

        /// <summary>Подписчики на состояние воспроизведения (мини-плеер, Discord и т.п.).</summary>
        private readonly System.Collections.Generic.List<INowPlayingSink> _sinks = new();

        private void InitMediaControls()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _media = MediaControls.TryCreate(hwnd);
            if (_media == null)
            {
                // старая Windows — обходимся глобальными медиаклавишами
                _mediaKeys = new MediaKeyHook(this, OnGlobalMediaKey);
                return;
            }
            _media.PlayRequested += () => Dispatcher.InvokeAsync(() => { if (!_isPlaying) TogglePlay(); });
            _media.PauseRequested += () => Dispatcher.InvokeAsync(() => { if (_isPlaying) TogglePlay(); });
            _media.NextRequested += () => Dispatcher.InvokeAsync(() => _ = NextAsync(auto: false));
            _media.PreviousRequested += () => Dispatcher.InvokeAsync(() => _ = PrevAsync());
            _media.SeekRequested += pos => Dispatcher.InvokeAsync(() =>
            {
                if (!_hasMedia) return;
                _player.Position = pos;
                UpdateProgress();
                PublishNowPlaying(force: true);
            });
        }

        // ────────── мини-плеер ──────────

        private MiniPlayer? _mini;

        /// <summary>Мини-плеер заменяет большое окно: открываем его — большое уходит в трей.</summary>
        private void ToggleMiniPlayer()
        {
            if (_mini != null)
            {
                _mini.Close();
                return;
            }
            var mini = new MiniPlayer(_settings);
            mini.PlayPauseRequested += TogglePlay;
            mini.NextRequested += () => _ = NextAsync(auto: false);
            mini.PrevRequested += () => _ = PrevAsync();
            mini.LikeRequested += () => { if (_queue.Current is { } t) _ = ToggleLikeAsync(t); };
            mini.ExpandRequested += () =>
            {
                _mini?.Close();
                ShowFromTray();
            };
            mini.Closed += (_, _) =>
            {
                _sinks.Remove(mini);
                if (_mini == mini) _mini = null;
                PersistSettings();
            };
            _mini = mini;
            _sinks.Add(mini);
            mini.Show();
            PublishNowPlaying(force: true);
            if (IsVisible) HideToTray();
        }

        private void MiniPlayer_Click(object sender, RoutedEventArgs e) => ToggleMiniPlayer();

        /// <param name="positionOnly">Вызов из таймера прогресса: обновить только позицию (дёшево).</param>
        /// <param name="force">Сразу отправить позицию в SMTC (после перемотки).</param>
        private void PublishNowPlaying(bool positionOnly = false, bool force = false)
        {
            var track = _queue.Current;
            var position = _hasMedia ? _player.Position : _resumeAt?.Position ?? TimeSpan.Zero;
            var duration = _hasMedia ? _player.NaturalDuration ?? track?.Duration ?? TimeSpan.Zero : track?.Duration ?? TimeSpan.Zero;

            _media?.Update(track, _isPlaying, position, duration, force);
            if (!positionOnly) UpdateTray();

            if (_sinks.Count == 0) return;
            var state = new NowPlayingState(track, _isPlaying, position, duration,
                track != null && _likedIds.Contains(track.Id), _queueLabel);
            foreach (var sink in _sinks)
            {
                try { sink.Update(state, positionOnly); }
                catch (Exception ex) { Log.Warn($"{sink.GetType().Name}: ошибка обновления", ex); }
            }
        }
    }

    public sealed record NowPlayingState(
        YaTrack? Track, bool IsPlaying, TimeSpan Position, TimeSpan Duration, bool IsLiked, string Source);

    /// <summary>Получает состояние «сейчас играет». Вызывается в UI-потоке.</summary>
    public interface INowPlayingSink
    {
        void Update(NowPlayingState state, bool positionOnly);
    }
}

namespace Undertone
{
    /// <summary>Discord: шлёт статус только при смене трека, паузе или перемотке (не каждые 200 мс).</summary>
    internal sealed class DiscordSink : INowPlayingSink, System.IDisposable
    {
        private readonly Undertone.Core.DiscordPresence _presence;
        private string _last = "";

        public DiscordSink(string appId) => _presence = new Undertone.Core.DiscordPresence(appId);

        public void Update(NowPlayingState state, bool positionOnly)
        {
            if (positionOnly) return;
            // момент начала трека, округлённый до 2 с: перемотка его меняет, обычная игра — нет
            var start = (System.DateTime.UtcNow - state.Position).Ticks / System.TimeSpan.FromSeconds(2).Ticks;
            var key = $"{state.Track?.Id}|{state.IsPlaying}|{(state.IsPlaying ? start : 0)}";
            if (key == _last) return;
            _last = key;
            _presence.Set(state.Track, state.IsPlaying, state.Position, state.Duration);
        }

        public void Dispose() => _presence.Dispose();
    }
}
