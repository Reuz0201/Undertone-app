using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Маленькое окно поверх всех: обложка, название, кнопки. Получает состояние через INowPlayingSink.</summary>
    public partial class MiniPlayer : Window, INowPlayingSink
    {
        private static readonly Brush Dim = (Brush)Application.Current.Resources["TextDimBrush"];
        private static readonly Brush Red = (Brush)Application.Current.Resources["RedBrush"];

        private readonly AppSettings _settings;
        private string? _trackId;

        public event Action? PlayPauseRequested;
        public event Action? NextRequested;
        public event Action? PrevRequested;
        public event Action? LikeRequested;
        public event Action? ExpandRequested;

        public MiniPlayer(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            PlaceOnScreen();
        }

        /// <summary>Где был в прошлый раз; если тот монитор отключили — в правый нижний угол рабочей области.</summary>
        private void PlaceOnScreen()
        {
            var area = SystemParameters.WorkArea;
            var left = _settings.MiniLeft ?? area.Right - Width - 16;
            var top = _settings.MiniTop ?? area.Bottom - Height - 16;
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var onScreen = left >= virtualLeft - Width / 2 && left <= virtualLeft + SystemParameters.VirtualScreenWidth - Width / 2 &&
                           top >= virtualTop && top <= virtualTop + SystemParameters.VirtualScreenHeight - Height / 2;
            if (!onScreen)
            {
                left = area.Right - Width - 16;
                top = area.Bottom - Height - 16;
            }
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        public void Update(NowPlayingState state, bool positionOnly)
        {
            var duration = state.Duration.TotalSeconds;
            var ratio = duration > 0 ? Math.Clamp(state.Position.TotalSeconds / duration, 0, 1) : 0;
            ProgressDone.Width = new GridLength(ratio, GridUnitType.Star);
            ProgressLeft.Width = new GridLength(1 - ratio, GridUnitType.Star);
            if (positionOnly) return;

            PlayBtn.Content = state.IsPlaying ? "" : "";
            LikeBtn.Content = state.IsLiked ? "" : "";
            LikeBtn.Foreground = state.IsLiked ? Red : Dim;
            LikeBtn.IsEnabled = state.Track != null;

            var track = state.Track;
            if (track?.Id == _trackId) return;
            _trackId = track?.Id;
            TitleText.Text = track?.FullTitle ?? "Ничего не играет";
            ArtistText.Text = track?.ArtistsText ?? "";
            CoverBrush.ImageSource = Covers.Load(track?.CoverUrl(200), 140);
            Title = track != null ? $"{track.ArtistsText} — {track.FullTitle}" : "Undertone — мини-плеер";
        }

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // за любое пустое место окно можно перетащить
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        }

        private void Window_LocationChanged(object? sender, EventArgs e)
        {
            _settings.MiniLeft = Left;
            _settings.MiniTop = Top;
        }

        private void Cover_Click(object sender, MouseButtonEventArgs e) => ExpandRequested?.Invoke();
        private void Expand_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Play_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();
        private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
        private void Prev_Click(object sender, RoutedEventArgs e) => PrevRequested?.Invoke();
        private void Like_Click(object sender, RoutedEventArgs e) => LikeRequested?.Invoke();
    }
}
