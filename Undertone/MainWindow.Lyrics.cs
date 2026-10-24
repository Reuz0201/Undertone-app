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
            // текст встаёт на место полок, очередь справа остаётся
            ShelvesScroll.Visibility = _lyricsMode ? Visibility.Collapsed : Visibility.Visible;
            LyricsPanel.Visibility = _lyricsMode ? Visibility.Visible : Visibility.Collapsed;
            NowLyricsBtn.ToolTip = _lyricsMode ? "Скрыть текст (T)" : "Текст песни (T)";
            if (_lyricsMode) NowLyricsGlyph.SetResourceReference(TextBlock.ForegroundProperty, "LiveAccent");
            else NowLyricsGlyph.ClearValue(TextBlock.ForegroundProperty);
            NowLyricsGlyph.Text = _lyricsMode ? "" : "";
            if (_lyricsMode) _ = LoadLyricsAsync();
        }

        /// <summary>Вызывается при смене трека; грузит текст, только если панель открыта.</summary>
        private void OnLyricsTrackChanged()
        {
            // текст нужен открытой панели или караоке в режиме вечеринки
            if (_lyricsMode || _party != null) _ = LoadLyricsAsync();
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
            if (IsLocalTrack(track))
            {
                _lyricsTrackId = track.Id;
                _lyrics = new List<LyricLine>();
                _party?.SetLyrics(_lyrics);
                ShowLyricsStatus("У своих файлов текста нет — он бывает только у треков Яндекса", spinner: false);
                return;
            }
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
            TranslateBtn.Visibility = Visibility.Visible;
            UpdateTranslateButton();
            if (_settings.TranslateLyrics) _ = TranslateLyricsAsync(track.Id, _lyrics, quiet: true);
            _party?.SetLyrics(_lyrics);
            LyricsStatus.Visibility = Visibility.Collapsed;
            LyricsList.Visibility = Visibility.Visible;
            UpdateLyricsPosition(scroll: true);
        }

        // ────────── перевод ──────────

        private readonly Dictionary<string, Translator.Result?> _translations = new();

        private async void Translate_Click(object sender, RoutedEventArgs e)
        {
            _settings.TranslateLyrics = !_settings.TranslateLyrics;
            PersistSettings();
            UpdateTranslateButton();
            if (_settings.TranslateLyrics && _queue.Current is { } t && _lyrics.Count > 0)
                await TranslateLyricsAsync(t.Id, _lyrics, quiet: false);
            else
                foreach (var l in _lyrics) l.Translation = "";
            _party?.SetLyrics(_lyrics);
        }

        private void UpdateTranslateButton()
        {
            TranslateText.Text = _settings.TranslateLyrics ? "Перевод вкл." : "Перевод";
            if (_settings.TranslateLyrics) TranslateGlyph.SetResourceReference(TextBlock.ForegroundProperty, "LiveAccent");
            else TranslateGlyph.ClearValue(TextBlock.ForegroundProperty);
        }

        /// <summary>Переводит строки (кроме «♪» и пустых) и подписывает их под оригиналом. Русский текст не трогаем.</summary>
        private async Task TranslateLyricsAsync(string trackId, List<LyricLine> lines, bool quiet)
        {
            var source = lines.Select(l => l.Text == "♪" ? "" : l.Text).ToList();
            if (source.All(s => s.Length == 0)) return;
            if (!_translations.TryGetValue(trackId, out var result))
            {
                try
                {
                    result = await Translator.TranslateAsync(source, "ru");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Перевод текста {trackId} не удался", ex);
                    if (!quiet) ShowToast("Переводчик не ответил — проверь интернет или VPN");
                    return;
                }
                _translations[trackId] = result;
            }
            if (result == null || _lyrics != lines || !_settings.TranslateLyrics) return;
            if (result.SourceLanguage.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            {
                if (!quiet) ShowToast("Песня и так на русском");
                return;
            }
            for (int i = 0; i < lines.Count; i++)
            {
                var tr = result.Lines[i];
                // перевод, совпадающий с оригиналом (имена, «yeah»), не дублируем
                lines[i].Translation = source[i].Length == 0 || string.Equals(tr, source[i], StringComparison.OrdinalIgnoreCase) ? "" : tr;
            }
            _party?.SetLyrics(lines);
        }

        private void ShowLyricsStatus(string text, bool spinner)
        {
            TranslateBtn.Visibility = Visibility.Collapsed;
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
                RoomPublishLocal();
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
