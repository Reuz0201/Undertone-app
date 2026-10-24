using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// Блок «Сейчас играет»: крупный плеер под треком, акцент цвета обложки, полки «Похожие» и «Недавно слушали»,
    /// очередь отдельной колонкой справа.
    /// </summary>
    public partial class MainWindow
    {
        // кисти акцента — окно перекрывает ими нейтральные из App.xaml и плавно перекрашивает под каждую обложку
        private readonly SolidColorBrush _liveAccent = new(CoverPalette.Neutral);
        private readonly SolidColorBrush _liveAccentSoft = new(WithAlpha(CoverPalette.Neutral, 0x29));
        private Color _accentShown = CoverPalette.Neutral;
        private string? _accentFor;

        private ObservableCollection<TrackItem> _similarItems = new();
        private ObservableCollection<TrackItem> _recentItems = new();
        private readonly Dictionary<string, List<YaTrack>> _similarCache = new();
        private int _shelvesGen;
        private string? _shelvesFor;

        private void InitNowPlaying()
        {
            Resources["LiveAccent"] = _liveAccent;
            Resources["LiveAccentSoft"] = _liveAccentSoft;

            // крупный ползунок — тот же прогресс, что и в нижней панели
            HeroSlider.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(Slider.Maximum)) { Source = ProgressSlider });
            HeroSlider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Slider.Value)) { Source = ProgressSlider, Mode = BindingMode.TwoWay });
            HeroSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _seeking = true), true);
            HeroSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => CommitSeek()), true);
            HeroSlider.LostMouseCapture += (_, _) => { if (_seeking) CommitSeek(); };

            ProgressSlider.ValueChanged += (_, _) => UpdateHeroTime();
            DependencyPropertyDescriptor.FromProperty(RangeBase.MaximumProperty, typeof(Slider))
                .AddValueChanged(ProgressSlider, (_, _) => UpdateHeroTime());

            // размытая обложка за шапкой артиста/альбома/плейлиста — та же картинка, что в шапке
            DependencyPropertyDescriptor.FromProperty(ImageBrush.ImageSourceProperty, typeof(ImageBrush))
                .AddValueChanged(CollCoverBrush, (_, _) => CollBackdropImage.Source = CollCoverBrush.ImageSource);

            SimilarList.ItemsSource = _similarItems;
            RecentList.ItemsSource = _recentItems;
            UpdateHeroTime();
        }

        private void UpdateHeroTime()
        {
            var pos = TimeSpan.FromSeconds(ProgressSlider.Value);
            var total = TimeSpan.FromSeconds(ProgressSlider.Maximum);
            HeroPos.Text = Format.Time(pos);
            var left = total - pos;
            HeroRemain.Text = "−" + Format.Time(left < TimeSpan.Zero ? TimeSpan.Zero : left);
        }

        /// <summary>Вызывается из ShowTrack: акцент, полки.</summary>
        private void OnNowPlayingTrack(YaTrack track)
        {
            UpdateAccent(track);
            _ = LoadShelvesAsync(track);
        }

        // ────────── акцент от обложки ──────────

        private void UpdateAccent(YaTrack track)
        {
            var url = track.CoverUrl(200);
            if (url == _accentFor) return;
            _accentFor = url;
            if (Covers.Load(url, 200) is not BitmapSource image)
            {
                AnimateAccent(CoverPalette.Neutral);
                return;
            }
            if (image is BitmapImage { IsDownloading: true } downloading)
            {
                downloading.DownloadCompleted += (_, _) => { if (_accentFor == url) AnimateAccent(CoverPalette.FromImage(downloading) ?? CoverPalette.Neutral); };
                downloading.DownloadFailed += (_, _) => { if (_accentFor == url) AnimateAccent(CoverPalette.Neutral); };
                return;
            }
            AnimateAccent(CoverPalette.FromImage(image) ?? CoverPalette.Neutral);
        }

        private void AnimateAccent(Color color)
        {
            if (color == _accentShown) return;
            _accentShown = color;
            var duration = new Duration(TimeSpan.FromMilliseconds(750));
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            _liveAccent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, duration) { EasingFunction = ease });
            _liveAccentSoft.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(WithAlpha(color, 0x29), duration) { EasingFunction = ease });
            HeroGlow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(color, duration) { EasingFunction = ease });
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        // ────────── полки ──────────

        private async Task LoadShelvesAsync(YaTrack track)
        {
            FillRecentShelf();
            if (track.Id == _shelvesFor) return;
            _shelvesFor = track.Id;
            var gen = ++_shelvesGen;

            if (!_similarCache.TryGetValue(track.Id, out var similar))
            {
                var api = _api;
                if (api == null) return;
                // не дёргаем Яндекс, пока человек листает треки подряд
                await Task.Delay(600);
                if (gen != _shelvesGen) return;
                try
                {
                    similar = await api.GetSimilarTracksAsync(track.Id);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Похожие на {track.Id} не загрузились", ex);
                    similar = new List<YaTrack>();
                }
                if (_similarCache.Count > 60) _similarCache.Clear();
                _similarCache[track.Id] = similar;
                if (gen != _shelvesGen) return;
            }

            var current = _queue.Current?.Id;
            _similarItems = new ObservableCollection<TrackItem>(similar.Where(t => t.Available).Take(20).Select((t, i) =>
                new TrackItem(t, i + 1) { IsLiked = _likedIds.Contains(t.Id), IsPlaying = t.Id == current }));
            SimilarList.ItemsSource = _similarItems;
            SimilarShelf.Visibility = _similarItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ScrollShelfTo(SimilarList, 0, animate: false);
        }

        /// <summary>«Недавно слушали» — из истории, без текущего трека и повторов.</summary>
        private void FillRecentShelf()
        {
            var current = _queue.Current?.Id;
            var tracks = _history.Select(h => h.Track).Where(t => t.Id != current).Take(20).ToList();
            if (tracks.Select(t => t.Id).SequenceEqual(_recentItems.Select(i => i.Track.Id))) return;
            _recentItems = new ObservableCollection<TrackItem>(tracks.Select((t, i) =>
                new TrackItem(t, i + 1) { IsLiked = _likedIds.Contains(t.Id) }));
            RecentList.ItemsSource = _recentItems;
            RecentShelf.Visibility = _recentItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task ActivateShelfAsync(ItemsControl list, TrackItem item)
        {
            if (list == SimilarList)
            {
                var from = _queue.Current?.FullTitle;
                await PlayFromListAsync(_similarItems, _similarItems.IndexOf(item), QueueSource.Collection,
                    from != null ? $"ПОХОЖЕЕ НА «{from.ToUpperInvariant()}»" : "ПОХОЖИЕ ТРЕКИ");
            }
            else if (list == RecentList)
                await PlayFromListAsync(_recentItems, _recentItems.IndexOf(item), QueueSource.Collection, "НЕДАВНО СЛУШАЛИ");
        }

        private void ShelfPrev_Click(object sender, RoutedEventArgs e) => PageShelf(sender, -1);
        private void ShelfNext_Click(object sender, RoutedEventArgs e) => PageShelf(sender, +1);

        private void PageShelf(object sender, int direction)
        {
            if ((sender as FrameworkElement)?.Tag is not ListBox list || FindChild<ScrollViewer>(list) is not { } sv) return;
            var step = Math.Max(170, sv.ViewportWidth * 0.8);
            ScrollShelfTo(list, sv.HorizontalOffset + direction * step, animate: true);
        }

        private void ScrollShelfTo(ListBox list, double target, bool animate)
        {
            if (FindChild<ScrollViewer>(list) is not { } sv) return;
            target = Math.Clamp(target, 0, sv.ScrollableWidth);
            if (!animate)
            {
                sv.ScrollToHorizontalOffset(target);
                return;
            }
            // ScrollViewer не анимируется сам — ведём смещение по кадрам с плавным торможением
            var start = sv.HorizontalOffset;
            var began = DateTime.UtcNow;
            const double ms = 320;
            EventHandler? tick = null;
            tick = (_, _) =>
            {
                var t = Math.Min(1, (DateTime.UtcNow - began).TotalMilliseconds / ms);
                var eased = 1 - Math.Pow(1 - t, 3);
                sv.ScrollToHorizontalOffset(start + (target - start) * eased);
                if (t >= 1) CompositionTarget.Rendering -= tick;
            };
            CompositionTarget.Rendering += tick;
        }

        /// <summary>Колесо над полкой листает страницу вниз, а с Shift — саму полку вбок.</summary>
        private void Shelf_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && sender is ListBox list && FindChild<ScrollViewer>(list) is { } sv)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
                return;
            }
            ShelvesScroll.ScrollToVerticalOffset(ShelvesScroll.VerticalOffset - e.Delta);
        }

        // ────────── раскладка под размер окна ──────────

        private void PageNow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var w = e.NewSize.Width;
            // очередь справа — когда хватает места; в узком окне она уходит, «Далее» остаётся в меню трека
            var queue = w >= 1320 ? 340 : w >= 860 ? 300 : 0;
            QueueColumn.Width = new GridLength(queue);
            var left = w - queue;
            var cover = left >= 820 ? 300 : left >= 660 ? 248 : 208;
            HeroCover.Width = HeroCover.Height = cover;
            NowTitle.FontSize = left >= 820 ? 42 : left >= 660 ? 36 : 30;
            NowTitle.LineHeight = NowTitle.FontSize * 1.22;
            NowTitle.MaxHeight = NowTitle.LineHeight * 2 + 4;
        }
    }
}
