using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Контекстные меню: трек (правый клик / «⋯»), плейлист.</summary>
    public partial class MainWindow
    {
        private static readonly FontFamily IconFontFamily = (FontFamily)Application.Current.Resources["IconFont"];

        private static MenuItem Item(string header, string glyph, Action? onClick = null)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = new TextBlock { Text = glyph, FontFamily = IconFontFamily, FontSize = 13 },
            };
            if (onClick != null) item.Click += (_, _) => onClick();
            return item;
        }

        private static void OpenMenu(ContextMenu menu, FrameworkElement target, bool atMouse = false)
        {
            menu.PlacementTarget = target;
            menu.Placement = atMouse ? PlacementMode.MousePoint : PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private ContextMenu BuildTrackMenu(YaTrack track, TrackItem? row, ItemsControl? list)
        {
            var menu = new ContextMenu();

            if (row != null && list == UpNextList)
            {
                menu.Items.Add(Item("Сыграть следующим", "", () => MoveToNext(row)));
                menu.Items.Add(Item("Убрать из очереди", "\uE711", () => RemoveFromQueue(row)));
                menu.Items.Add(new Separator());
            }
            else
            {
                menu.Items.Add(Item("Играть следующим", "", () => PlayNext(track)));
                menu.Items.Add(Item("Добавить в конец очереди", "\uE710", () => AddToQueueEnd(track)));
            }

            var add = Item("Добавить в плейлист", "");
            add.Items.Add(Item("Новый плейлист…", "", () => _ = CreatePlaylistAsync(track)));
            if (_playlists.Count > 0) add.Items.Add(new Separator());
            foreach (var pl in _playlists.ToList())
            {
                var target = pl;
                add.Items.Add(Item(pl.Title, "", () => _ = AddTrackToPlaylistAsync(target, track)));
            }
            menu.Items.Add(add);

            if (row != null && list == CollList && _collKind == CollectionKind.Playlist)
                menu.Items.Add(Item("Убрать из плейлиста", "", () => _ = RemoveFromPlaylistAsync(row)));

            menu.Items.Add(new Separator());
            var liked = _likedIds.Contains(track.Id);
            menu.Items.Add(Item(liked ? "Убрать из «Мне нравится»" : "Мне нравится", liked ? "" : "",
                () => _ = ToggleLikeAsync(track)));

            if (track.ArtistRefs.Length > 0 || track.AlbumId.Length > 0) menu.Items.Add(new Separator());
            if (track.ArtistRefs.Length == 1)
            {
                var id = track.ArtistRefs[0].Id;
                menu.Items.Add(Item($"Перейти к артисту: {track.ArtistRefs[0].Name}", "", () => _ = OpenArtistAsync(id)));
            }
            else if (track.ArtistRefs.Length > 1)
            {
                var artists = Item("Перейти к артисту", "");
                foreach (var a in track.ArtistRefs)
                {
                    var id = a.Id;
                    artists.Items.Add(Item(a.Name, "", () => _ = OpenArtistAsync(id)));
                }
                menu.Items.Add(artists);
            }
            if (track.AlbumId.Length > 0)
            {
                var albumId = track.AlbumId;
                menu.Items.Add(Item(string.IsNullOrEmpty(track.Album) ? "Перейти к альбому" : $"Перейти к альбому: {track.Album}",
                    "", () => _ = OpenAlbumAsync(albumId)));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Поделиться: скопировать ссылку", "", () => ShareTrack(track)));
            return menu;
        }

        /// <summary>Ссылка на трек в Яндекс Музыке — в буфер обмена, чтобы кинуть в мессенджер.</summary>
        private void ShareTrack(YaTrack track)
        {
            if (LocalUnsupported(track, "Это твой файл — ссылки на него нет")) return;
            var url = track.AlbumId.Length > 0
                ? $"https://music.yandex.ru/album/{track.AlbumId}/track/{track.Id}"
                : $"https://music.yandex.ru/track/{track.Id}";
            try
            {
                Clipboard.SetText(url);
                ShowToast($"Ссылка на «{track.FullTitle}» скопирована");
            }
            catch (Exception) { ShowToast("Буфер обмена занят — попробуй ещё раз"); }
        }

        private ContextMenu BuildPlaylistMenu(PlaylistItem item)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Переименовать", "", () => _ = RenamePlaylistAsync(item)));
            menu.Items.Add(Item("Удалить плейлист", "", () => _ = DeletePlaylistAsync(item)));
            return menu;
        }

        private void Row_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem { DataContext: TrackItem row } container) return;
            e.Handled = true;
            container.IsSelected = true;
            var list = ItemsControl.ItemsControlFromItemContainer(container);
            OpenMenu(BuildTrackMenu(row.Track, row, list), container, atMouse: true);
        }

        private void NowMore_Click(object sender, RoutedEventArgs e)
        {
            if (_queue.Current is { } track)
                OpenMenu(BuildTrackMenu(track, null, null), (FrameworkElement)sender);
        }

        /// <summary>«Играть следующим»: вставка сразу после текущего трека.</summary>
        private void PlayNext(YaTrack track)
        {
            if (_queue.Current == null)
            {
                _waveSession = null;
                _waveKey = "";
                _queueLabel = "СЕЙЧАС ИГРАЕТ";
                _queue.Set(new[] { track }, 0, QueueSource.Collection);
                _ = PlayCurrentAsync();
                return;
            }
            if (RoomSuggestInstead(track)) return;
            _queue.InsertNext(track);
            QueueEdited(); // в том числе — сообщает комнате
            ShowToast($"«{track.FullTitle}» сыграет следующим");
        }
    }
}
