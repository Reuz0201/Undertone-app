using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Текст песни на странице «Сейчас играет»: синхронный (с подсветкой строки) или обычный.</summary>
    public partial class MainWindow
    {
        private bool _lyricsMode;
        private string? _lyricsTrackId;
        private List<LyricLine> _lyrics = new();
        private int _lyricsActive = -1;
        private int _lyricsGen;
        private readonly Dictionary<string, (bool Synced, string Text)?> _lyricsCache = new();

        private void NowLyrics_Click(object sender, RoutedEventArgs e) => ToggleLyrics();

        private void ToggleLyrics()
        {
            _lyricsMode = !_lyricsMode;
            UpNextPanel.Visibility = _lyricsMode ? Visibility.Collapsed : Visibility.Visible;
            LyricsPanel.Visibility = _lyricsMode ? Visibility.Visible : Visibility.Collapsed;
            NowLyricsText.Text = _lyricsMode ? "Очередь" : "Текст";
            NowLyricsGlyph.Text = _lyricsMode ? "" : "";
            if (_lyricsMode) _ = LoadLyricsAsync();
        }

        /// <summary>Вызывается при смене трека; грузит текст, только если панель открыта.</summary>
        private void OnLyricsTrackChanged()
        {
            if (_lyricsMode) _ = LoadLyricsAsync();
            else _lyricsTrackId = null;
        }

        private async Task LoadLyricsAsync()
        {
            var track = _queue.Current;
            var api = _api;
            if (track == null || api == null)
            {
                ShowLyricsStatus("Сейчас ничего не играет", spinner: false);
                return;
            }
            if (_lyricsTrackId == track.Id) return;
            _lyricsTrackId = track.Id;
            var gen = ++_lyricsGen;

            _lyrics = new List<LyricLine>();
            _lyricsActive = -1;
            LyricsList.ItemsSource = null;
            ShowLyricsStatus("Загружаю текст…", spinner: true);

            (bool Synced, string Text)? found;
            if (!_lyricsCache.TryGetValue(track.Id, out found))
            {
                found = null;
                try
                {
                    // сначала с таймкодами; если нет — обычный текст
                    var lrc = track.HasSyncLyrics || !track.HasLyrics ? await api.GetLyricsAsync(track.Id, sync: true) : null;
                    if (lrc != null) found = (true, lrc);
                    else if (await api.GetLyricsAsync(track.Id, sync: false) is { } plain) found = (false, plain);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Текст {track.Id} не загрузился", ex);
                    if (gen == _lyricsGen) ShowLyricsStatus("Не удалось загрузить текст: " + Short(ex.Message), spinner: false);
                    _lyricsTrackId = null; // попробуем снова при следующем открытии
                    return;
                }
                _lyricsCache[track.Id] = found;
            }
            if (gen != _lyricsGen) return;

            if (found is not { } lyrics)
            {
                ShowLyricsStatus("У этого трека нет текста в Яндекс Музыке", spinner: false);
                return;
            }

            _lyrics = lyrics.Synced ? LyricsParser.ParseLrc(lyrics.Text) : LyricsParser.ParsePlain(lyrics.Text);
            if (_lyrics.Count == 0) _lyrics = LyricsParser.ParsePlain(lyrics.Text);
            LyricsList.ItemsSource = new ObservableCollection<LyricLine>(_lyrics);
            LyricsStatus.Visibility = Visibility.Collapsed;
            LyricsList.Visibility = Visibility.Visible;
            UpdateLyricsPosition(scroll: true);
        }

        private void ShowLyricsStatus(string text, bool spinner)
        {
            LyricsList.Visibility = Visibility.Collapsed;
            LyricsStatus.Visibility = Visibility.Visible;
            LyricsSpinner.Visibility = spinner ? Visibility.Visible : Visibility.Collapsed;
            LyricsStatusText.Text = text;
        }

        /// <summary>Подсветка текущей строки (из таймера прогресса).</summary>
        private void UpdateLyricsPosition(bool scroll = false)
        {
            if (!_lyricsMode || _lyrics.Count == 0 || _lyrics[0].IsPlain) return;
            var position = _hasMedia ? _player.Position : _resumeAt?.Position ?? TimeSpan.Zero;
            // строку подсвечиваем чуть заранее — так глаз успевает за голосом
            var index = LyricsParser.ActiveIndex(_lyrics, position + TimeSpan.FromMilliseconds(250));
            if (index == _lyricsActive && !scroll) return;

            for (int i = 0; i < _lyrics.Count; i++)
            {
                _lyrics[i].IsActive = i == index;
                _lyrics[i].IsPast = i < index;
            }
            _lyricsActive = index;
            if (index >= 0) CenterLyric(_lyrics[index]);
        }

        /// <summary>Прокручивает так, чтобы текущая строка была примерно посередине (а не у края, как ScrollIntoView).</summary>
        private void CenterLyric(LyricLine line)
        {
            var viewer = FindChild<ScrollViewer>(LyricsList);
            if (viewer == null || LyricsList.ItemContainerGenerator.ContainerFromItem(line) is not FrameworkElement container)
            {
                LyricsList.ScrollIntoView(line);
                return;
            }
            try
            {
                var top = container.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
                var target = viewer.VerticalOffset + top - (viewer.ViewportHeight - container.ActualHeight) * 0.4;
                viewer.ScrollToVerticalOffset(Math.Max(0, target));
            }
            catch (InvalidOperationException)
            {
                LyricsList.ScrollIntoView(line);
            }
        }

        private void LyricLine_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem { DataContext: LyricLine { Time: { } time } }) return;
            if (_hasMedia)
            {
                _player.Position = time;
                UpdateProgress();
                PublishNowPlaying(force: true);
            }
            else if (_resumeAt is { } r)
            {
                _resumeAt = (r.TrackId, time);
            }
            UpdateLyricsPosition(scroll: true);
        }

        private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                if (FindChild<T>(child) is { } deeper) return deeper;
            }
            return null;
        }
    }
}
