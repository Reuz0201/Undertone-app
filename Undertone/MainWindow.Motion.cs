using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Подключение анимаций (<see cref="Motion"/>) и фоновой визуализации (<see cref="AmbientViz"/>) к элементам окна.</summary>
    public partial class MainWindow
    {
        /// <summary>Какой трек уже показан — анимация смены только при настоящей смене, не при обновлении экрана.</summary>
        private string? _motionTrackId;
        /// <summary>Лайк текущего трека в прошлый раз — сердечко прыгает, только когда лайк поменялся.</summary>
        private (string? Id, bool Liked) _motionLike;
        /// <summary>Куда листнули: +1 — «следующий», −1 — «предыдущий», 0 — трек выбран сам.</summary>
        private int _motionDirection;
        /// <summary>Номер выбранного пункта меню — чтобы страница въезжала с нужной стороны.</summary>
        private int _navIndex;
        /// <summary>Очередь «Далее» в прошлый раз (id по порядку) — чтобы анимировать только то, что изменилось.</summary>
        private IReadOnlyList<string> _motionQueue = Array.Empty<string>();
        /// <summary>Строка текста, которая сейчас выделена крупнее.</summary>
        private FrameworkElement? _motionLyric;

        private void InitMotion()
        {
            Motion.Enabled = _settings.Animations;

            Motion.PageOnShow(PageNow, SimilarList, RecentList);
            Motion.PageOnShow(PageLiked, LikedList);
            Motion.PageOnShow(PageSearch, SearchCards, SearchList);
            Motion.PageOnShow(PageCollection, CollAlbums, CollList);
            Motion.PageOnShow(PageHistory, HistoryList);
            Motion.PageOnShow(PageSmart, SpotifyMixList, SmartList);
            Motion.PageOnShow(PageLocal, LocalList);
            Motion.PageOnShow(PageFriends, FriendsList);
            Motion.PageOnShow(PageSettings);

            Motion.HoverLift(SimilarList);
            Motion.HoverLift(RecentList);
            Motion.HoverLift(CollAlbums);
            Motion.HoverLift(SearchCards, scale: 1.02);

            Motion.OverlayOnShow(DialogOverlay);
            Motion.OverlayOnShow(UpdateOverlay);
            Motion.OverlayOnShow(QuickOverlay);

            Motion.Pressable(PlayBtn, 0.86);
            Motion.Pressable(HeroPlayBtn, 0.88);
            Motion.Pressable(NowLike);
            Motion.Pressable(BarLike);

            // подсветка меню: встаёт на место сразу после разметки и при изменении размеров
            NavHost.Loaded += (_, _) => Dispatcher.InvokeAsync(() => SyncNavIndicator(animate: false), DispatcherPriority.Loaded);
            NavHost.SizeChanged += (_, _) => SyncNavIndicator(animate: false);
        }

        // ────────── меню ──────────

        private RadioButton? CheckedNav() =>
            NavStack.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);

        /// <summary>Перед сменой страницы: с какой стороны ей въезжать и куда ехать подсветке.</summary>
        private void OnNavMotion(RadioButton nav)
        {
            var index = NavStack.Children.IndexOf(nav);
            Motion.PageDirection = Math.Sign(index - _navIndex);
            _navIndex = index;
            if (nav.Template?.FindName("Glyph", nav) is UIElement glyph) Motion.Bounce(glyph, 1.25, 460);
        }

        private void SyncNavIndicator(bool animate = true)
        {
            var nav = CheckedNav();
            if (!animate)
            {
                var was = Motion.Enabled;
                Motion.Enabled = false;
                Motion.MoveIndicator(NavPill, nav, NavHost);
                Motion.Enabled = was;
                return;
            }
            Motion.MoveIndicator(NavPill, nav, NavHost);
        }

        // ────────── трек ──────────

        private void AnimateTrackChange(string trackId)
        {
            var direction = _motionDirection;
            _motionDirection = 0;
            if (trackId == _motionTrackId) return;
            var first = _motionTrackId == null;
            _motionTrackId = trackId;
            if (first) return; // первый трек после запуска — без анимации, окно и так только появилось
            Motion.TrackChanged(HeroCover, BarCover, direction, NowTitle, NowArtists, NowAlbum);
        }

        private void AnimateLike(string? trackId, bool liked)
        {
            var prev = _motionLike;
            _motionLike = (trackId, liked);
            if (trackId == null || prev.Id != trackId || prev.Liked == liked) return;
            var peak = liked ? 1.35 : 0.8;
            Motion.Bounce(NowLike, peak);
            Motion.Bounce(BarLike, peak);
            if (!liked) return;
            var red = (RedBrushRes as SolidColorBrush)?.Color ?? Color.FromRgb(0xFF, 0x4D, 0x6A);
            // вспышка у той кнопки, что на виду (на «Сейчас играет» — у большой, иначе — у нижней панели)
            Motion.Burst(FxLayer, NowLike.IsVisible ? NowLike : BarLike, red, _accentShown);
        }

        private void AnimatePlayGlyph(object? before)
        {
            if (Equals(before, PlayBtn.Content)) return;
            Motion.GlyphSwap(PlayBtn);
            Motion.GlyphSwap(HeroPlayBtn);
        }

        // ────────── очередь и текст ──────────

        private void AnimateQueue()
        {
            var now = _upNextItems.Select(i => i.Track.Id).ToList();
            var before = _motionQueue;
            _motionQueue = now;
            Motion.QueueChanged(UpNextList, before, now);
        }

        private void AnimateLyric(LyricLine line)
        {
            if (LyricsList.ItemContainerGenerator.ContainerFromItem(line) is not FrameworkElement row) return;
            if (row == _motionLyric) return;
            if (_motionLyric != null) Motion.Emphasize(_motionLyric, false);
            Motion.Emphasize(row, true);
            _motionLyric = row;
        }

        private void ResetLyricMotion()
        {
            if (_motionLyric != null) Motion.Emphasize(_motionLyric, false);
            _motionLyric = null;
        }

        // ────────── фон «Сейчас играет» ──────────

        private void InitAmbient()
        {
            Ambient.Engine = _player;
            Ambient.IsActive = () => _isPlaying;
            Ambient.FocusElement = HeroCover;
            Ambient.SetColors(_livePalette);
            Ambient.Mode = (AmbientMode)Math.Clamp(_settings.AmbientMode, 0, 3);
        }

        private void Ambient_Checked(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings || sender is not RadioButton pill) return;
            _settings.AmbientMode = Math.Clamp(TagValue(pill), 0, 3);
            PersistSettings();
            Ambient.Mode = (AmbientMode)_settings.AmbientMode;
        }
    }
}
