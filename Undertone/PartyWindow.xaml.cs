using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// Режим вечеринки: полный экран для телевизора или второго монитора — огромная обложка, живой фон,
    /// текст караоке-строками и QR со ссылкой на трек. Управляется из главного окна.
    /// </summary>
    public partial class PartyWindow : Window
    {
        private IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
        private int _activeLine = -2;
        private string? _qrFor;
        private readonly DispatcherTimer _hideCursor = new() { Interval = TimeSpan.FromSeconds(2.5) };

        public PartyWindow()
        {
            InitializeComponent();
            // курсор прячется, если мышь не двигается, — на телевизоре он мешает
            _hideCursor.Tick += (_, _) => { _hideCursor.Stop(); Cursor = Cursors.None; };
            _hideCursor.Start();
            Loaded += (_, _) => Live.Start();
        }

        public void SetTrack(YaTrack track, string source)
        {
            TitleText.Text = track.FullTitle;
            ArtistText.Text = track.ArtistsText;
            SourceText.Text = string.IsNullOrEmpty(source) ? "СЕЙЧАС ИГРАЕТ" : source;
            CoverBrush.ImageSource = Covers.Load(track.CoverUrl(1000), 1000);
            Backdrop.Source = Covers.Load(track.CoverUrl(200), 200);
            // включён пульт — QR для гостей «предложи трек», иначе ссылка на сам трек
            if (_guestLink != null) SetQr(_guestLink);
            else if (track.WebUrl is { } url)
                SetQr(url);
            SetLyrics(Array.Empty<LyricLine>());
        }

        public void SetPalette(Color[] colors, Color accent)
        {
            Live.SetColors(colors);
            Glow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(accent, TimeSpan.FromMilliseconds(900)));
        }

        public void SetLyrics(IReadOnlyList<LyricLine> lines)
        {
            _lyrics = lines;
            _activeLine = -2; // перерисуем строки на следующем тике — в том числе с переводом
            var synced = lines.Count > 0 && !lines[0].IsPlain;
            Karaoke.Visibility = synced ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetPlayback(TimeSpan position, TimeSpan duration)
        {
            var d = Math.Max(1, duration.TotalSeconds);
            ProgressFill.Width = 820 * Math.Clamp(position.TotalSeconds / d, 0, 1);
            PosText.Text = Format.Time(position);
            DurText.Text = Format.Time(duration);
            UpdateKaraoke(position);
        }

        private void UpdateKaraoke(TimeSpan position)
        {
            if (_lyrics.Count == 0 || _lyrics[0].IsPlain) return;
            var i = LyricsParser.ActiveIndex(_lyrics, position + TimeSpan.FromMilliseconds(250));
            if (i == _activeLine) return;
            _activeLine = i;
            string Line(int k) => k >= 0 && k < _lyrics.Count ? _lyrics[k].Text : "";
            LinePrev.Text = Line(i - 1);
            LineNow.Text = i < 0 ? "♪" : Line(i);
            LineNowTr.Text = i >= 0 && i < _lyrics.Count ? _lyrics[i].Translation : "";
            LineNext.Text = Line(i + 1);
            LineNext2.Text = Line(i + 2);
            // новая строка чуть «вплывает»
            var tt = new TranslateTransform();
            LineNow.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            LineNow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(260)));
        }

        public void SetRoom(string text) => RoomText.Text = text;

        private string? _guestLink;

        /// <summary>Гостевая ссылка пульта: на экране вечеринки QR зовёт предлагать треки.</summary>
        public void SetGuestLink(string? url)
        {
            _guestLink = url;
            QrTitle.Text = url != null ? "Хочешь свой трек?" : "Что за трек?";
            QrHint.Text = url != null ? "Сканируй и предложи — с телефона" : "Наведи камеру телефона";
            if (url != null) SetQr(url);
        }

        private void SetQr(string url)
        {
            if (url == _qrFor) return;
            _qrFor = url;
            try
            {
                QrImage.Source = Qr.Make(url);
            }
            catch (Exception ex)
            {
                Log.Warn("QR не нарисовался", ex);
                QrImage.Source = null;
            }
        }

        /// <summary>Реакция из комнаты — всплывает над обложкой и здесь.</summary>
        public void ShowReaction(ImageSource image, string name)
        {
            var bubble = new StackPanel { IsHitTestVisible = false };
            bubble.Children.Add(new Image { Source = image, Width = 90, Height = 90 });
            if (name.Length > 0)
                bubble.Children.Add(new TextBlock
                {
                    Text = name, FontSize = 20, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0),
                });
            var move = new TranslateTransform();
            bubble.RenderTransform = move;
            Canvas.SetLeft(bubble, Random.Shared.NextDouble() * 440 + 10);
            Canvas.SetTop(bubble, 440);
            ReactionLayer.Children.Add(bubble);
            var dur = TimeSpan.FromMilliseconds(2800);
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -460, dur)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var fade = new DoubleAnimationUsingKeyFrames { Duration = dur };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.6)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            fade.Completed += (_, _) => ReactionLayer.Children.Remove(bubble);
            bubble.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>Пробел и стрелки управляют музыкой и отсюда — главное окно делает остальное.</summary>
        public event Action<Key>? KeyPressed;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Escape or Key.F11)
            {
                e.Handled = true;
                Close();
                return;
            }
            KeyPressed?.Invoke(e.Key);
        }

        private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Close();

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            Cursor = null;
            _hideCursor.Stop();
            _hideCursor.Start();
        }
    }
}
