using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Плейлисты пользователя: сайдбар, создание/переименование/удаление, добавление и удаление треков.</summary>
    public partial class MainWindow
    {
        private ObservableCollection<PlaylistItem> _playlists = new();
        private bool _suppressPlaylistNav;

        // ────────── список в сайдбаре ──────────

        private async Task LoadPlaylistsAsync()
        {
            if (_api == null) return;
            try
            {
                var list = await _api.GetPlaylistsAsync();
                _playlists = new ObservableCollection<PlaylistItem>(list.Select(p => new PlaylistItem(p)));
                PlaylistNav.ItemsSource = _playlists;
            }
            catch (Exception)
            {
                // не критично — сайдбар останется пустым, можно создать новый
            }
            UpdatePlaylistsEmpty();
        }

        private void UpdatePlaylistsEmpty() =>
            PlaylistsEmpty.Visibility = _playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void ClearPlaylistSelection()
        {
            _suppressPlaylistNav = true;
            PlaylistNav.SelectedItem = null;
            _suppressPlaylistNav = false;
        }

        private async void PlaylistNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPlaylistNav || PlaylistNav.SelectedItem is not PlaylistItem item) return;
            var kind = item.Playlist.Kind;
            // по kind, а не по объекту: после изменений элемент в списке заменяется свежим
            await OpenCollectionAsync(() => LoadPlaylistViewAsync(FindPlaylist(kind) ?? item), fromSidebar: true);
        }

        private void PlaylistNav_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem { DataContext: PlaylistItem item } container) return;
            e.Handled = true;
            OpenMenu(BuildPlaylistMenu(item), container, atMouse: true);
        }

        private PlaylistItem? FindPlaylist(string kind) => _playlists.FirstOrDefault(p => p.Playlist.Kind == kind);

        /// <summary>Подменяет элемент свежими данными (ревизия, число треков, обложка), не дёргая навигацию.</summary>
        private PlaylistItem ReplacePlaylist(PlaylistItem old, YaPlaylist fresh)
        {
            var updated = new PlaylistItem(fresh.Title.Length > 0 ? fresh : old.Playlist);
            var index = _playlists.IndexOf(FindPlaylist(old.Playlist.Kind) ?? old);
            if (index < 0) return updated;

            var wasSelected = PlaylistNav.SelectedIndex == index;
            _suppressPlaylistNav = true;
            _playlists[index] = updated;
            if (wasSelected) PlaylistNav.SelectedIndex = index;
            _suppressPlaylistNav = false;

            if (_collPlaylist?.Playlist.Kind == fresh.Kind) _collPlaylist = updated;
            return updated;
        }

        private bool IsViewing(PlaylistItem item) =>
            PageCollection.Visibility == Visibility.Visible && _collKind == CollectionKind.Playlist &&
            _collPlaylist?.Playlist.Kind == item.Playlist.Kind;

        // ────────── создание / переименование / удаление ──────────

        private async void NewPlaylist_Click(object sender, RoutedEventArgs e) => await CreatePlaylistAsync(null);

        private async Task CreatePlaylistAsync(YaTrack? addTrack)
        {
            if (_api == null) return;
            var title = await ShowDialogAsync("Новый плейлист",
                addTrack != null ? $"И сразу добавим туда «{addTrack.FullTitle}»." : "",
                "", "Создать");
            if (string.IsNullOrWhiteSpace(title)) return;

            try
            {
                var created = await _api.CreatePlaylistAsync(title.Trim());
                var item = new PlaylistItem(created);
                _playlists.Insert(0, item);
                UpdatePlaylistsEmpty();
                if (addTrack != null) await AddTrackToPlaylistAsync(item, addTrack);
                else ShowToast($"Плейлист «{created.Title}» создан");
            }
            catch (Exception ex)
            {
                ShowToast("Не удалось создать плейлист: " + Short(ex.Message));
            }
        }

        private async Task RenamePlaylistAsync(PlaylistItem item)
        {
            if (_api == null) return;
            var title = await ShowDialogAsync("Переименовать плейлист", "", item.Title, "Сохранить");
            if (string.IsNullOrWhiteSpace(title) || title.Trim() == item.Title) return;
            try
            {
                var renamed = await _api.RenamePlaylistAsync(item.Playlist.Kind, title.Trim());
                ReplacePlaylist(item, renamed);
                if (IsViewing(item))
                {
                    CollTitle.Text = renamed.Title;
                    _collLabel = $"ПЛЕЙЛИСТ · {renamed.Title.ToUpperInvariant()}";
                }
            }
            catch (Exception ex)
            {
                ShowToast("Не удалось переименовать: " + Short(ex.Message));
            }
        }

        private async Task DeletePlaylistAsync(PlaylistItem item)
        {
            if (_api == null) return;
            var ok = await ShowDialogAsync("Удалить плейлист?",
                $"«{item.Title}» будет удалён из Яндекс Музыки на всех устройствах.", null, "Удалить");
            if (ok == null) return;
            try
            {
                var viewing = IsViewing(item);
                await _api.DeletePlaylistAsync(item.Playlist.Kind);
                var existing = FindPlaylist(item.Playlist.Kind);
                if (existing != null)
                {
                    _suppressPlaylistNav = true;
                    _playlists.Remove(existing);
                    _suppressPlaylistNav = false;
                }
                UpdatePlaylistsEmpty();
                if (viewing) GoTo(NavNow);
                ShowToast($"Плейлист «{item.Title}» удалён");
            }
            catch (Exception ex)
            {
                ShowToast("Не удалось удалить: " + Short(ex.Message));
            }
        }

        // ────────── треки в плейлистах ──────────

        /// <summary>Изменения плейлиста идут по ревизии; если она устарела (правили с телефона) — берём свежую и повторяем.</summary>
        private async Task<YaPlaylist> WithFreshRevisionAsync(PlaylistItem item, Func<YaPlaylist, Task<YaPlaylist>> change)
        {
            var api = _api ?? throw new InvalidOperationException("Нет подключения");
            try
            {
                return await change(item.Playlist);
            }
            catch (YaApiException ex) when (!ex.IsUnauthorized)
            {
                var (fresh, _) = await api.GetPlaylistAsync(item.Playlist.Kind);
                return await change(fresh);
            }
        }

        private async Task AddTrackToPlaylistAsync(PlaylistItem item, YaTrack track)
        {
            if (_api == null) return;
            var api = _api;
            try
            {
                var target = FindPlaylist(item.Playlist.Kind) ?? item;
                var updated = await WithFreshRevisionAsync(target, pl => api.AddToPlaylistAsync(pl, track));
                var fresh = ReplacePlaylist(target, updated);
                ShowToast($"Добавлено в «{fresh.Title}»");
                if (IsViewing(fresh)) await LoadPlaylistViewAsync(fresh);
            }
            catch (Exception ex)
            {
                ShowToast("Не удалось добавить: " + Short(ex.Message));
            }
        }

        private async Task RemoveFromPlaylistAsync(TrackItem row)
        {
            if (_api == null || _collPlaylist == null) return;
            var api = _api;
            var index = _collItems.IndexOf(row);
            if (index < 0) return;
            var item = _collPlaylist;
            try
            {
                var updated = await WithFreshRevisionAsync(item, pl => api.RemoveFromPlaylistAsync(pl, index));
                var fresh = ReplacePlaylist(item, updated);
                _collItems.Remove(row);
                for (int i = 0; i < _collItems.Count; i++) _collItems[i].Index = i + 1;
                CollSub.Text = $"{_collItems.Count} {Format.Plural(_collItems.Count, "трек", "трека", "треков")}";
                ShowToast($"Убрано из «{fresh.Title}»");
            }
            catch (Exception ex)
            {
                ShowToast("Не удалось убрать: " + Short(ex.Message));
            }
        }

        // ────────── диалог ──────────

        private TaskCompletionSource<string?>? _dialog;

        /// <summary>
        /// Модальный диалог в стиле приложения. input == null — только подтверждение (вернёт "" или null).
        /// Иначе — поле ввода, вернёт текст или null при отмене.
        /// </summary>
        private Task<string?> ShowDialogAsync(string title, string message, string? input, string okText)
        {
            _dialog?.TrySetResult(null);
            _dialog = new TaskCompletionSource<string?>();

            DialogTitle.Text = title;
            DialogMessage.Text = message;
            DialogMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            DialogInput.Visibility = input == null ? Visibility.Collapsed : Visibility.Visible;
            DialogInput.Text = input ?? "";
            DialogOk.Content = okText;
            DialogOverlay.Visibility = Visibility.Visible;

            if (input != null)
                Dispatcher.InvokeAsync(() =>
                {
                    DialogInput.Focus();
                    DialogInput.SelectAll();
                }, DispatcherPriority.Input);

            return _dialog.Task;
        }

        private void CloseDialog(string? result)
        {
            DialogOverlay.Visibility = Visibility.Collapsed;
            var tcs = _dialog;
            _dialog = null;
            tcs?.TrySetResult(result);
        }

        private void DialogOk_Click(object sender, RoutedEventArgs e) => SubmitDialog();
        private void DialogCancel_Click(object sender, RoutedEventArgs e) => CloseDialog(null);

        private void SubmitDialog()
        {
            if (DialogInput.Visibility == Visibility.Visible)
            {
                var text = DialogInput.Text.Trim();
                if (text.Length == 0) { DialogInput.Focus(); return; }
                CloseDialog(text);
            }
            else
            {
                CloseDialog("");
            }
        }

        private void DialogInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; SubmitDialog(); }
        }
    }
}
