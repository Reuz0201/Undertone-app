using System;
using System.Collections.Generic;
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
    /// <summary>Быстрый поиск по Ctrl+K: окно поверх всего, результаты по мере ввода, управление с клавиатуры.</summary>
    public partial class MainWindow
    {
        private readonly DispatcherTimer _quickDebounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private int _quickGen;
        private string _quickQuery = "";
        private List<YaTrack> _quickTracks = new();
        private bool _quickInit;

        private void QuickOpen_Click(object sender, RoutedEventArgs e) => OpenQuickSearch();

        private void OpenQuickSearch()
        {
            if (!_quickInit)
            {
                _quickInit = true;
                _quickDebounce.Tick += async (_, _) =>
                {
                    _quickDebounce.Stop();
                    await RunQuickSearchAsync();
                };
            }
            if (QuickOverlay.Visibility == Visibility.Visible)
            {
                QuickBox.SelectAll();
                return;
            }
            QuickOverlay.Visibility = Visibility.Visible;
            // появление: лёгкое приближение и проявление
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            QuickPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            if (QuickPanel.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            }
            QuickBox.SelectAll();
            if (QuickBox.Text.Trim().Length == 0) ShowQuickRecent();
            Dispatcher.InvokeAsync(() => { QuickBox.Focus(); Keyboard.Focus(QuickBox); }, DispatcherPriority.Input);
        }

        private void CloseQuickSearch()
        {
            _quickDebounce.Stop();
            QuickOverlay.Visibility = Visibility.Collapsed;
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
        }

        private void QuickOverlay_Backdrop(object sender, MouseButtonEventArgs e) => CloseQuickSearch();

        private void QuickBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            QuickPlaceholder.Visibility = QuickBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _quickDebounce.Stop();
            if (QuickBox.Text.Trim().Length == 0)
            {
                _quickGen++;
                QuickSpinner.Visibility = Visibility.Collapsed;
                QuickEsc.Visibility = Visibility.Visible;
                ShowQuickRecent();
                return;
            }
            _quickDebounce.Start();
        }

        /// <summary>Пустой запрос — недавно слушали, чтобы было с чего начать.</summary>
        private void ShowQuickRecent()
        {
            var current = _queue.Current?.Id;
            _quickTracks = _history.Select(h => h.Track).Where(t => t.Id != current).Take(6).ToList();
            var items = _quickTracks.Select((t, i) => new QuickItem
            {
                Kind = QuickKind.Track, Title = t.FullTitle, Sub = t.ArtistsText, Cover = t.CoverUrl(100), Payload = t,
                Section = i == 0 ? "НЕДАВНО СЛУШАЛИ" : "",
            }).ToList();
            SetQuickItems(items, items.Count == 0 ? "Начни печатать — найду треки, артистов и альбомы" : "");
        }

        private async Task RunQuickSearchAsync()
        {
            var query = QuickBox.Text.Trim();
            if (_api == null || query.Length == 0) return;
            var gen = ++_quickGen;
            QuickSpinner.Visibility = Visibility.Visible;
            QuickEsc.Visibility = Visibility.Collapsed;
            try
            {
                var r = await _api.SearchAsync(query);
                if (gen != _quickGen) return;
                _quickQuery = query;
                var items = new List<QuickItem>();

                // лучший результат — первым, если это не трек (трек и так первый в треках)
                if (r.Best is { } best && best.Type != "track")
                {
                    if (best.Artist is { } a) items.Add(ArtistItem(a, "ЛУЧШИЙ РЕЗУЛЬТАТ"));
                    else if (best.Album is { } al) items.Add(AlbumItem(al, "ЛУЧШИЙ РЕЗУЛЬТАТ"));
                    else if (best.Playlist is { } pl) items.Add(PlaylistItem(pl, "ЛУЧШИЙ РЕЗУЛЬТАТ"));
                }

                _quickTracks = r.Tracks.Where(t => t.Available).Take(5).ToList();
                items.AddRange(_quickTracks.Select((t, i) => new QuickItem
                {
                    Kind = QuickKind.Track, Title = t.FullTitle, Sub = $"{t.ArtistsText} · {Format.Time(t.Duration)}",
                    Cover = t.CoverUrl(100), Payload = t, Section = i == 0 ? "ТРЕКИ" : "",
                }));

                // свои файлы — сразу после треков Яндекса
                var local = _local.Search(query, 3);
                items.AddRange(local.Select((t, i) => new QuickItem
                {
                    Kind = QuickKind.Track, Title = t.FullTitle, Sub = $"{t.ArtistsText} · свой файл",
                    Cover = t.CoverUrl(100), Payload = t, Section = i == 0 ? "МОИ ФАЙЛЫ" : "",
                }));

                var bestArtist = (r.Best?.Artist)?.Id;
                items.AddRange(r.Artists.Where(a => a.Id != bestArtist).Take(3)
                    .Select((a, i) => ArtistItem(a, i == 0 ? "АРТИСТЫ" : "")));
                var bestAlbum = (r.Best?.Album)?.Id;
                items.AddRange(r.Albums.Where(a => a.Id != bestAlbum).Take(3)
                    .Select((a, i) => AlbumItem(a, i == 0 ? "АЛЬБОМЫ" : "")));

                if (items.Count > 0)
                    items.Add(new QuickItem { Kind = QuickKind.AllResults, Title = $"Все результаты по «{query}»", Sub = "Треки, артисты, альбомы и плейлисты" });
                SetQuickItems(items, items.Count == 0 ? $"Ничего не нашлось по «{query}»" : "");
            }
            catch (Exception ex)
            {
                if (gen == _quickGen) SetQuickItems(new List<QuickItem>(), "Не получилось найти: " + Short(ex.Message));
            }
            finally
            {
                if (gen == _quickGen)
                {
                    QuickSpinner.Visibility = Visibility.Collapsed;
                    QuickEsc.Visibility = Visibility.Visible;
                }
            }
        }

        private static QuickItem ArtistItem(YaArtist a, string section) => new()
        {
            Kind = QuickKind.Artist, Title = a.Name, Payload = a, Cover = a.CoverUrl(100), Section = section,
            Sub = a.Genres.Length > 0 ? "Артист · " + string.Join(", ", a.Genres.Take(2)) : "Артист",
        };

        private static QuickItem AlbumItem(YaAlbum a, string section) => new()
        {
            Kind = QuickKind.Album, Title = a.Title, Payload = a, Cover = a.CoverUrl(100), Section = section,
            Sub = string.Join(" · ", new[] { a.Type == "single" ? "Сингл" : "Альбом", a.ArtistsText, a.Year > 0 ? a.Year.ToString() : "" }
                .Where(x => x.Length > 0)),
        };

        private static QuickItem PlaylistItem(YaPlaylist p, string section) => new()
        {
            Kind = QuickKind.Playlist, Title = p.Title, Payload = p, Cover = p.CoverUrl(100), Section = section,
            Sub = "Плейлист" + (p.OwnerName.Length > 0 ? " · " + p.OwnerName : ""),
        };

        private void SetQuickItems(List<QuickItem> items, string hint)
        {
            QuickList.ItemsSource = items;
            QuickList.SelectedIndex = items.Count > 0 ? 0 : -1;
            QuickHint.Text = hint;
            QuickHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void QuickBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var count = QuickList.Items.Count;
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    CloseQuickSearch();
                    break;
                case Key.Down when count > 0:
                    e.Handled = true;
                    QuickList.SelectedIndex = (QuickList.SelectedIndex + 1) % count;
                    QuickList.ScrollIntoView(QuickList.SelectedItem);
                    break;
                case Key.Up when count > 0:
                    e.Handled = true;
                    QuickList.SelectedIndex = (QuickList.SelectedIndex - 1 + count) % count;
                    QuickList.ScrollIntoView(QuickList.SelectedItem);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    // запрос ещё не долетел — ищем сразу, без ожидания паузы в наборе
                    if (_quickDebounce.IsEnabled)
                    {
                        _quickDebounce.Stop();
                        await RunQuickSearchAsync();
                    }
                    if (QuickList.SelectedItem is QuickItem item)
                        await ActivateQuickAsync(item, playNext: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                    break;
            }
        }

        private async void QuickItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: QuickItem item })
                await ActivateQuickAsync(item, playNext: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }

        private async Task ActivateQuickAsync(QuickItem item, bool playNext)
        {
            switch (item.Kind)
            {
                case QuickKind.Track when item.Payload is YaTrack track:
                    CloseQuickSearch();
                    if (playNext)
                    {
                        PlayNext(track);
                        return;
                    }
                    // включаем со всей пятёркой найденного — дальше играет то, что тоже подходило под запрос
                    var index = _quickTracks.FindIndex(t => t.Id == track.Id);
                    var list = index >= 0
                        ? _quickTracks.Select((t, i) => new TrackItem(t, i + 1)).ToList()
                        : new List<TrackItem> { new(track, 1) }; // свой файл — один
                    await PlayFromListAsync(list, Math.Max(0, index), QueueSource.Search);
                    break;
                case QuickKind.Artist when item.Payload is YaArtist artist:
                    CloseQuickSearch();
                    await OpenArtistAsync(artist.Id);
                    break;
                case QuickKind.Album when item.Payload is YaAlbum album:
                    CloseQuickSearch();
                    await OpenAlbumAsync(album.Id);
                    break;
                case QuickKind.Playlist when item.Payload is YaPlaylist playlist:
                    CloseQuickSearch();
                    await OpenPublicPlaylistAsync(playlist);
                    break;
                case QuickKind.AllResults:
                    var query = _quickQuery;
                    CloseQuickSearch();
                    GoTo(NavSearch);
                    SearchBox.Text = query;
                    _lastQuery = "";
                    _searchDebounce.Stop();
                    await SearchAsync();
                    break;
            }
        }
    }
}
