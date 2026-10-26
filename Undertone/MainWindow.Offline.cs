using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// Офлайн: «Скачать» у «Мне нравится», альбома или плейлиста. Треки качаются по одному в фоне,
    /// закрепляются в кэше (лимит и очистка их не трогают) и играют без интернета.
    /// </summary>
    public partial class MainWindow
    {
        private const string LikedKey = "liked";
        private OfflineStore _offline = new();
        private string _collKey = "";
        private readonly Queue<YaTrack> _offlineQueue = new();
        private readonly HashSet<string> _offlineQueued = new();
        private bool _offlineRunning;
        private DateTime _offlineUiAt;

        private void LoadOffline()
        {
            _offline = _account != null ? OfflineStore.Load(_account.Uid) : new OfflineStore();
            _cache.IsPinned = id => _offline.IsPinned(id);
        }

        /// <summary>Скачан ли трек полностью (в любом формате — качество могли поменять после скачивания).</summary>
        private bool IsOfflineReady(YaTrack t) =>
            _cache.FindCached(t.Id, "mp3", "lq.mp3", "flac", "mp4", "m4a", "aac") != null;

        // ────────── кнопки ──────────

        private async void LikedDownload_Click(object sender, RoutedEventArgs e) =>
            await ToggleOfflineAsync(LikedKey, "Мне нравится", _likedTracks);

        private async void CollDownload_Click(object sender, RoutedEventArgs e) =>
            await ToggleOfflineAsync(_collKey, CollTitle.Text, _collItems.Select(i => i.Track).ToList());

        private async Task ToggleOfflineAsync(string key, string title, IReadOnlyList<YaTrack> tracks)
        {
            if (key.Length == 0 || tracks.Count == 0) return;
            if (_offline.Has(key))
            {
                var ok = await ShowDialogAsync("Удалить скачанное?",
                    $"«{title}» больше не будет доступен без интернета. Треки останутся в обычном кэше, пока он не заполнится.",
                    null, "Удалить");
                if (ok == null) return;
                _offline.Remove(key);
                ShowToast($"«{title}» больше не хранится офлайн");
                UpdateOfflineButtons();
                return;
            }
            if (_api is { SupportsDownload: false } api)
            {
                ShowToast($"{api.Kind.Title()} не разрешает скачивать музыку в сторонние плееры — офлайн только в его приложении", 4);
                return;
            }
            var playable = tracks.Where(t => t.Available).ToList();
            _offline.Set(key, title, playable.Select(t => t.Id));
            QueueOfflineDownloads(playable);
            var missing = playable.Count(t => !IsOfflineReady(t));
            ShowToast(missing == 0
                ? $"«{title}» уже на диске — будет играть и без интернета"
                : $"Скачиваю «{title}»: {missing} {Format.Plural(missing, "трек", "трека", "треков")}", 3);
            UpdateOfflineButtons();
        }

        /// <summary>Лайки поменялись — скачанная «Мне нравится» догружает новые и забывает убранные.</summary>
        private void SyncOfflineLiked()
        {
            if (!_offline.Has(LikedKey)) return;
            var playable = _likedTracks.Where(t => t.Available).ToList();
            _offline.Set(LikedKey, "Мне нравится", playable.Select(t => t.Id));
            QueueOfflineDownloads(playable);
            UpdateOfflineButtons();
        }

        private void QueueOfflineDownloads(IEnumerable<YaTrack> tracks)
        {
            foreach (var t in tracks)
                if (!IsOfflineReady(t) && _offlineQueued.Add(t.Id)) _offlineQueue.Enqueue(t);
            if (!_offlineRunning) _ = RunOfflineQueueAsync();
        }

        /// <summary>По одному треку, чтобы не забивать канал тому, что играет сейчас.</summary>
        private async Task RunOfflineQueueAsync()
        {
            _offlineRunning = true;
            try
            {
                var failures = 0;
                while (_offlineQueue.Count > 0)
                {
                    var api = _api;
                    if (api == null) break;
                    var t = _offlineQueue.Dequeue();
                    _offlineQueued.Remove(t.Id);
                    if (!_offline.IsPinned(t.Id) || IsOfflineReady(t)) continue;
                    try
                    {
                        if (EffectiveQuality == AudioQuality.Lossless && !_noLossless.Contains(t.Id) &&
                            await DownloadLosslessAsync(api, t, CancellationToken.None) != null) { }
                        else await _cache.GetAsync(t.Id, Mp3Resolver(api, t), Mp3Ext, CancellationToken.None);
                        failures = 0;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Офлайн: {t.Id} не скачан", ex);
                        // сеть пропала — не крутим очередь вхолостую, продолжим при следующем нажатии/запуске
                        if (++failures >= 3) break;
                    }
                    if (DateTime.UtcNow - _offlineUiAt > TimeSpan.FromSeconds(1))
                    {
                        _offlineUiAt = DateTime.UtcNow;
                        UpdateOfflineButtons();
                    }
                }
            }
            finally
            {
                _offlineRunning = false;
                _offlineQueue.Clear();
                _offlineQueued.Clear();
                UpdateOfflineButtons();
            }
        }

        // ────────── состояние кнопок ──────────

        private void UpdateOfflineButtons()
        {
            SetOfflineButton(LikedDownloadText, LikedDownloadGlyph, LikedDownload, LikedKey, _likedTracks);
            SetOfflineButton(CollDownloadText, CollDownloadGlyph, CollDownload, _collKey, _collItems.Select(i => i.Track).ToList());
        }

        private void SetOfflineButton(TextBlock text, TextBlock glyph, Button button, string key, IReadOnlyList<YaTrack> tracks)
        {
            button.Visibility = key.Length > 0 && tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_offline.Has(key))
            {
                text.Text = "Скачать";
                glyph.Text = "";
                glyph.ClearValue(TextBlock.ForegroundProperty);
                button.ToolTip = "Скачать, чтобы слушать без интернета";
                return;
            }
            var playable = tracks.Where(t => t.Available).ToList();
            var ready = playable.Count(IsOfflineReady);
            if (ready >= playable.Count)
            {
                text.Text = "Скачано";
                glyph.Text = "";
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "LiveAccent");
                button.ToolTip = "Доступно без интернета. Нажми, чтобы удалить с диска";
            }
            else
            {
                text.Text = $"Скачиваю {ready}/{playable.Count}";
                glyph.Text = "";
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "LiveAccent");
                button.ToolTip = "Качается в фоне. Нажми, чтобы отменить";
                if (!_offlineRunning) QueueOfflineDownloads(playable);
            }
        }
    }
}
