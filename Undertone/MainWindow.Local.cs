using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>«Мои файлы»: своя музыка из папок на диске — в тех же очередях, поиске и истории, что и Яндекс.</summary>
    public partial class MainWindow
    {
        private readonly LocalLibrary _local = new();
        private ObservableCollection<TrackItem> _localItems = new();
        private CancellationTokenSource? _localScan;
        private bool _localLoaded;

        private bool IsLocalTrack(YaTrack t) => LocalLibrary.IsLocal(t);

        /// <summary>При запуске: индекс сразу, пересканирование — в фоне.</summary>
        private void InitLocal()
        {
            if (_localLoaded) return;
            _localLoaded = true;
            _local.LoadIndex();
            if (_settings.LocalFolders.Count > 0) _ = RescanLocalAsync(quiet: true);
        }

        private void ShowLocal()
        {
            InitLocal();
            RebuildLocal();
        }

        private void RebuildLocal()
        {
            var filter = LocalFilter.Text.Trim();
            var tracks = filter.Length > 0 ? _local.Search(filter, int.MaxValue) : _local.Tracks();
            var current = _queue.Current?.Id;
            _localItems = new ObservableCollection<TrackItem>(tracks.Select((t, i) => new TrackItem(t, i + 1) { IsPlaying = t.Id == current }));
            LocalList.ItemsSource = _localItems;
            var total = TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs));
            var folders = _settings.LocalFolders.Count;
            LocalSub.Text = _local.Count == 0
                ? (folders == 0 ? "Папки не выбраны" : "Треков не найдено")
                : $"{_local.Count} {Format.Plural(_local.Count, "трек", "трека", "треков")} · {Format.Total(TimeSpan.FromMilliseconds(_local.Tracks().Sum(t => t.DurationMs)))} · " +
                  $"{folders} {Format.Plural(folders, "папка", "папки", "папок")}";
            if (filter.Length > 0 && _localItems.Count > 0) LocalSub.Text += $" · найдено {_localItems.Count} ({Format.Total(total)})";
            LocalEmpty.Visibility = _localItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LocalEmptyText.Text = _local.Count > 0 && filter.Length > 0
                ? $"Ничего не нашлось по «{filter}»"
                : folders == 0
                    ? "Добавь папку с mp3, flac или m4a — треки появятся здесь, в быстром поиске и будут играть в одной очереди с Яндексом."
                    : "В выбранных папках нет музыки (ищу mp3, flac, m4a, aac, wav, wma).";
            LocalPlay.IsEnabled = _localItems.Count > 0;
        }

        private void LocalFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_ready) RebuildLocal();
        }

        private async void LocalPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_localItems.Count > 0) await PlayFromListAsync(_localItems, 0, QueueSource.Collection, "МОИ ФАЙЛЫ");
        }

        private async void LocalShuffle_Click(object sender, RoutedEventArgs e)
        {
            if (_localItems.Count == 0) return;
            var shuffled = _localItems.OrderBy(_ => Random.Shared.Next()).ToList();
            await PlayFromListAsync(shuffled, 0, QueueSource.Collection, "МОИ ФАЙЛЫ · ВПЕРЕМЕШКУ");
        }

        private async void LocalAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с музыкой", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            var added = 0;
            foreach (var folder in dialog.FolderNames)
            {
                if (_settings.LocalFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))) continue;
                _settings.LocalFolders.Add(folder);
                added++;
            }
            if (added == 0) return;
            PersistSettings();
            await RescanLocalAsync(quiet: false);
        }

        private async void LocalRescan_Click(object sender, RoutedEventArgs e) => await RescanLocalAsync(quiet: false);

        private void LocalFolders_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            if (_settings.LocalFolders.Count == 0)
                menu.Items.Add(new MenuItem { Header = "Папок пока нет", IsEnabled = false });
            foreach (var folder in _settings.LocalFolders.ToList())
            {
                var f = folder;
                menu.Items.Add(Item($"Убрать: {f}", "", () =>
                {
                    _settings.LocalFolders.Remove(f);
                    PersistSettings();
                    _ = RescanLocalAsync(quiet: false);
                }));
            }
            OpenMenu(menu, LocalFoldersBtn);
        }

        private async Task RescanLocalAsync(bool quiet)
        {
            _localScan?.Cancel();
            var cts = _localScan = new CancellationTokenSource();
            LocalRescan.IsEnabled = false;
            if (!quiet) LocalSub.Text = "Ищу музыку в папках…";
            var progress = new Progress<int>(n =>
            {
                if (!quiet && PageLocal.Visibility == Visibility.Visible) LocalSub.Text = $"Читаю теги… {n}";
            });
            try
            {
                var count = await _local.ScanAsync(_settings.LocalFolders.ToList(), progress, cts.Token);
                if (!quiet) ShowToast(count == 0 ? "Музыки в папках не нашлось" : $"Мои файлы: {count} {Format.Plural(count, "трек", "трека", "треков")}");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn("Сканирование своих файлов", ex);
                if (!quiet) ShowToast("Не получилось просканировать: " + Short(ex.Message));
            }
            finally
            {
                if (_localScan == cts) LocalRescan.IsEnabled = true;
            }
            if (PageLocal.Visibility == Visibility.Visible) RebuildLocal();
        }

        /// <summary>Свои файлы нельзя дать друзьям в комнате и нельзя лайкнуть в Яндексе — объясняем вместо ошибки.</summary>
        private bool LocalUnsupported(YaTrack t, string what)
        {
            if (!IsLocalTrack(t)) return false;
            ShowToast(what, 3);
            return true;
        }
    }
}
