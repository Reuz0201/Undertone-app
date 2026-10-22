using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        private readonly TokenStore _tokens;
        private readonly AudioCache _cache = new();

        private YaApi? _api;
        private YaAccount? _account;
        private bool _ready;

        private List<YaTrack> _likedTracks = new();
        private readonly HashSet<string> _likedIds = new();
        private ObservableCollection<TrackItem> _likedItems = new();
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
            _tokens = new TokenStore(_env);

            LikedList.ItemsSource = _likedItems;
            SearchList.ItemsSource = _searchItems;
            UpNextList.ItemsSource = _upNextItems;

            _searchDebounce.Tick += async (_, _) =>
            {
                _searchDebounce.Stop();
                await SearchAsync();
            };
            _toastTimer.Tick += (_, _) => HideToast();

            InitPlayer();
            RestoreSettings();
            _ready = true;

            Loaded += async (_, _) => await StartAsync();
        }

        // ────────── старт ──────────

        private async Task StartAsync()
        {
            _ = Task.Run(_cache.Trim);
            InitUpdater();

            var token = _tokens.AccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                ShowAuth();
                return;
            }
            await ConnectAsync(token);
        }

        private async Task ConnectAsync(string token)
        {
            _api?.Dispose();
            _api = new YaApi(token, (old, ct) => _tokens.TryRefreshAsync(old, ct));
            RetryBtn.Visibility = Visibility.Collapsed;
            AccountName.Text = "Подключение…";
            AccountSub.Text = "";

            try
            {
                _account = await _api.GetAccountAsync();
            }
            catch (YaApiException ex) when (ex.IsUnauthorized)
            {
                ShowAuth("Сохранённый токен больше не работает — войдите заново.");
                return;
            }
            catch (Exception ex)
            {
                AccountName.Text = "Нет подключения";
                AccountSub.Text = Short(ex.Message);
                RetryBtn.Visibility = Visibility.Visible;
                ShowToast("Не удалось подключиться к Яндекс Музыке: " + Short(ex.Message));
                return;
            }

            HideAuth();
            var name = string.IsNullOrWhiteSpace(_account.DisplayName) ? _account.Login : _account.DisplayName;
            AccountName.Text = name;
            AvatarLetter.Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?";
            AccountSub.Text = _account.HasPlus ? "Яндекс Плюс" : "Без Плюса · только превью";
            if (!_account.HasPlus)
                ShowToast("Без подписки Плюс Яндекс отдаёт только 30-секундные фрагменты треков", 5);

            await LoadLikedAsync();
        }

        private async void Retry_Click(object sender, RoutedEventArgs e)
        {
            var token = _tokens.AccessToken;
            if (token == null) ShowAuth();
            else await ConnectAsync(token);
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(this, "Выйти из аккаунта? Токены будут удалены из .env.", "Выход",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            StopPlayback();
            _queue.Clear();
            _waveSession = null;
            _env.Set(new Dictionary<string, string?>
            {
                ["access_token"] = "",
                ["refresh_token"] = "",
                ["expires_in"] = "",
            });
            _api?.Dispose();
            _api = null;
            _account = null;
            _likedTracks.Clear();
            _likedIds.Clear();
            _likedItems.Clear();
            _searchItems.Clear();
            ShowEmptyNowPlaying();
            AccountName.Text = "Не выполнен вход";
            AccountSub.Text = "";
            AvatarLetter.Text = "?";
            ShowAuth();
        }

        // ────────── навигация ──────────

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            PageNow.Visibility = NavNow.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageLiked.Visibility = NavLiked.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSearch.Visibility = NavSearch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

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

        private async Task LoadLikedAsync()
        {
            if (_api == null) return;
            LikedLoading.Visibility = Visibility.Visible;
            LikedEmpty.Visibility = Visibility.Collapsed;
            try
            {
                _likedTracks = await _api.GetLikedTracksAsync();
                _likedIds.Clear();
                foreach (var t in _likedTracks) _likedIds.Add(t.Id);
                RebuildLiked();
                RefreshLikeMarks();
            }
            catch (Exception ex)
            {
                LikedEmpty.Text = "Не удалось загрузить любимые треки\n" + Short(ex.Message);
                LikedEmpty.Visibility = Visibility.Visible;
            }
            finally
            {
                LikedLoading.Visibility = Visibility.Collapsed;
            }
        }

        private void RebuildLiked()
        {
            var filter = LikedFilter.Text.Trim();
            var current = _queue.Current?.Id;
            var items = _likedTracks
                .Where(t => Matches(t, filter))
                .Select((t, i) => new TrackItem(t, i + 1) { IsLiked = true, IsPlaying = t.Id == current });
            _likedItems = new ObservableCollection<TrackItem>(items);
            LikedList.ItemsSource = _likedItems;
            UpdateLikedHeader();

            if (_likedTracks.Count == 0)
            {
                LikedEmpty.Text = "Здесь будут треки, которые вам понравились.\nНажмите ♥ у любого трека.";
                LikedEmpty.Visibility = Visibility.Visible;
            }
            else if (_likedItems.Count == 0)
            {
                LikedEmpty.Text = $"В любимых нет ничего по запросу «{filter}»";
                LikedEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                LikedEmpty.Visibility = Visibility.Collapsed;
            }
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
            if (_ready) RebuildLiked();
        }

        private async void LikedPlay_Click(object sender, RoutedEventArgs e) => await PlayLikedAsync(shuffle: false);
        private async void LikedShuffle_Click(object sender, RoutedEventArgs e) => await PlayLikedAsync(shuffle: true);

        private async Task PlayLikedAsync(bool shuffle)
        {
            if (_likedItems.Count == 0) return;
            if (shuffle != _settings.Shuffle)
            {
                _settings.Shuffle = shuffle;
                UpdateModeButtons();
            }
            var start = shuffle ? new Random().Next(_likedItems.Count) : 0;
            await PlayFromListAsync(_likedItems, start, QueueSource.Liked);
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
            _searchDebounce.Stop();
            if (SearchBox.Text.Trim().Length >= 2) _searchDebounce.Start();
        }

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
                var tracks = await _api.SearchTracksAsync(query);
                if (gen != _searchGen) return;

                var current = _queue.Current?.Id;
                _searchItems = new ObservableCollection<TrackItem>(tracks.Select((t, i) =>
                    new TrackItem(t, i + 1) { IsLiked = _likedIds.Contains(t.Id), IsPlaying = t.Id == current }));
                SearchList.ItemsSource = _searchItems;
                SearchList.ScrollIntoView(_searchItems.FirstOrDefault());

                if (_searchItems.Count == 0)
                {
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
            // клик по играющему треку — пауза/продолжить
            if (_queue.Current?.Id == item.Track.Id && _hasMedia && list != UpNextList)
            {
                TogglePlay();
                return;
            }

            if (list == LikedList)
                await PlayFromListAsync(_likedItems, _likedItems.IndexOf(item), QueueSource.Liked);
            else if (list == SearchList)
                await PlayFromListAsync(_searchItems, _searchItems.IndexOf(item), QueueSource.Search);
            else if (list == UpNextList)
                await JumpAheadAsync(_upNextItems.IndexOf(item) + 1);
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
            foreach (var list in new[] { _likedItems, _searchItems, _upNextItems })
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
            NowLikeText.Text = liked ? "В любимых" : "Нравится";
        }

        // ────────── хоткеи ──────────

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            switch (e.Key)
            {
                case Key.MediaPlayPause: e.Handled = true; TogglePlay(); return;
                case Key.MediaNextTrack: e.Handled = true; await NextAsync(auto: false); return;
                case Key.MediaPreviousTrack: e.Handled = true; await PrevAsync(); return;
            }

            if (AuthOverlay.Visibility == Visibility.Visible) return;

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
                default: e.Handled = false; break;
            }
        }

        // ────────── окно ──────────

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            Native.ApplyDarkTitleBar(this, (Color)Application.Current.Resources["BgColor"]);
            _mediaKeys = new MediaKeyHook(this, OnGlobalMediaKey);
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
            ReportWaveStop();
            _settings.Volume = VolumeSlider.Value;
            _settings.Muted = _player.IsMuted;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.Save();

            _mediaKeys?.Dispose();
            _authCts?.Cancel();
            _updateTimer.Stop();
            _player.Close();
            _api?.Dispose();
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
