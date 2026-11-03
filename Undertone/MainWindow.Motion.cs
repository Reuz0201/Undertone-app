using System.Windows;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Подключение анимаций (<see cref="Motion"/>) к элементам окна.</summary>
    public partial class MainWindow
    {
        /// <summary>Какой трек уже показан — анимация смены только при настоящей смене, не при обновлении экрана.</summary>
        private string? _motionTrackId;
        /// <summary>Лайк текущего трека в прошлый раз — сердечко прыгает, только когда лайк поменялся.</summary>
        private (string? Id, bool Liked) _motionLike;

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
            Motion.Pressable(NowLike);
            Motion.Pressable(BarLike);
        }

        private void AnimateTrackChange(string trackId)
        {
            if (trackId == _motionTrackId) return;
            var first = _motionTrackId == null;
            _motionTrackId = trackId;
            if (first) return; // первый трек после запуска — без анимации, окно и так только появилось
            Motion.TrackChanged(HeroCover, BarCover, NowTitle, NowArtists, NowAlbum);
        }

        private void AnimateLike(string? trackId, bool liked)
        {
            var prev = _motionLike;
            _motionLike = (trackId, liked);
            if (trackId == null || prev.Id != trackId || prev.Liked == liked) return;
            var peak = liked ? 1.35 : 0.8;
            Motion.Bounce(NowLike, peak);
            Motion.Bounce(BarLike, peak);
        }
    }
}
