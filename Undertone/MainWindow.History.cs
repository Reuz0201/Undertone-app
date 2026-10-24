using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Страница «История»: что играло недавно (локально, последние 300 треков).</summary>
    public partial class MainWindow
    {
        private List<HistoryEntry> _history = new();
        private ObservableCollection<TrackItem> _historyItems = new();
        private DispatcherTimer? _historySaveTimer;
        private bool _historyDirty;

        private void LoadHistory()
        {
            _history = _account != null ? HistoryStore.Load(_account.Uid) : new List<HistoryEntry>();
            _historyDirty = true;
            if (PageHistory.Visibility == Visibility.Visible) RebuildHistory();
        }

        private void AddToHistory(YaTrack track)
        {
            _history.RemoveAll(h => h.Track.Id == track.Id); // повтор — просто поднимаем наверх
            _history.Insert(0, new HistoryEntry { Track = track, PlayedAt = DateTime.Now });
            if (_history.Count > HistoryStore.MaxEntries) _history.RemoveRange(HistoryStore.MaxEntries, _history.Count - HistoryStore.MaxEntries);
            _historyDirty = true;
            if (PageHistory.Visibility == Visibility.Visible) RebuildHistory();
            ScheduleHistorySave();
        }

        private void ScheduleHistorySave()
        {
            if (_historySaveTimer == null)
            {
                _historySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _historySaveTimer.Tick += (_, _) =>
                {
                    _historySaveTimer.Stop();
                    if (_account != null)
                    {
                        _ = HistoryStore.SaveAsync(_account.Uid, _history);
                        _ = _stats.SaveAsync(_account.Uid);
                    }
                };
            }
            _historySaveTimer.Stop();
            _historySaveTimer.Start();
        }

        private void ShowHistory()
        {
            if (_historyDirty) RebuildHistory();
        }

        private void RebuildHistory()
        {
            _historyDirty = false;
            var current = _queue.Current?.Id;
            _historyItems = new ObservableCollection<TrackItem>(_history.Select((h, i) =>
                new TrackItem(h.Track, i + 1) { IsLiked = _likedIds.Contains(h.Track.Id), IsPlaying = h.Track.Id == current }));
            HistoryList.ItemsSource = _historyItems;
            HistorySub.Text = _history.Count == 0
                ? "Пока пусто"
                : $"{_history.Count} {Format.Plural(_history.Count, "трек", "трека", "треков")} · последний — {When(_history[0].PlayedAt)}";
            HistoryEmpty.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryPlay.IsEnabled = HistoryClear.IsEnabled = _history.Count > 0;
        }

        private static string When(DateTime t)
        {
            var ago = DateTime.Now - t;
            if (ago < TimeSpan.FromMinutes(1)) return "только что";
            if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} мин назад";
            if (t.Date == DateTime.Today) return $"сегодня в {t:HH:mm}";
            if (t.Date == DateTime.Today.AddDays(-1)) return $"вчера в {t:HH:mm}";
            return t.ToString("d MMMM, HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        }

        private async void HistoryPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_historyItems.Count == 0) return;
            await PlayFromListAsync(_historyItems, 0, QueueSource.Collection, "ИЗ ИСТОРИИ");
        }

        private async void HistoryClear_Click(object sender, RoutedEventArgs e)
        {
            var ok = await ShowDialogAsync("Очистить историю?",
                "Список недавно игравших треков на этом компьютере станет пустым. История в Яндекс Музыке не изменится.",
                null, "Очистить");
            if (ok == null) return;
            _history.Clear();
            RebuildHistory();
            if (_account != null) _ = HistoryStore.SaveAsync(_account.Uid, _history);
        }
    }
}
