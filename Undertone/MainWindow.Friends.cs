using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Лента друзей: кто из компании что слушает (через публичный MQTT, см. FriendsFeed).</summary>
    public partial class MainWindow
    {
        private FriendsFeed? _friendsFeed;
        private FriendsSink? _friendsSink;
        private DispatcherTimer? _friendsRefresh;
        private bool _friendsRefreshQueued;

        private string FriendsDisplayName =>
            _settings.FriendsName.Trim() is { Length: > 0 } n ? n
            : _account is { } a ? Format.PublicName(a.DisplayName.Length > 0 ? a.DisplayName : a.Login)
            : "Друг";

        /// <summary>Включает, выключает или перезапускает ленту по текущим настройкам.</summary>
        private void ApplyFriends()
        {
            if (_friendsSink != null)
            {
                _sinks.Remove(_friendsSink);
                _friendsSink = null;
            }
            LeaveTogether(announce: false);
            _friendsFeed?.Dispose();
            _friendsFeed = null;

            if (_settings.FriendsMemberId.Length == 0)
            {
                _settings.FriendsMemberId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
                PersistSettings();
            }

            if (_settings.FriendsEnabled && FriendsFeed.IsValidCode(_settings.FriendsCode) && _account != null)
            {
                _friendsFeed = new FriendsFeed(_settings.FriendsCode, _settings.FriendsMemberId, FriendsDisplayName);
                _friendsFeed.Changed += QueueFriendsRefresh;
                _friendsFeed.RoomChanged += OnRoomChanged;
                _friendsSink = new FriendsSink(_friendsFeed, () => FriendsDisplayName, () => _inRoom);
                TimeSync.EnsureStarted();
                _sinks.Add(_friendsSink);
                PublishNowPlaying();
            }

            if (_friendsRefresh == null)
            {
                // «5 мин назад» должно тикать само
                _friendsRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _friendsRefresh.Tick += (_, _) =>
                {
                    _friendsSink?.Heartbeat();
                    if (PageFriends.Visibility == Visibility.Visible) RebuildFriends();
                };
                _friendsRefresh.Start();
            }
            RebuildFriends();
        }

        /// <summary>Сообщения от брокеров приходят пачками и в чужом потоке — перерисовываем не чаще раза в полсекунды.</summary>
        private void QueueFriendsRefresh()
        {
            if (_friendsRefreshQueued) return;
            _friendsRefreshQueued = true;
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(500);
                _friendsRefreshQueued = false;
                RebuildFriends();
            });
        }

        private void RebuildFriends()
        {
            var feed = _friendsFeed;
            if (feed == null)
            {
                FriendsList.ItemsSource = null;
                FriendsStatus.Text = "Лента выключена";
                FriendsEmpty.Text = !_settings.FriendsEnabled
                    ? "Включи «Показывать друзьям, что я слушаю» в настройках, создай код компании и отправь его друзьям."
                    : "Нужен код компании: создай его в настройках или вставь код от друга.";
                FriendsEmpty.Visibility = Visibility.Visible;
                FriendsSetupBtn.Visibility = Visibility.Visible;
                UpdateTogetherUi();
                return;
            }

            var friends = feed.Friends.Select(p => new FriendItem(p)).ToList();
            FriendsList.ItemsSource = friends;
            var live = friends.Count(f => f.IsLive);
            var brokers = feed.ConnectedBrokers;
            FriendsStatus.Text = brokers == 0
                ? "Подключаюсь…"
                : friends.Count == 0
                    ? "На связи · пока никого"
                    : $"{friends.Count} {Format.Plural(friends.Count, "друг", "друга", "друзей")} · слушают сейчас: {live}";
            FriendsEmpty.Text = brokers == 0
                ? "Подключаюсь к серверам… Если долго не получается, проверь интернет или VPN."
                : "Пока никого. Отправь друзьям код компании из настроек — и они появятся здесь, как только включат музыку.";
            FriendsEmpty.Visibility = friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FriendsSetupBtn.Visibility = friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateTogetherUi();
        }

        private void FriendsSetup_Click(object sender, RoutedEventArgs e)
        {
            GoTo(NavSettings);
            Dispatcher.InvokeAsync(() => FriendsSettingsHeader.BringIntoView(), DispatcherPriority.Loaded);
        }

        private async Task<YaTrack?> FriendTrackAsync(object sender)
        {
            if (sender is not FrameworkElement { DataContext: FriendItem { HasTrack: true } item } || _api == null) return null;
            var p = item.Presence;
            try
            {
                var id = p.AlbumId.Length > 0 ? $"{p.TrackId}:{p.AlbumId}" : p.TrackId;
                return (await _api.GetTracksAsync(new[] { id })).FirstOrDefault();
            }
            catch (Exception ex)
            {
                ShowToast("Не удалось открыть трек: " + Short(ex.Message));
                return null;
            }
        }

        private async void FriendPlay_Click(object sender, RoutedEventArgs e)
        {
            var name = (sender as FrameworkElement)?.DataContext is FriendItem f ? f.Name : "";
            if (await FriendTrackAsync(sender) is not { } track) return;
            ReportSkip();
            _waveSession = null;
            _waveKey = "";
            _queueLabel = $"СЛУШАЕТ {name.ToUpperInvariant()}";
            _queue.Set(new[] { track }, 0, QueueSource.Collection);
            _queue.Repeat = _settings.Repeat;
            await PlayCurrentAsync();
        }

        private async void FriendQueue_Click(object sender, RoutedEventArgs e)
        {
            if (await FriendTrackAsync(sender) is { } track) PlayNext(track);
        }

        // ────────── настройки ──────────

        private void FillFriendsSettings()
        {
            FriendsToggle.IsChecked = _settings.FriendsEnabled;
            FriendsNameBox.Text = _settings.FriendsName;
            FriendsNameBox.Tag = _account is { } a ? Format.PublicName(a.DisplayName.Length > 0 ? a.DisplayName : a.Login) : "Твоё имя";
            FriendsCodeText.Text = _settings.FriendsCode.Length > 0 ? _settings.FriendsCode : "ещё не создан";
        }

        private void FriendsToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.FriendsEnabled = FriendsToggle.IsChecked == true;
            if (_settings.FriendsEnabled && !FriendsFeed.IsValidCode(_settings.FriendsCode))
            {
                _settings.FriendsCode = FriendsFeed.NewCode();
                FriendsCodeText.Text = _settings.FriendsCode;
                ShowToast("Создан код компании — отправь его друзьям");
            }
            PersistSettings();
            ApplyFriends();
        }

        private void FriendsName_LostFocus(object sender, RoutedEventArgs e) => SaveFriendsName();

        private void FriendsName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SaveFriendsName();
        }

        private void SaveFriendsName()
        {
            var name = FriendsNameBox.Text.Trim();
            if (name == _settings.FriendsName) return;
            _settings.FriendsName = name;
            PersistSettings();
            PublishNowPlaying(); // имя уйдёт вместе со статусом
            _friendsSink?.Heartbeat(force: true);
        }

        private void FriendsCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.FriendsCode.Length == 0)
            {
                ShowToast("Сначала включи ленту или создай код");
                return;
            }
            try
            {
                Clipboard.SetText(_settings.FriendsCode);
                ShowToast("Код скопирован — отправь его друзьям");
            }
            catch (Exception) { ShowToast("Буфер обмена занят — попробуй ещё раз"); }
        }

        private async void FriendsNewCode_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.FriendsCode.Length > 0)
            {
                var ok = await ShowDialogAsync("Создать новый код?",
                    "Друзья со старым кодом перестанут тебя видеть, пока не введут новый.", null, "Создать");
                if (ok == null) return;
            }
            _settings.FriendsCode = FriendsFeed.NewCode();
            _settings.FriendsEnabled = true;
            PersistSettings();
            _fillingSettings = true;
            try { FillFriendsSettings(); } finally { _fillingSettings = false; }
            ApplyFriends();
            ShowToast("Новый код создан — скопируй и отправь друзьям");
        }

        private void FriendsJoin_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            JoinFriends();
        }

        private void FriendsJoin_Click(object sender, RoutedEventArgs e) => JoinFriends();

        private void JoinFriends()
        {
            var code = FriendsJoinBox.Text.Trim();
            if (!FriendsFeed.IsValidCode(code))
            {
                ShowToast("Это не похоже на код компании");
                return;
            }
            // сохраняем как ввели (с дефисами), сравниваются коды без учёта регистра и разделителей
            _settings.FriendsCode = code.ToUpperInvariant();
            _settings.FriendsEnabled = true;
            PersistSettings();
            FriendsJoinBox.Clear();
            _fillingSettings = true;
            try { FillFriendsSettings(); } finally { _fillingSettings = false; }
            ApplyFriends();
            ShowToast("Готово! Друзья появятся на странице «Друзья»");
        }
    }

    /// <summary>Отправляет друзьям свой статус: при смене трека или паузы и раз в минуту, пока играет.</summary>
    internal sealed class FriendsSink : INowPlayingSink
    {
        private readonly FriendsFeed _feed;
        private readonly Func<string> _name;
        private readonly Func<bool> _inRoom;
        private NowPlayingState? _last;
        private string _lastKey = "";
        private DateTime _lastSent = DateTime.MinValue;

        public FriendsSink(FriendsFeed feed, Func<string> name, Func<bool> inRoom)
        {
            _feed = feed;
            _name = name;
            _inRoom = inRoom;
        }

        public void Update(NowPlayingState state, bool positionOnly)
        {
            if (positionOnly) return;
            _last = state;
            var key = $"{state.Track?.Id}|{state.IsPlaying}|{_name()}|{_inRoom()}";
            if (key == _lastKey) return;
            _lastKey = key;
            Send(state);
        }

        /// <summary>Раз в минуту, пока играет музыка, — чтобы друзья видели «слушает сейчас», а не «был 10 мин назад».</summary>
        public void Heartbeat(bool force = false)
        {
            if (_last is not { } state) return;
            // в комнате сигналим и на паузе — иначе через 3 минуты нас перестанут считать участником
            if (!force && ((!state.IsPlaying && !_inRoom()) || DateTime.UtcNow - _lastSent < TimeSpan.FromSeconds(55))) return;
            Send(state);
        }

        private void Send(NowPlayingState state)
        {
            _lastSent = DateTime.UtcNow;
            var t = state.Track;
            _feed.Publish(new FriendPresence
            {
                Name = _name(),
                Playing = state.IsPlaying && t != null,
                TrackId = t?.Id ?? "",
                AlbumId = t?.AlbumId ?? "",
                Title = t?.FullTitle ?? "",
                Artists = t?.ArtistsText ?? "",
                Cover = t?.CoverUrl(200),
                InRoom = _inRoom(),
            });
        }
    }
}
