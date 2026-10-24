using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Каркас окна: запуск, навигация, списки, поиск, хоткеи. Плеер — в MainWindow.Playback.cs, вход — в MainWindow.Auth.cs.</summary>
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings = AppSettings.Load();
        private readonly EnvFile _env = EnvFile.Locate();
        private readonly TokenStore _yaTokens;
        private readonly TokenStore _scTokens;
        private SoundCloudConfig? _scConfig;
        private readonly AudioCache _cache;

        private IMusicService? _api;
        private MusicServiceKind _service;
        private YaAccount? _account;
        private int _connectGen;
        private bool _ready;

        private List<YaTrack> _likedTracks = new();
        private readonly HashSet<string> _likedIds = new();
        /// <summary>Все любимые треки; фильтр «Найти в любимых» — через представление, без пересборки списка.</summary>
        private ObservableCollection<TrackItem> _likedItems = new();
        private ICollectionView? _likedView;
        private string _likedFilter = "";
        private int _likedRevision;
        private readonly DispatcherTimer _likedFilterDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly DispatcherTimer _librarySaveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private ObservableCollection<TrackItem> _searchItems = new();
        private ObservableCollection<TrackItem> _upNextItems = new();

        private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
        private int _searchGen;
        private string _lastQuery = "";
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };

        private MediaKeyHook? _mediaKeys;

        private static readonly Brush DimBrush = (Brush)Application.Current.Resources["TextDimBrush"];
        private static readonly Brush TextBrushRes = (Brush)Application.Current.Resources["TextBrush"];
        private static readonly Brush AccentBrushRes = (Brush)Application.Current.Resources["AccentBrush"];
        private static readonly Brush RedBrushRes = (Brush)Application.Current.Resources["RedBrush"];

        public MainWindow()
        {
            InitializeComponent();
            _yaTokens = new TokenStore(_env);
            _scConfig = SoundCloudConfig.Load(_env);
            _scTokens = TokenStore.ForSoundCloud(_env, (refresh, ct) =>
                SoundCloudAuth.RefreshAsync(_scConfig ?? throw new AuthException("Не задан ключ приложения SoundCloud"), refresh, ct));
            _cache = new AudioCache(_settings.CacheLimitBytes);

            BindLiked(_likedItems);
            SearchList.ItemsSource = _searchItems;
            UpNextList.ItemsSource = _upNextItems;

            _searchDebounce.Tick += async (_, _) =>
            {
                _searchDebounce.Stop();
                await SearchAsync();
            };
            _toastTimer.Tick += (_, _) => HideToast();
            _likedFilterDebounce.Tick += (_, _) =>
            {
                _likedFilterDebounce.Stop();
                ApplyLikedFilter();
            };
            _librarySaveTimer.Tick += (_, _) =>
            {
                _librarySaveTimer.Stop();
                if (_account != null) _ = LibraryCache.SaveAsync(_account.Uid, _likedRevision, _likedTracks);
            };

            InitPlayer();
            InitTray();
            RestoreSettings();
            _service = MusicServices.Parse(_settings.ActiveService);
            ApplyServiceUi();
            _ready = true;

            Loaded += async (_, _) => await StartOnceAsync();
        }

        // ────────── старт ──────────

        private bool _started;

        /// <summary>Автозапуск с --tray: окно не показываем, но подключаемся и ждём в трее.</summary>
        public void StartHidden()
        {
            // окно ни разу не показано — создаём hwnd вручную (нужен для медиаклавиш и SMTC)
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            _ = StartOnceAsync();
        }

        private Task StartOnceAsync()
        {
            if (_started) return Task.CompletedTask;
            _started = true;
            return StartAsync();
        }

        private async Task StartAsync()
        {
            Log.Info($"Запуск Undertone {Updater.AppVersion.ToString(3)}");
            _ = Task.Run(() =>
            {
                _cache.MigrateLegacy();
                _cache.Trim();
            });
            Autostart.RefreshPath();
            ApplyDiscord();
            InitUpdater();

            // прошлый сервис, если в нём есть вход, иначе другой; ни одного — экран выбора
            var preferred = MusicServices.Parse(_settings.ActiveService);
            foreach (var service in new[] { preferred, preferred.Other() })
            {
                if (TokensFor(service).AccessToken is { Length: > 0 } token)
                {
                    await ConnectAsync(service, token);
                    return;
                }
            }
            if (!IsVisible) ShowFromTray(); // из автозапуска, но входа нет — без окна не войти
            ShowAuth();
        }

        // ────────── аккаунты: Яндекс Музыка и SoundCloud ──────────

        private TokenStore TokensFor(MusicServiceKind service) =>
            service == MusicServiceKind.SoundCloud ? _scTokens : _yaTokens;

        private async Task ConnectAsync(MusicServiceKind service, string token)
        {
            var gen = ++_connectGen;
            _api?.Dispose();
            var store = TokensFor(service);
            _service = service;
            IMusicService api = service == MusicServiceKind.SoundCloud
                ? new ScApi(token, (old, ct) => store.TryRefreshAsync(old, ct))
                : new YaApi(token, (old, ct) => store.TryRefreshAsync(old, ct));
            _api = api;
            if (_settings.ActiveService != service.Id())
            {
                _settings.ActiveService = service.Id();
                PersistSettings();
            }
            ApplyServiceUi();
            RetryBtn.Visibility = Visibility.Collapsed;
            AccountName.Text = "Подключение…";
            AccountSub.Text = service.Title();

            YaAccount account;
            try
            {
                account = await api.GetAccountAsync();
            }
            catch (YaApiException ex) when (ex.IsUnauthorized)
            {
                if (gen != _connectGen) return;
                Log.Warn($"Токен {service.Title()} отклонён — нужен повторный вход");
                if (!IsVisible) ShowFromTray();
                ShowAuth(service, $"Сохранённый вход в {service.Title()} больше не работает — войдите заново.");
                return;
            }
            catch (Exception ex)
            {
                if (gen != _connectGen) return;
                Log.Error($"Не удалось подключиться к {service.TitleGenitive()}", ex);
                AccountName.Text = "Нет подключения";
                AccountSub.Text = $"{service.Title()} · {Short(ex.Message)}";
                RetryBtn.Visibility = Visibility.Visible;
                ShowToast($"Не удалось подключиться к {service.TitleGenitive()}: " + Short(ex.Message));
                return;
            }
            if (gen != _connectGen) return;

            _account = account;
            HideAuth();
            var name = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Login : account.DisplayName;
            AccountName.Text = name;
            AvatarLetter.Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?";
            if (service == MusicServiceKind.Yandex)
            {
                AccountSub.Text = account.HasPlus ? "Яндекс Плюс" : "Без Плюса · только превью";
                if (!account.HasPlus)
                    ShowToast("Без подписки Плюс Яндекс отдаёт только 30-секундные фрагменты треков", 5);
            }
            else
            {
                AccountSub.Text = "SoundCloud";
            }
            if (!_settings.AccountNames.TryGetValue(service.Id(), out var saved) || saved != name)
            {
                _settings.AccountNames[service.Id()] = name;
                PersistSettings();
            }

            LoadHistory();
            LoadStats();
            ApplyRemote();
            LoadOffline();
            InitLocal();
            ApplyFriends();
            RestoreSession();
            _ = LoadAvatarAsync();
            _ = LoadPlaylistsAsync();
            await LoadLikedAsync();
        }

        private async Task LoadAvatarAsync()
        {
            var api = _api;
            if (api == null) return;
            var url = await api.GetAvatarUrlAsync();
            if (url != null && api == _api) AvatarBrush.ImageSource = Covers.Load(url, 76);
        }

        private async void Retry_Click(object sender, RoutedEventArgs e)
        {
            var token = TokensFor(_service).AccessToken;
            if (token == null) ShowAuth(_service);
            else await ConnectAsync(_service, token);
        }

        /// <summary>Переключение на другой сервис: если вход в него уже есть — сразу, иначе экран входа.</summary>
        private async Task SwitchToAsync(MusicServiceKind service)
        {
            if (service == _service && _api != null && _account != null) return;
            if (TokensFor(service).AccessToken is not { Length: > 0 } token)
            {
                ShowAuth(service, cancelable: true);
                return;
            }
            ResetSession(keepData: true);
            await ConnectAsync(service, token);
            if (_account != null && _service == service) ShowToast($"{service.Title()} · {AccountName.Text}");
        }

        /// <summary>
        /// Отключает текущий аккаунт: очередь, списки, плейлисты, комната. keepData — переключение, а не выход:
        /// история, статистика и «продолжить с места» сохраняются (они лежат по Uid аккаунта).
        /// </summary>
        private void ResetSession(bool keepData)
        {
            _connectGen++;
            _collGen++;
            _searchGen++;
            if (keepData && _account != null)
            {
                SaveSession();
                _ = HistoryStore.SaveAsync(_account.Uid, _history);
                _ = _stats.SaveAsync(_account.Uid);
            }
            FlushPlayReport();
            LeaveTogether(announce: false);
            StopPlayback();
            _queue.Clear();
            _waveSession = null;
            _waveKey = "";
            _api?.Dispose();
            _api = null;
            _account = null;
            ApplyFriends(); // без аккаунта лента не работает — отключаемся
            _history.Clear();
            _historyDirty = true;
            _likedTracks.Clear();
            _likedIds.Clear();
            _likedItems.Clear();
            _likedRevision = 0;
            UpdateLikedHeader();
            LikedEmpty.Visibility = Visibility.Collapsed;
            LikedLoading.Visibility = Visibility.Collapsed;
            _searchItems.Clear();
            _lastQuery = "";
            _playlists.Clear();
            UpdateUpNext();
            AvatarBrush.ImageSource = null;
            ShowEmptyNowPlaying();
            AccountName.Text = "Не выполнен вход";
            AccountSub.Text = "";
            AvatarLetter.Text = "?";
            RetryBtn.Visibility = Visibility.Collapsed;
            if (PageCollection.Visibility == Visibility.Visible) GoTo(NavNow);
        }

        /// <summary>Кнопки и подписи под возможности сервиса: у SoundCloud нет Моей волны, дизлайков и lossless.</summary>
        private void ApplyServiceUi()
        {
            var wave = _api?.SupportsWave ?? _service == MusicServiceKind.Yandex;
            var dislike = _api?.SupportsDislike ?? _service == MusicServiceKind.Yandex;
            WaveCard.Visibility = wave ? Visibility.Visible : Visibility.Collapsed;
            NowDislike.Visibility = dislike ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = wave ? "Моя волна" : "SoundCloud";
            EmptySub.Text = wave
                ? "Бесконечный поток музыки под ваш вкус"
                : "Включите любимые треки или найдите что-нибудь в поиске";
            EmptyPlay.ToolTip = wave ? "Запустить Мою волну" : "Слушать «Мне нравится»";
            if (_queue.Current == null) BarArtist.Text = EmptyBarHint;
            if (_searchItems.Count == 0) SearchEmptyText.Text = $"Найдите что угодно в каталоге {_service.TitleGenitive()}";
            UpdateVersionText();
        }

        private string EmptyBarHint =>
            _api?.SupportsWave ?? _service == MusicServiceKind.Yandex ? "Включите Мою волну" : "Выберите трек";

        private void Logout_Click(object sender, RoutedEventArgs e) => Logout();

        /// <summary>Выход из текущего сервиса: его токены и локальные данные удаляются. Есть вход во второй — переключаемся.</summary>
        private void Logout()
        {
            var service = _service;
            var answer = MessageBox.Show(this,
                $"Выйти из {service.TitleGenitive()}? Токены этого аккаунта будут удалены из .env.", "Выход",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            var uid = _account?.Uid;
            ResetSession(keepData: false);
            TokensFor(service).Clear();
            LibraryCache.Delete(uid);
            HistoryStore.Delete(uid);
            if (uid != null && SessionStore.Load(uid.Value) != null) SessionStore.Clear();
            if (_settings.AccountNames.Remove(service.Id())) PersistSettings();

            var other = service.Other();
            if (TokensFor(other).AccessToken is { Length: > 0 } token)
            {
                ShowToast($"Вы вышли из {service.TitleGenitive()} · переключаюсь на {other.Title()}");
                _ = ConnectAsync(other, token);
            }
            else
            {
                ShowAuth();
            }
        }

        // ────────── навигация ──────────

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            PageNow.Visibility = NavNow.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageLiked.Visibility = NavLiked.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSearch.Visibility = NavSearch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageHistory.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageFriends.Visibility = NavFriends.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageLocal.Visibility = NavLocal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSmart.Visibility = NavSmart.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageCollection.Visibility = Visibility.Collapsed;
            ClearPlaylistSelection();
            if (NavSettings.IsChecked == true) ShowSettings();
            if (NavHistory.IsChecked == true) ShowHistory();
            if (NavFriends.IsChecked == true) RebuildFriends();
            if (NavLocal.IsChecked == true) ShowLocal();
            if (NavSmart.IsChecked == true) RebuildSmart();

            if (NavSearch.IsChecked == true)
                Dispatcher.InvokeAsync(() =>
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                }, DispatcherPriority.Input);
            else
                Keyboard.ClearFocus();
        }

        private void GoTo(RadioButton nav) => nav.IsChecked = true;

        // ────────── Мне нравится ──────────

        /// <summary>
        /// Сначала — мгновенно из локальной копии, потом с сервера: лёгкий запрос списка id,
        /// полные данные догружаются только для новых треков.
        /// </summary>
        private async Task LoadLikedAsync()
        {
            if (_api == null || _account == null) return;
            var api = _api;
            var uid = _account.Uid;

            var cached = LibraryCache.Load(uid);
            if (cached is { Liked.Count: > 0 } snap)
            {
                _likedRevision = snap.Revision;
                SetLiked(snap.Liked);
            }
            else
            {
                LikedLoading.Visibility = Visibility.Visible;
                LikedEmpty.Visibility = Visibility.Collapsed;
            }

            try
            {
                var (revision, ids) = await api.GetLikedIdsAsync();
                var fresh = await LibraryCache.MergeAsync(api, ids, cached?.Liked ?? new List<YaTrack>());
                if (_api != api || _account?.Uid != uid) return; // за это время вышли из аккаунта

                if (!fresh.Select(t => t.Id).SequenceEqual(_likedTracks.Select(t => t.Id)))
                    SetLiked(fresh);
                _likedRevision = revision;
                _ = LibraryCache.SaveAsync(uid, revision, _likedTracks);
            }
            catch (Exception ex)
            {
                Log.Warn("«Мне нравится» не обновился с сервера", ex);
                if (_likedTracks.Count == 0)
                {
                    LikedEmpty.Text = "Не удалось загрузить любимые треки\n" + Short(ex.Message);
                    LikedEmpty.Visibility = Visibility.Visible;
                }
                else
                {
                    ShowToast("Показан сохранённый список «Мне нравится» — сервер недоступен");
                }
            }
            finally
            {
                LikedLoading.Visibility = Visibility.Collapsed;
                UpdateLikedEmpty();
            }
        }

        private void SetLiked(List<YaTrack> tracks)
        {
            _likedTracks = tracks;
            _likedIds.Clear();
            foreach (var t in _likedTracks) _likedIds.Add(t.Id);
            RebuildLiked();
            RefreshLikeMarks();
            SyncOfflineLiked();
            UpdateOfflineButtons();
            PublishMyLikes();
        }

        private void BindLiked(ObservableCollection<TrackItem> items)
        {
            _likedItems = items;
            _likedView = CollectionViewSource.GetDefaultView(_likedItems);
            _likedView.Filter = o => o is TrackItem it && Matches(it.Track, _likedFilter);
            LikedList.ItemsSource = _likedItems;
        }

        /// <summary>Полная пересборка — только когда сменился сам список (загрузка, обновление с сервера).</summary>
        private void RebuildLiked()
        {
            var current = _queue.Current?.Id;
            BindLiked(new ObservableCollection<TrackItem>(_likedTracks.Select((t, i) =>
                new TrackItem(t, i + 1) { IsLiked = true, IsPlaying = t.Id == current })));
            UpdateLikedHeader();
            UpdateLikedEmpty();
        }

        /// <summary>Треки, которые сейчас видны в списке (с учётом фильтра) — их и играем.</summary>
        private List<TrackItem> VisibleLiked() =>
            _likedView?.Cast<TrackItem>().ToList() ?? _likedItems.ToList();

        private void ApplyLikedFilter()
        {
            _likedFilter = LikedFilter.Text.Trim();
            _likedView?.Refresh();
            UpdateLikedEmpty();
        }

        private void UpdateLikedEmpty()
        {
            if (LikedLoading.Visibility == Visibility.Visible)
            {
                LikedEmpty.Visibility = Visibility.Collapsed;
                return;
            }
            if (_likedTracks.Count == 0)
            {
                if (LikedEmpty.Text.StartsWith("Не удалось", StringComparison.Ordinal) && LikedEmpty.Visibility == Visibility.Visible)
                    return;
                LikedEmpty.Text = "Здесь будут треки, которые вам понравились.\nНажмите ♥ у любого трека.";
                LikedEmpty.Visibility = Visibility.Visible;
            }
            else if (_likedFilter.Length > 0 && (_likedView?.IsEmpty ?? true))
            {
                LikedEmpty.Text = $"В любимых нет ничего по запросу «{_likedFilter}»";
                LikedEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                LikedEmpty.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Локальную копию пишем не на каждый клик ♥, а через 2 секунды тишины.</summary>
        private void ScheduleLibrarySave()
        {
            _librarySaveTimer.Stop();
            _librarySaveTimer.Start();
        }

        private void UpdateLikedHeader()
        {
            var n = _likedTracks.Count;
            var total = TimeSpan.FromMilliseconds(_likedTracks.Sum(t => t.DurationMs));
            LikedCount.Text = n == 0 ? "Пока пусто" : $"{n} {Format.Plural(n, "трек", "трека", "треков")} · {Format.Total(total)}";
        }

        private static bool Matches(YaTrack t, string filter) =>
            filter.Length == 0 ||
            t.FullTitle.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            t.ArtistsText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            t.Album.Contains(filter, StringComparison.OrdinalIgnoreCase);

        private void LikedFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            _likedFilterDebounce.Stop();
            _likedFilterDebounce.Start();
        }

        private async void LikedPlay_Click(object sender, RoutedEventArgs e) => await PlayLikedAsync(shuffle: false);
        private async void LikedShuffle_Click(object sender, RoutedEventArgs e) => await PlayLikedAsync(shuffle: true);

        private async Task PlayLikedAsync(bool shuffle)
        {
            var visible = VisibleLiked();
            if (visible.Count == 0) return;
            if (shuffle != _settings.Shuffle)
            {
                _settings.Shuffle = shuffle;
                PersistSettings();
                UpdateModeButtons();
            }
            var start = shuffle ? Random.Shared.Next(visible.Count) : 0;
            await PlayFromListAsync(visible, start, QueueSource.Liked);
        }

        // ────────── поиск ──────────

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _searchDebounce.Stop();
            _lastQuery = "";
            await SearchAsync();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            SearchClear.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchDebounce.Stop();
            if (SearchBox.Text.Trim().Length >= 2) _searchDebounce.Start();
        }

        private void SearchClear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            _lastQuery = "";
            SearchBox.Focus();
        }

        private YaSearchResult? _searchResult;
        private int _searchPage;
        private bool _searchLoadingMore;

        private async Task SearchAsync()
        {
            var query = SearchBox.Text.Trim();
            if (_api == null || query.Length == 0 || query == _lastQuery) return;
            _lastQuery = query;
            var gen = ++_searchGen;

            SearchLoading.Visibility = Visibility.Visible;
            SearchEmpty.Visibility = Visibility.Collapsed;
            try
            {
                var result = await _api.SearchAsync(query);
                if (gen != _searchGen) return;

                _searchResult = result;
                _searchPage = 0;
                var current = _queue.Current?.Id;
                _searchItems = new ObservableCollection<TrackItem>(result.Tracks.Select((t, i) =>
                    new TrackItem(t, i + 1) { IsLiked = _likedIds.Contains(t.Id), IsPlaying = t.Id == current }));
                SearchList.ItemsSource = _searchItems;
                if (_searchItems.Count > 0) SearchList.ScrollIntoView(_searchItems[0]);

                ShowSearchBest(result.Best);
                SearchTabTracks.Content = TabTitle("Треки", result.Tracks.Count, result.TracksTotal);
                SearchTabArtists.Content = TabTitle("Артисты", result.Artists.Count, 0);
                SearchTabAlbums.Content = TabTitle("Альбомы", result.Albums.Count, 0);
                SearchTabPlaylists.Content = TabTitle("Плейлисты", result.Playlists.Count, 0);
                SearchTabs.Visibility = Visibility.Visible;

                // нет треков, но есть артисты/альбомы — сразу показываем то, что нашлось
                if (result.Tracks.Count == 0)
                {
                    var tab = result.Artists.Count > 0 ? SearchTabArtists
                        : result.Albums.Count > 0 ? SearchTabAlbums
                        : result.Playlists.Count > 0 ? SearchTabPlaylists
                        : SearchTabTracks;
                    tab.IsChecked = true;
                }
                ShowSearchTab();

                if (result.Tracks.Count == 0 && result.Artists.Count == 0 && result.Albums.Count == 0 && result.Playlists.Count == 0)
                {
                    SearchTabs.Visibility = Visibility.Collapsed;
                    SearchEmptyText.Text = $"Ничего не нашлось по запросу «{query}»";
                    SearchEmpty.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                if (gen != _searchGen) return;
                _lastQuery = "";
                SearchEmptyText.Text = "Ошибка поиска: " + Short(ex.Message);
                SearchEmpty.Visibility = Visibility.Visible;
            }
            finally
            {
                if (gen == _searchGen) SearchLoading.Visibility = Visibility.Collapsed;
            }
        }

        private static string TabTitle(string name, int count, int total) =>
            count == 0 ? name : $"{name} · {(total > count ? total : count)}";

        private void SearchTab_Checked(object sender, RoutedEventArgs e)
        {
            if (_ready) ShowSearchTab();
        }

        private void ShowSearchTab()
        {
            var result = _searchResult;
            var tracks = SearchTabTracks.IsChecked == true;
            SearchList.Visibility = tracks ? Visibility.Visible : Visibility.Collapsed;
            SearchCardsScroll.Visibility = tracks ? Visibility.Collapsed : Visibility.Visible;
            if (tracks || result == null) return;

            IEnumerable<SearchCard> cards = SearchTabArtists.IsChecked == true ? result.Artists.Select(SearchCard.From)
                : SearchTabAlbums.IsChecked == true ? result.Albums.Select(SearchCard.From)
                : result.Playlists.Select(SearchCard.From);
            SearchCards.ItemsSource = cards.ToList();
            SearchCardsScroll.ScrollToTop();
        }

        private void ShowSearchBest(YaSearchBest? best)
        {
            // лучший-трек и так первый в списке — карточка нужна для артиста, альбома, плейлиста
            if (best == null || best.Type == "track")
            {
                SearchBest.Visibility = Visibility.Collapsed;
                return;
            }
            var round = best.Type == "artist";
            SearchBestCover.CornerRadius = SearchBestCoverBack.CornerRadius = new CornerRadius(round ? 36 : 10);
            (string kind, string title, string sub, string? cover) = best.Type switch
            {
                "artist" => ("АРТИСТ", best.Artist!.Name, string.Join(", ", best.Artist.Genres.Take(3)), best.Artist.CoverUrl(200)),
                "album" => (best.Album!.Type == "single" ? "СИНГЛ" : "АЛЬБОМ", best.Album.Title,
                    string.Join(" · ", new[] { best.Album.ArtistsText, best.Album.Year > 0 ? best.Album.Year.ToString() : "" }.Where(x => x.Length > 0)),
                    best.Album.CoverUrl(200)),
                _ => ("ПЛЕЙЛИСТ", best.Playlist!.Title, best.Playlist.OwnerName, best.Playlist.CoverUrl(200)),
            };
            SearchBestKind.Text = "ЛУЧШИЙ РЕЗУЛЬТАТ · " + kind;
            SearchBestTitle.Text = title;
            SearchBestSub.Text = sub;
            SearchBestCoverBrush.ImageSource = Covers.Load(cover, 144);
            SearchBest.Visibility = Visibility.Visible;
        }

        private async void SearchBest_Click(object sender, MouseButtonEventArgs e)
        {
            var best = _searchResult?.Best;
            if (best == null) return;
            if (best.Artist != null) await OpenArtistAsync(best.Artist.Id);
            else if (best.Album != null) await OpenAlbumAsync(best.Album.Id);
            else if (best.Playlist != null) await OpenPublicPlaylistAsync(best.Playlist);
        }

        private async void SearchCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: SearchCard card }) return;
            switch (card.Kind)
            {
                case SearchCardKind.Artist: await OpenArtistAsync(card.Id); break;
                case SearchCardKind.Album: await OpenAlbumAsync(card.Id); break;
                case SearchCardKind.Playlist:
                    var pl = _searchResult?.Playlists.FirstOrDefault(p => p.Kind == card.Id && p.OwnerUid == card.OwnerUid);
                    if (pl != null) await OpenPublicPlaylistAsync(pl);
                    break;
            }
        }

        /// <summary>Докручивают список треков до конца — подгружаем следующую страницу.</summary>
        private async void SearchList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_searchLoadingMore || _api == null || _searchResult == null || e.ExtentHeight <= 0) return;
            if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 4) return;
            if (_searchItems.Count >= _searchResult.TracksTotal || _searchItems.Count == 0) return;

            _searchLoadingMore = true;
            var gen = _searchGen;
            var query = _lastQuery;
            try
            {
                var more = await _api.SearchTracksAsync(query, _searchPage + 1);
                if (gen != _searchGen || more.Count == 0) return;
                _searchPage++;
                var known = _searchItems.Select(i => i.Track.Id).ToHashSet();
                var current = _queue.Current?.Id;
                foreach (var t in more.Where(t => known.Add(t.Id)))
                    _searchItems.Add(new TrackItem(t, _searchItems.Count + 1) { IsLiked = _likedIds.Contains(t.Id), IsPlaying = t.Id == current });
            }
            catch (Exception ex)
            {
                Log.Warn("Следующая страница поиска не загрузилась", ex);
            }
            finally
            {
                _searchLoadingMore = false;
            }
        }

        // ────────── клики по спискам ──────────

        private async void Row_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem { DataContext: TrackItem item } container) return;
            var list = ItemsControl.ItemsControlFromItemContainer(container);
            await ActivateRowAsync(list, item);
        }

        private async void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not ListBox { SelectedItem: TrackItem item } list) return;
            e.Handled = true;
            await ActivateRowAsync(list, item);
        }

        private async Task ActivateRowAsync(ItemsControl? list, TrackItem item)
        {
            if (RowClickSuppressed) return; // только что перетаскивали строку
            // клик по играющему треку — пауза/продолжить
            if (_queue.Current?.Id == item.Track.Id && _hasMedia && list != UpNextList)
            {
                TogglePlay();
                return;
            }

            if (list == LikedList)
            {
                var visible = VisibleLiked();
                await PlayFromListAsync(visible, visible.IndexOf(item), QueueSource.Liked);
            }
            else if (list == SearchList)
                await PlayFromListAsync(_searchItems, _searchItems.IndexOf(item), QueueSource.Search);
            else if (list == UpNextList)
                await JumpAheadAsync(_upNextItems.IndexOf(item) + 1);
            else if (list == CollList)
                await PlayFromListAsync(_collItems, _collItems.IndexOf(item), QueueSource.Collection, _collLabel);
            else if (list == HistoryList)
                await PlayFromListAsync(_historyItems, _historyItems.IndexOf(item), QueueSource.Collection, "ИЗ ИСТОРИИ");
            else if (list == LocalList)
                await PlayFromListAsync(_localItems, _localItems.IndexOf(item), QueueSource.Collection, "МОИ ФАЙЛЫ");
            else if (list == SimilarList || list == RecentList)
                await ActivateShelfAsync(list, item);
        }

        private async void RowLike_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: TrackItem item })
                await ToggleLikeAsync(item.Track);
        }

        /// <summary>Обновляет отметки ♥ и «играет» во всех списках.</summary>
        private void RefreshLikeMarks()
        {
            var current = _queue.Current?.Id;
            foreach (var list in new[] { _likedItems, _searchItems, _upNextItems, _collItems, _historyItems, _similarItems, _recentItems, _localItems })
                foreach (var it in list)
                {
                    it.IsLiked = _likedIds.Contains(it.Track.Id);
                    it.IsPlaying = it.Track.Id == current;
                }

            var liked = current != null && _likedIds.Contains(current);
            BarLike.Content = liked ? "" : "";
            BarLike.Foreground = liked ? RedBrushRes : DimBrush;
            NowLikeGlyph.Text = liked ? "" : "";
            NowLikeGlyph.Foreground = liked ? RedBrushRes : TextBrushRes;
            NowLike.ToolTip = liked ? "В любимых — убрать (L)" : "Мне нравится (L)";
        }

        // ────────── хоткеи ──────────

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (CaptureHotkey(e)) return; // записываем новое сочетание на странице настроек
            var mods = Keyboard.Modifiers;
            switch (e.Key)
            {
                case Key.MediaPlayPause: e.Handled = true; TogglePlay(); return;
                case Key.MediaNextTrack: e.Handled = true; await NextAsync(auto: false); return;
                case Key.MediaPreviousTrack: e.Handled = true; await PrevAsync(); return;
            }

            if (AuthOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape && _authCancelable) { e.Handled = true; HideAuth(); }
                return;
            }

            if (DialogOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape) { e.Handled = true; CloseDialog(null); }
                return;
            }

            // окно обновления: Esc скрывает, остальные клавиши (пробел, медиаклавиши) работают как обычно
            if (UpdateOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
            {
                e.Handled = true;
                HideUpdateOverlay();
                return;
            }

            if (e.Key == Key.F11 && mods == ModifierKeys.None)
            {
                e.Handled = true;
                ToggleParty();
                return;
            }
            if (mods == ModifierKeys.Control && e.Key == Key.K)
            {
                e.Handled = true;
                OpenQuickSearch();
                return;
            }
            if (SmartEditor.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape) { e.Handled = true; CloseSmartEditor(); }
                return;
            }
            // быстрый поиск открыт — клавиши его (стрелки, Enter, Esc обрабатывает само окно поиска)
            if (QuickOverlay.Visibility == Visibility.Visible)
            {
                if (!QuickBox.IsKeyboardFocusWithin) QuickBox.Focus();
                return;
            }

            // Backspace / Alt+← — назад со страницы артиста/альбома
            if (PageCollection.Visibility == Visibility.Visible && Keyboard.FocusedElement is not TextBox &&
                (e.Key == Key.Back || (mods == ModifierKeys.Alt && e.SystemKey == Key.Left)))
            {
                e.Handled = true;
                await GoBackAsync();
                return;
            }

            if (mods == ModifierKeys.Control && e.Key == Key.F)
            {
                e.Handled = true;
                if (NavSearch.IsChecked == true) { SearchBox.Focus(); SearchBox.SelectAll(); }
                else GoTo(NavSearch);
                return;
            }

            if (Keyboard.FocusedElement is TextBox)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    Keyboard.ClearFocus();
                    FocusManager.SetFocusedElement(this, this);
                }
                return;
            }

            if (mods == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.Right: e.Handled = true; await NextAsync(auto: false); break;
                    case Key.Left: e.Handled = true; await PrevAsync(); break;
                    case Key.Up: e.Handled = true; VolumeSlider.Value = Math.Min(1, VolumeSlider.Value + 0.05); break;
                    case Key.Down: e.Handled = true; VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 0.05); break;
                }
                return;
            }
            if (mods != ModifierKeys.None && mods != ModifierKeys.Shift) return;

            var big = mods == ModifierKeys.Shift;
            // Handled ставим до любого await — иначе событие уйдёт дальше (пробел нажмёт кнопку в фокусе)
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Right: SeekBy(big ? 15 : 5); break;
                case Key.Left: SeekBy(big ? -15 : -5); break;
                case Key.N: await NextAsync(auto: false); break;
                case Key.P: await PrevAsync(); break;
                case Key.S: ToggleShuffle(); break;
                case Key.R: CycleRepeat(); break;
                case Key.M: ToggleMute(); break;
                case Key.L: if (_queue.Current is { } c) await ToggleLikeAsync(c); break;
                case Key.D: await DislikeCurrentAsync(); break;
                case Key.W: await StartWaveAsync(); break;
                case Key.D1: case Key.NumPad1: GoTo(NavNow); break;
                case Key.D2: case Key.NumPad2: GoTo(NavLiked); break;
                case Key.D3: case Key.NumPad3: GoTo(NavSearch); break;
                case Key.D4: case Key.NumPad4: GoTo(NavHistory); break;
                case Key.D5: case Key.NumPad5: GoTo(NavFriends); break;
                case Key.D6: case Key.NumPad6: GoTo(NavSettings); break;
                case Key.T: ToggleLyrics(); if (NavNow.IsChecked != true) GoTo(NavNow); break;
                default: e.Handled = false; break;
            }
        }

        // ────────── окно ──────────

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            Native.ApplyDarkTitleBar(this, (Color)Application.Current.Resources["BgColor"]);
            InitMediaControls();
            _hotkeys = new GlobalHotkeys(this, OnHotkey);
            ApplyHotkeys();
        }

        private async void OnGlobalMediaKey(Key key)
        {
            switch (key)
            {
                case Key.MediaPlayPause: TogglePlay(); break;
                case Key.MediaNextTrack: await NextAsync(auto: false); break;
                case Key.MediaPreviousTrack: await PrevAsync(); break;
            }
        }

        private void RestoreSettings()
        {
            if (_settings.WindowWidth >= MinWidth && _settings.WindowHeight >= MinHeight)
            {
                Width = _settings.WindowWidth;
                Height = _settings.WindowHeight;
            }
            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

            VolumeSlider.Value = Math.Clamp(_settings.Volume, 0, 1);
            _player.Volume = VolumeSlider.Value;
            _player.IsMuted = _settings.Muted;
            _queue.Repeat = _settings.Repeat;
            UpdateVolumeGlyph();
            UpdateModeButtons();
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                // крестик — в трей (по умолчанию) или полный выход, если так выбрано в настройках
                if (_settings.CloseToTray) HideToTray();
                else ExitApp();
                return;
            }

            ReportWaveStop();
            SaveSession();
            _settings.Volume = VolumeSlider.Value;
            _settings.Muted = _player.IsMuted;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.Save();

            _mediaKeys?.Dispose();
            _hotkeys?.Dispose();
            _media?.Dispose();
            foreach (var sink in _sinks) (sink as IDisposable)?.Dispose();
            _authCts?.Cancel();
            _updateTimer.Stop();
            _party?.Close();
            _remote?.Dispose();
            LeaveTogether(announce: false);
            _friendsFeed?.Dispose();
            _player.Dispose();
            _api?.Dispose();
            _tray?.Dispose();
            _tray = null;
        }

        // ────────── тост ──────────

        private void ShowToast(string text, double seconds = 2.6)
        {
            ToastText.Text = text;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
            _toastTimer.Start();
        }

        private void HideToast()
        {
            _toastTimer.Stop();
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        }

        private static string Short(string message) =>
            message.Length > 140 ? message.Substring(0, 137) + "…" : message;
    }
}
