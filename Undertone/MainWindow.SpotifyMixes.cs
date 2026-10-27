using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// Миксы Spotify на странице «Подборки»: ежедневный, открытия, топ месяца, недавнее, по жанрам и артистам.
    /// Собирает их SpotifyApi (см. SpotifyApi.Radio) — у Spotify для сторонних приложений своих рекомендаций нет.
    /// </summary>
    public partial class MainWindow
    {
        private int _spMixGen;

        private async Task RebuildSpotifyMixesAsync()
        {
            if (_api is not SpotifyApi spotify)
            {
                SpotifyMixSection.Visibility = Visibility.Collapsed;
                return;
            }
            SpotifyMixSection.Visibility = Visibility.Visible;
            var gen = ++_spMixGen;
            if (SpotifyMixList.ItemsSource == null) SpotifyMixHint.Text = "Собираю миксы по твоей истории в Spotify…";
            try
            {
                var mixes = await spotify.GetMixesAsync();
                if (gen != _spMixGen || _api != spotify) return;
                SpotifyMixList.ItemsSource = mixes.Select((m, i) => new SpotifyMixItem(m, i)).ToList();
                SpotifyMixHint.Text = spotify.MissingHistoryScopes
                    ? "Чтобы миксы учитывали твой топ и историю, перезайди в Spotify: меню профиля → «Выйти из Spotify» → войти снова. Пока они собраны по лайкам."
                    : "Из твоего топа, истории и лайков в Spotify — знакомое вперемешку с новым. Обновляются каждый день.";
            }
            catch (Exception ex)
            {
                if (gen != _spMixGen) return;
                Log.Warn("Миксы Spotify не собрались", ex);
                SpotifyMixHint.Text = "Миксы не собрались: " + Short(ex.Message);
            }
        }

        private async void SpotifyMix_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: SpotifyMixItem item })
                await OpenCollectionAsync(() => LoadSpotifyMixViewAsync(item.Mix));
        }

        private async Task LoadSpotifyMixViewAsync(SpotifyMix mix)
        {
            if (_api is not SpotifyApi spotify) return;
            var gen = ++_collGen;
            ResetCollection(CollectionKind.Mix, "МИКС SPOTIFY", mix.Title, round: false);
            _collKey = "spmix:" + mix.Id;
            _collLabel = "МИКС · " + mix.Title.ToUpperInvariant();
            CollCoverGlyph.Text = mix.Glyph;
            try
            {
                var tracks = await spotify.GetMixTracksAsync(mix.Id);
                if (gen != _collGen) return;
                var total = TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs));
                CollSub.Text = $"{mix.Sub} · {tracks.Count} {Format.Plural(tracks.Count, "трек", "трека", "треков")}" +
                               (tracks.Count > 0 ? $" · {Format.Total(total)}" : "");
                CollCoverBrush.ImageSource = Covers.Load(tracks.FirstOrDefault()?.CoverUrl(400), 320);
                FillTracks(tracks, "Пока пусто — послушай побольше в Spotify, и микс наполнится");
            }
            catch (Exception ex)
            {
                if (gen == _collGen) CollFailed(ex);
            }
        }
    }
}
