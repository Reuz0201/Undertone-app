using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// «Микс компании»: участники с общим кодом делятся последними лайками (зашифрованно, через ту же ленту),
    /// и у каждого собирается один и тот же на сегодня плейлист — общее + открытия от друзей + немного своего.
    /// </summary>
    public partial class MainWindow
    {
        private const int MixSize = 50;
        private DateTime _likesPublishedAt = DateTime.MinValue;

        /// <summary>Отдаём друзьям свои последние лайки (дубли подряд отбрасываем).</summary>
        private void PublishMyLikes(bool force = false)
        {
            if (_friendsFeed is not { } feed || _likedTracks.Count == 0) return;
            if (!force && DateTime.UtcNow - _likesPublishedAt < TimeSpan.FromSeconds(2)) return;
            _likesPublishedAt = DateTime.UtcNow;
            feed.PublishLikes(FriendsDisplayName, _likedTracks.Select(t => t.LikeId));
        }

        private void UpdateMixCard()
        {
            var feed = _friendsFeed;
            MixCard.Visibility = feed == null ? Visibility.Collapsed : Visibility.Visible;
            if (feed == null) return;
            var friends = feed.FriendsLikes.Where(l => l.Ids.Count > 0).ToList();
            MixPlayBtn.IsEnabled = MixOpenBtn.IsEnabled = friends.Count > 0 && _likedTracks.Count > 0;
            MixText.Text = friends.Count == 0
                ? "Соберётся из лайков всей компании, как только друзья обновятся до 3.7 и включат ленту."
                : $"Общее и открытия от {Names(friends.Select(f => new FriendPresence { Name = f.Name }).ToList())} · новый каждый день";
        }

        private async void MixPlay_Click(object sender, RoutedEventArgs e)
        {
            var tracks = await BuildMixAsync();
            if (tracks == null) return;
            var items = tracks.Select((t, i) => new TrackItem(t, i + 1)).ToList();
            await PlayFromListAsync(items, 0, QueueSource.Collection, "МИКС КОМПАНИИ");
        }

        private async void MixOpen_Click(object sender, RoutedEventArgs e) =>
            await OpenCollectionAsync(LoadMixViewAsync);

        private (int Shared, int FromFriends) _mixStats;

        private async Task<List<YaTrack>?> BuildMixAsync()
        {
            if (_friendsFeed is not { } feed || _api == null) return null;
            var friends = feed.FriendsLikes.Where(l => l.Ids.Count > 0).ToDictionary(l => l.Member, l => l.Ids);
            if (friends.Count == 0)
            {
                ShowToast("Пока нечего смешивать: друзьям нужно обновиться и включить ленту");
                return null;
            }
            var mix = CompanyMix.Build(_likedTracks.Select(t => t.LikeId).ToList(), friends, MixSize, CompanyMix.TodaySeed(DateTime.Now));
            _mixStats = (mix.Shared, mix.FromFriends);
            try
            {
                // свои лайки уже есть целиком — запрашиваем у Яндекса только чужие
                var known = _likedTracks.ToDictionary(t => t.Id, t => t);
                var missing = mix.Ids.Where(id => !known.ContainsKey(id.Split(':')[0])).ToList();
                var fetched = missing.Count > 0 ? await _api.GetTracksAsync(missing) : new List<YaTrack>();
                var all = fetched.ToDictionary(t => t.Id, t => t);
                foreach (var kv in known) all.TryAdd(kv.Key, kv.Value);
                return mix.Ids.Select(id => all.GetValueOrDefault(id.Split(':')[0])).Where(t => t is { Available: true }).Select(t => t!).ToList();
            }
            catch (Exception ex)
            {
                ShowToast("Микс не собрался: " + Short(ex.Message));
                return null;
            }
        }

        private async Task LoadMixViewAsync()
        {
            var gen = ++_collGen;
            ResetCollection(CollectionKind.Mix, "ПЛЕЙЛИСТ КОМПАНИИ", "Микс компании", round: false);
            _collKey = "mix";
            _collLabel = "МИКС КОМПАНИИ";
            var tracks = await BuildMixAsync();
            if (gen != _collGen) return;
            if (tracks == null)
            {
                CollLoading.Visibility = Visibility.Collapsed;
                CollEmpty.Text = "Микс пока не собрать — друзьям нужно включить ленту в версии 3.7";
                CollEmpty.Visibility = Visibility.Visible;
                return;
            }
            var total = TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs));
            CollSub.Text = $"{tracks.Count} {Format.Plural(tracks.Count, "трек", "трека", "треков")} · {Format.Total(total)} · " +
                           $"общих {_mixStats.Shared}, от друзей {_mixStats.FromFriends} · обновится завтра";
            // обложка — первого трека из чужих открытий, чтобы было видно, что это не свой плейлист
            CollCoverBrush.ImageSource = Covers.Load(tracks.FirstOrDefault()?.CoverUrl(400), 320);
            FillTracks(tracks, "Микс пуст");
        }
    }
}
