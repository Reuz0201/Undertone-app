using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Страницы артиста, альбома и плейлиста (одна общая разметка) + переходы «Назад».</summary>
    public partial class MainWindow
    {
        /// <summary>Playlist — свой (можно править), PublicPlaylist — чужой (из поиска).</summary>
        private enum CollectionKind { None, Artist, Album, Playlist, PublicPlaylist }

        private ObservableCollection<TrackItem> _collItems = new();
        private CollectionKind _collKind;
        private string _collLabel = "";
        private YaArtist? _collArtist;
        private PlaylistItem? _collPlaylist;
        private int _collGen;

        private Func<Task>? _collView;
        private readonly Stack<Func<Task>> _collHistory = new();
        private RadioButton? _collReturnTo;

        // ────────── навигация ──────────

        /// <summary>Открывает страницу коллекции. fromSidebar — пришли из списка плейлистов (без «Назад»).</summary>
        private async Task OpenCollectionAsync(Func<Task> view, bool fromSidebar = false)
        {
            if (fromSidebar)
            {
                _collHistory.Clear();
                _collReturnTo = null;
            }
            else if (PageCollection.Visibility == Visibility.Visible && _collView != null)
            {
                _collHistory.Push(_collView);
            }
            else
            {
                _collHistory.Clear();
                _collReturnTo = NavNow.IsChecked == true ? NavNow
                    : NavLiked.IsChecked == true ? NavLiked
                    : NavSearch.IsChecked == true ? NavSearch
                    : NavHistory.IsChecked == true ? NavHistory
                    : NavFriends.IsChecked == true ? NavFriends
                    : NavSettings.IsChecked == true ? NavSettings
                    : null;
            }

            _collView = view;
            ShowCollectionPage();
            if (!fromSidebar) ClearPlaylistSelection();
            await view();
        }

        private void ShowCollectionPage()
        {
            PageNow.Visibility = PageLiked.Visibility = PageSearch.Visibility = PageSettings.Visibility = PageHistory.Visibility = PageFriends.Visibility = Visibility.Collapsed;
            PageCollection.Visibility = Visibility.Visible;
            NavNow.IsChecked = NavLiked.IsChecked = NavSearch.IsChecked = NavSettings.IsChecked = NavHistory.IsChecked = NavFriends.IsChecked = false;
            CollBack.Visibility = _collHistory.Count > 0 || _collReturnTo != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void CollBack_Click(object sender, RoutedEventArgs e) => await GoBackAsync();

        private async Task GoBackAsync()
        {
            if (_collHistory.Count > 0)
            {
                _collView = _collHistory.Pop();
                ShowCollectionPage();
                await _collView();
                return;
            }
            GoTo(_collReturnTo ?? NavNow);
        }

        private Task OpenArtistAsync(string artistId) =>
            string.IsNullOrEmpty(artistId) ? Task.CompletedTask : OpenCollectionAsync(() => LoadArtistViewAsync(artistId));

        private Task OpenAlbumAsync(string albumId) =>
            string.IsNullOrEmpty(albumId) ? Task.CompletedTask : OpenCollectionAsync(() => LoadAlbumViewAsync(albumId));

        // ────────── заполнение страниц ──────────

        private void ResetCollection(CollectionKind kind, string kindText, string title, bool round)
        {
            _collKind = kind;
            _collArtist = null;
            _collPlaylist = null;
            CollKind.Text = kindText;
            CollTitle.Text = title;
            CollSub.Text = "";
            CollArtists.ItemsSource = null;
            CollArtists.Visibility = Visibility.Collapsed;
            CollCoverBrush.ImageSource = null;
            var radius = new CornerRadius(round ? 80 : 16);
            CollCover.CornerRadius = radius;
            CollCoverBack.CornerRadius = radius;
            CollCoverGlyph.Text = kind switch { CollectionKind.Artist => "", CollectionKind.Album => "", _ => "" };
            CollWave.Visibility = kind == CollectionKind.Artist ? Visibility.Visible : Visibility.Collapsed;
            CollMore.Visibility = kind == CollectionKind.Playlist ? Visibility.Visible : Visibility.Collapsed;
            CollAlbumsSection.Visibility = Visibility.Collapsed;
            CollAlbums.ItemsSource = null;
            CollTracksHeader.Text = kind == CollectionKind.Artist ? "Популярные треки" : "Треки";
            _collItems = new ObservableCollection<TrackItem>();
            CollList.ItemsSource = _collItems;
            CollEmpty.Visibility = Visibility.Collapsed;
            CollLoading.Visibility = Visibility.Visible;
            CollPlay.IsEnabled = CollShuffle.IsEnabled = false;
            CollDownload.Visibility = Visibility.Collapsed;
        }

        private void FillTracks(IEnumerable<YaTrack> tracks, string emptyText)
        {
            var current = _queue.Current?.Id;
            _collItems = new ObservableCollection<TrackItem>(tracks.Select((t, i) =>
                new TrackItem(t, i + 1) { IsLiked = _likedIds.Contains(t.Id), IsPlaying = t.Id == current }));
            CollList.ItemsSource = _collItems;
            CollLoading.Visibility = Visibility.Collapsed;
            CollPlay.IsEnabled = CollShuffle.IsEnabled = _collItems.Count > 0;
            // скачанный плейлист могли пополнить — новые треки докачаются
            if (_offline.Has(_collKey))
            {
                var playable = _collItems.Select(i => i.Track).Where(t => t.Available).ToList();
                _offline.Set(_collKey, CollTitle.Text, playable.Select(t => t.Id));
                QueueOfflineDownloads(playable);
            }
            UpdateOfflineButtons();
            if (_collItems.Count == 0)
            {
                CollEmpty.Text = emptyText;
                CollEmpty.Visibility = Visibility.Visible;
            }
        }

        private void CollFailed(Exception ex)
        {
            CollLoading.Visibility = Visibility.Collapsed;
            CollEmpty.Text = "Не удалось загрузить\n" + Short(ex.Message);
            CollEmpty.Visibility = Visibility.Visible;
        }

        private async Task LoadArtistViewAsync(string artistId)
        {
            if (_api == null) return;
            var gen = ++_collGen;
            ResetCollection(CollectionKind.Artist, "АРТИСТ", "Загрузка…", round: true);
            _collKey = "artist:" + artistId;
            try
            {
                var page = await _api.GetArtistAsync(artistId);
                if (gen != _collGen) return;

                _collArtist = page.Artist;
                CollTitle.Text = page.Artist.Name;
                var parts = new List<string>();
                if (page.Artist.Genres.Length > 0) parts.Add(string.Join(", ", page.Artist.Genres.Take(3)));
                if (page.Artist.LikesCount > 0)
                    parts.Add($"{page.Artist.LikesCount:N0} {Format.Plural((int)Math.Min(page.Artist.LikesCount, int.MaxValue), "лайк", "лайка", "лайков")}");
                CollSub.Text = string.Join(" · ", parts);
                CollCoverBrush.ImageSource = Covers.Load(page.Artist.CoverUrl(400), 320);
                _collLabel = $"АРТИСТ · {page.Artist.Name.ToUpperInvariant()}";

                if (page.Albums.Count > 0)
                {
                    CollAlbums.ItemsSource = page.Albums.Select(a => new AlbumItem(a)).ToList();
                    CollAlbumsSection.Visibility = Visibility.Visible;
                }
                FillTracks(page.Tracks, "У артиста нет доступных треков");
            }
            catch (Exception ex)
            {
                if (gen == _collGen) CollFailed(ex);
            }
        }

        private async Task LoadAlbumViewAsync(string albumId)
        {
            if (_api == null) return;
            var gen = ++_collGen;
            ResetCollection(CollectionKind.Album, "АЛЬБОМ", "Загрузка…", round: false);
            _collKey = "album:" + albumId;
            try
            {
                var (album, tracks) = await _api.GetAlbumAsync(albumId);
                if (gen != _collGen) return;

                CollKind.Text = album.Type == "single" ? "СИНГЛ" : "АЛЬБОМ";
                CollTitle.Text = album.Title;
                CollArtists.ItemsSource = ArtistLink.From(album.Artists);
                CollArtists.Visibility = album.Artists.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                var total = TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs));
                var sub = new List<string>();
                if (album.Year > 0) sub.Add(album.Year.ToString());
                sub.Add($"{tracks.Count} {Format.Plural(tracks.Count, "трек", "трека", "треков")}");
                if (total > TimeSpan.Zero) sub.Add(Format.Total(total));
                CollSub.Text = string.Join(" · ", sub);
                CollCoverBrush.ImageSource = Covers.Load(album.CoverUrl(400), 320);
                _collLabel = $"АЛЬБОМ · {album.Title.ToUpperInvariant()}";
                FillTracks(tracks, "В альбоме нет доступных треков");
            }
            catch (Exception ex)
            {
                if (gen == _collGen) CollFailed(ex);
            }
        }

        private async Task LoadPlaylistViewAsync(PlaylistItem item)
        {
            if (_api == null) return;
            var gen = ++_collGen;
            ResetCollection(CollectionKind.Playlist, "ПЛЕЙЛИСТ", item.Title, round: false);
            _collKey = $"playlist:{item.Playlist.OwnerUid}:{item.Playlist.Kind}";
            _collPlaylist = item;
            CollCoverBrush.ImageSource = Covers.Load(item.Playlist.CoverUrl(400), 320);
            try
            {
                var (playlist, tracks) = await _api.GetPlaylistAsync(item.Playlist.Kind);
                if (gen != _collGen) return;

                // обновляем ревизию/счётчик в сайдбаре
                var fresh = ReplacePlaylist(item, playlist);
                _collPlaylist = fresh;
                CollTitle.Text = playlist.Title;
                var total = TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs));
                CollSub.Text = tracks.Count == 0
                    ? "Пока пусто"
                    : $"{tracks.Count} {Format.Plural(tracks.Count, "трек", "трека", "треков")} · {Format.Total(total)}";
                if (playlist.CoverUrl(400) is { } cover) CollCoverBrush.ImageSource = Covers.Load(cover, 320);
                _collLabel = $"ПЛЕЙЛИСТ · {playlist.Title.ToUpperInvariant()}";
                FillTracks(tracks, "Плейлист пуст.\nДобавляйте треки правым кликом → «Добавить в плейлист».");
            }
            catch (Exception ex)
            {
                if (gen == _collGen) CollFailed(ex);
            }
        }

        /// <summary>Плейлист из поиска. Свой открываем обычной страницей — с переименованием и удалением треков.</summary>
        private Task OpenPublicPlaylistAsync(YaPlaylist playlist)
        {
            if (_account != null && playlist.OwnerUid == _account.Uid && FindPlaylist(playlist.Kind) is { } mine)
                return OpenCollectionAsync(() => LoadPlaylistViewAsync(mine));
            return OpenCollectionAsync(() => LoadPublicPlaylistViewAsync(playlist));
        }

        private async Task LoadPublicPlaylistViewAsync(YaPlaylist source)
        {
            if (_api == null) return;
            var gen = ++_collGen;
            ResetCollection(CollectionKind.PublicPlaylist, "ПЛЕЙЛИСТ", source.Title, round: false);
            _collKey = $"playlist:{source.OwnerUid}:{source.Kind}";
            CollCoverBrush.ImageSource = Covers.Load(source.CoverUrl(400), 320);
            try
            {
                var (playlist, tracks) = await _api.GetPlaylistAsync(source.OwnerUid, source.Kind);
                if (gen != _collGen) return;

                var title = playlist.Title.Length > 0 ? playlist.Title : source.Title;
                var owner = playlist.OwnerName.Length > 0 ? playlist.OwnerName : source.OwnerName;
                CollTitle.Text = title;
                var total = TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs));
                var sub = new List<string>();
                if (owner.Length > 0) sub.Add(owner);
                sub.Add($"{tracks.Count} {Format.Plural(tracks.Count, "трек", "трека", "треков")}");
                if (total > TimeSpan.Zero) sub.Add(Format.Total(total));
                CollSub.Text = string.Join(" · ", sub);
                if (playlist.CoverUrl(400) is { } cover) CollCoverBrush.ImageSource = Covers.Load(cover, 320);
                _collLabel = $"ПЛЕЙЛИСТ · {title.ToUpperInvariant()}";
                FillTracks(tracks, "Плейлист пуст");
            }
            catch (Exception ex)
            {
                if (gen == _collGen) CollFailed(ex);
            }
        }

        // ────────── кнопки страницы ──────────

        private async void CollPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_collItems.Count == 0) return;
            if (_settings.Shuffle)
            {
                _settings.Shuffle = false;
                UpdateModeButtons();
                PersistSettings();
            }
            await PlayFromListAsync(_collItems, 0, QueueSource.Collection, _collLabel);
        }

        private async void CollShuffle_Click(object sender, RoutedEventArgs e)
        {
            if (_collItems.Count == 0) return;
            if (!_settings.Shuffle)
            {
                _settings.Shuffle = true;
                UpdateModeButtons();
                PersistSettings();
            }
            await PlayFromListAsync(_collItems, new Random().Next(_collItems.Count), QueueSource.Collection, _collLabel);
        }

        private async void CollWave_Click(object sender, RoutedEventArgs e)
        {
            if (_collArtist == null) return;
            await StartWaveAsync(new[] { $"artist:{_collArtist.Id}" }, $"ВОЛНА ПО АРТИСТУ · {_collArtist.Name.ToUpperInvariant()}");
        }

        private void CollMore_Click(object sender, RoutedEventArgs e)
        {
            if (_collPlaylist != null) OpenMenu(BuildPlaylistMenu(_collPlaylist), (FrameworkElement)sender);
        }

        // ────────── ссылки ──────────

        private async void ArtistLink_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ArtistLink link }) return;
            e.Handled = true; // не запускать трек, по строке которого кликнули
            await OpenArtistAsync(link.Id);
        }

        private async void AlbumLink_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: TrackItem item }) return;
            if (string.IsNullOrEmpty(item.Track.AlbumId)) return;
            e.Handled = true;
            await OpenAlbumAsync(item.Track.AlbumId);
        }

        private async void NowAlbum_Click(object sender, MouseButtonEventArgs e)
        {
            if (_queue.Current is { AlbumId.Length: > 0 } t) await OpenAlbumAsync(t.AlbumId);
        }

        private async void AlbumCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: AlbumItem album }) await OpenAlbumAsync(album.Album.Id);
        }

        private void BarCover_Click(object sender, MouseButtonEventArgs e)
        {
            if (_queue.Current != null) GoTo(NavNow);
        }

        /// <summary>Колесо мыши листает ленту альбомов по горизонтали.</summary>
        private void Albums_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv || sv.ScrollableWidth <= 0) return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }
}
