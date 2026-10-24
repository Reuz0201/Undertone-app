using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// «Слушать вместе»: одна музыка на всю компанию. Общее состояние (трек, пауза, позиция, очередь)
    /// лежит в зашифрованном retained-сообщении room на тех же MQTT-брокерах, что и лента друзей.
    /// Любой участник может его поменять — побеждает последнее изменение; позиция считается от общего времени.
    /// </summary>
    public partial class MainWindow
    {
        private const int RoomQueueMax = 20;
        private static readonly TimeSpan RoomMemberFresh = TimeSpan.FromMinutes(3);

        private bool _inRoom;
        /// <summary>Сейчас применяем чужое состояние — свои изменения в комнату не шлём.</summary>
        private bool _roomApplying;
        /// <summary>Трек включается по команде комнаты — когда зазвучит, встанем на нужное место.</summary>
        private RoomState? _roomPending;
        private DispatcherTimer? _roomTimer;
        private string? _roomWaitEndOf;

        // ────────── вход и выход ──────────

        private void Together_Click(object sender, RoutedEventArgs e)
        {
            if (_inRoom) LeaveTogether(announce: true);
            else JoinTogether();
        }

        private void JoinTogether()
        {
            var feed = _friendsFeed;
            if (feed == null)
            {
                ShowToast("Сначала включи ленту друзей в настройках");
                return;
            }
            TimeSync.EnsureStarted();
            _inRoom = true;
            _roomPending = null;
            _roomWaitEndOf = null;
            InvalidatePreparedNext(); // кроссфейд в комнате выключен — у всех трек должен меняться в одну секунду

            var room = feed.Room;
            var others = RoomFriends();
            if (room is { Active: true } && others.Count > 0)
            {
                ApplyRoomState(room, joining: true);
                ShowToast($"Ты в комнате с {Names(others)}");
            }
            else
            {
                RoomPublishLocal();
                ShowToast(_queue.Current == null
                    ? "Комната открыта — включи что-нибудь, и друзья услышат то же"
                    : "Комната открыта — друзья могут присоединиться на странице «Друзья»", 3.5);
            }

            _roomTimer ??= CreateRoomTimer();
            _roomTimer.Start();
            PublishNowPlaying(force: true); // друзья увидят, что мы в комнате
            UpdateTogetherUi();
        }

        private void LeaveTogether(bool announce)
        {
            if (!_inRoom) return;
            _inRoom = false;
            _roomPending = null;
            _roomWaitEndOf = null;
            _roomTimer?.Stop();

            // уходим последними — закрываем комнату, чтобы следующий открыл новую со своей музыкой
            if (_friendsFeed is { } feed && RoomFriends().Count == 0)
            {
                var now = TimeSync.NowMs;
                feed.PublishRoom(new RoomState { Seq = now, At = now, ByName = FriendsDisplayName, Active = false });
            }
            if (announce)
            {
                PublishNowPlaying(force: true);
                ShowToast("Ты вышел из комнаты — дальше играет только у тебя");
            }
            UpdateTogetherUi();
        }

        /// <summary>Друзья, которые сейчас в комнате и на связи.</summary>
        private List<FriendPresence> RoomFriends()
        {
            if (_friendsFeed is not { } feed) return new List<FriendPresence>();
            var now = DateTimeOffset.UtcNow;
            return feed.Friends
                .Where(f => f.InRoom && f.Online && now - DateTimeOffset.FromUnixTimeSeconds(f.Ts) < RoomMemberFresh)
                .ToList();
        }

        private static string Names(IReadOnlyList<FriendPresence> people)
        {
            var names = people.Select(p => p.Name.Length > 0 ? p.Name : "Друг").ToList();
            return names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} и ещё {names.Count - 3}";
        }

        private void UpdateTogetherUi()
        {
            var feed = _friendsFeed;
            TogetherCard.Visibility = feed == null ? Visibility.Collapsed : Visibility.Visible;
            if (feed == null) return;

            var others = RoomFriends();
            if (_inRoom)
            {
                TogetherTitle.Text = "Вы слушаете вместе";
                TogetherText.Text = others.Count == 0
                    ? "Пока ты один. Друзья увидят кнопку «Присоединиться» здесь же."
                    : $"В комнате: ты, {Names(others)}. Пауза, перемотка и выбор трека — у всех сразу.";
                TogetherBtn.Content = "Выйти";
            }
            else if (feed.Room is { Active: true } && others.Count > 0)
            {
                TogetherTitle.Text = "Друзья слушают вместе";
                var t = feed.Room.Track;
                TogetherText.Text = $"{Names(others)}" + (t != null ? $" · «{t.FullTitle}» — {t.ArtistsText}" : "");
                TogetherBtn.Content = "Присоединиться";
            }
            else
            {
                TogetherTitle.Text = "Слушать вместе";
                TogetherText.Text = "Включите одну музыку на всех: пауза, перемотка и выбор трека — у всех сразу.";
                TogetherBtn.Content = "Слушать вместе";
            }
        }

        // ────────── свои изменения → комната ──────────

        /// <summary>Отправить в комнату то, что сейчас играет у нас. Вызывается после любого своего действия.</summary>
        private void RoomPublishLocal()
        {
            if (!_inRoom || _roomApplying || _friendsFeed is not { } feed) return;
            _roomWaitEndOf = null;
            var now = TimeSync.NowMs;
            var position = _hasMedia ? _player.Position : _resumeAt?.Position ?? TimeSpan.Zero;
            feed.PublishRoom(new RoomState
            {
                Seq = now,
                At = now,
                ByName = FriendsDisplayName,
                Active = true,
                Track = _queue.Current,
                Playing = _hasMedia && _isPlaying,
                Position = position.TotalSeconds,
                Queue = _queue.Upcoming(RoomQueueMax).ToList(),
            });
            QueueFriendsRefresh();
        }

        /// <summary>Трек зазвучал: если его включила комната — встаём на общее место, иначе сообщаем комнате.</summary>
        private void RoomOnTrackStarted(YaTrack track)
        {
            if (!_inRoom) return;
            if (_roomPending is { } pending && pending.Track?.Id == track.Id)
            {
                _roomPending = null;
                AlignToRoom(pending, force: true);
                return;
            }
            _roomPending = null;
            RoomPublishLocal();
        }

        /// <summary>
        /// Трек доиграл. Следующий включает «ведущий» (участник с наименьшим id), остальные ждут его команду
        /// пару секунд — иначе все разом пришлют «следующий» и могут разъехаться. Не дождались — включают сами.
        /// </summary>
        private bool RoomDefersAutoNext()
        {
            if (!_inRoom || _queue.Current is not { } current) return false;
            var me = _friendsFeed?.MemberId ?? "";
            var leader = RoomFriends().Select(f => f.Member).Append(me).Min(StringComparer.Ordinal);
            if (leader == me) return false;

            _roomWaitEndOf = current.Id;
            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => Dispatcher.InvokeAsync(async () =>
            {
                if (!_inRoom || _roomWaitEndOf != current.Id || _queue.Current?.Id != current.Id) return;
                _roomWaitEndOf = null;
                await NextAsync(auto: true);
            }), TaskScheduler.Default);
            return true;
        }

        // ────────── комната → у нас ──────────

        private void OnRoomChanged(RoomState state) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (_inRoom) ApplyRoomState(state, joining: false);
                UpdateTogetherUi();
            });

        private void ApplyRoomState(RoomState state, bool joining)
        {
            if (!state.Active)
            {
                // кто-то решил, что ушёл последним, но мы ещё здесь — комната продолжается с нашей музыкой
                RoomPublishLocal();
                return;
            }

            _roomApplying = true;
            try
            {
                var track = state.Track;
                var current = _queue.Current;
                if (track == null)
                {
                    if (_hasMedia && _isPlaying) SetPlaying(false);
                    return;
                }

                if (current?.Id != track.Id)
                {
                    _roomWaitEndOf = null;
                    ReportSkip();
                    _waveSession = null;
                    _waveKey = "";
                    _queueLabel = "СЛУШАЕМ ВМЕСТЕ";
                    _queue.Set(new[] { track }.Concat(state.Queue), 0, QueueSource.Collection, shuffle: false);
                    _queue.Repeat = RepeatMode.Off;
                    _roomPending = state;
                    _ = PlayCurrentAsync();
                    if (!joining) ShowToast($"{state.ByName} включил «{track.FullTitle}»");
                    return;
                }

                // тот же трек: очередь, пауза, позиция
                if (!_queue.Upcoming(RoomQueueMax).Select(t => t.Id).SequenceEqual(state.Queue.Select(t => t.Id)))
                {
                    _queue.ReplaceUpcoming(state.Queue);
                    QueueEdited();
                }

                if (!_hasMedia)
                {
                    if (_roomPending?.Track?.Id == track.Id) _roomPending = state; // ещё грузится — встанем куда надо, когда зазвучит
                    else
                    {
                        _roomPending = state;
                        _ = PlayCurrentAsync();
                    }
                    return;
                }

                var wasPlaying = _isPlaying;
                var moved = AlignToRoom(state, force: false);
                if (joining) return;
                if (wasPlaying != state.Playing) ShowToast(state.Playing ? $"{state.ByName} продолжил" : $"{state.ByName} поставил на паузу");
                else if (moved) ShowToast($"{state.ByName} перемотал");
            }
            finally
            {
                _roomApplying = false;
            }
        }

        /// <summary>Подгоняет паузу и позицию под комнату. true — перемотали.</summary>
        private bool AlignToRoom(RoomState state, bool force)
        {
            if (!_hasMedia || state.Track == null || state.Track.Id != _queue.Current?.Id) return false;
            var applying = _roomApplying;
            _roomApplying = true;
            try
            {
                var moved = false;
                var expected = state.ExpectedPosition(TimeSync.NowMs);
                var duration = _player.NaturalDuration ?? state.Track.Duration;
                if (expected < duration - TimeSpan.FromSeconds(1.5))
                {
                    var diff = Math.Abs((expected - _player.Position).TotalSeconds);
                    if (diff > (force ? 0.4 : 1.0))
                    {
                        _player.Position = expected;
                        UpdateProgress();
                        moved = true;
                    }
                }
                if (state.Playing != _isPlaying) SetPlaying(state.Playing);
                else if (moved) PublishNowPlaying(force: true);
                return moved;
            }
            finally
            {
                _roomApplying = applying;
            }
        }

        private void SetPlaying(bool playing)
        {
            if (!_hasMedia || _isPlaying == playing) return;
            if (playing) _player.Play();
            else _player.Pause();
            _isPlaying = playing;
            UpdatePlayState();
        }

        /// <summary>Раз в несколько секунд сверяемся с комнатой: если разъехались больше чем на секунду — подправляем.</summary>
        private DispatcherTimer CreateRoomTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                if (!_inRoom || _seeking || _player.IsBuffering || _roomPending != null || _roomWaitEndOf != null) return;
                if (_friendsFeed?.Room is { Active: true } room && room.Track?.Id == _queue.Current?.Id)
                    AlignToRoom(room, force: false);
            };
            return timer;
        }
    }
}
