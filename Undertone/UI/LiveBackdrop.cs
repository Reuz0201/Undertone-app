using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Undertone.UI
{
    /// <summary>
    /// «Живой фон»: несколько мягких пятен цветов обложки медленно плывут и дышат.
    /// Пятна — радиальные градиенты без BlurEffect, поэтому анимация почти ничего не стоит видеокарте.
    /// </summary>
    public sealed class LiveBackdrop : Grid
    {
        private const int Blobs = 4;
        private readonly Ellipse[] _blobs = new Ellipse[Blobs];
        private readonly GradientStop[] _inner = new GradientStop[Blobs];
        private readonly GradientStop[] _mid = new GradientStop[Blobs];
        private readonly TranslateTransform[] _move = new TranslateTransform[Blobs];
        private readonly ScaleTransform[] _scale = new ScaleTransform[Blobs];
        private bool _running;

        // у каждого пятна свои период и направление — рисунок не повторяется
        private static readonly (double X, double Y, double Dx, double Dy, double Secs)[] Paths =
        {
            (0.18, 0.20, 0.22, 0.14, 23),
            (0.78, 0.18, -0.20, 0.18, 31),
            (0.30, 0.78, 0.18, -0.16, 37),
            (0.82, 0.72, -0.16, -0.20, 29),
        };

        public LiveBackdrop()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
            for (int i = 0; i < Blobs; i++)
            {
                // середина держит цвет, к краю — в ноль: мягкое пятно без размытия
                _inner[i] = new GradientStop(Colors.Transparent, 0);
                _mid[i] = new GradientStop(Colors.Transparent, 0.5);
                var brush = new RadialGradientBrush();
                brush.GradientStops.Add(_inner[i]);
                brush.GradientStops.Add(_mid[i]);
                brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                _move[i] = new TranslateTransform();
                _scale[i] = new ScaleTransform(1, 1);
                var group = new TransformGroup();
                group.Children.Add(_scale[i]);
                group.Children.Add(_move[i]);
                _blobs[i] = new Ellipse
                {
                    Fill = brush,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = group,
                };
                Children.Add(_blobs[i]);
            }
            SizeChanged += (_, _) => Layout();
            IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
        }

        /// <summary>Новые цвета (обычно 2–3 от обложки) — плавный переход за секунду.</summary>
        public void SetColors(Color[] colors)
        {
            if (colors.Length == 0) return;
            var dur = new Duration(TimeSpan.FromMilliseconds(1100));
            for (int i = 0; i < Blobs; i++)
            {
                var c = colors[i % colors.Length];
                // ярче в центре, полупрозрачная середина
                var inner = Color.FromArgb(0xB0, c.R, c.G, c.B);
                var mid = Color.FromArgb(0x48, c.R, c.G, c.B);
                _inner[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(inner, dur));
                _mid[i].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(mid, dur));
            }
        }

        private void Layout()
        {
            var w = ActualWidth;
            var h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var size = Math.Max(w, h) * 0.75;
            for (int i = 0; i < Blobs; i++)
            {
                _blobs[i].Width = _blobs[i].Height = size;
                _blobs[i].Margin = new Thickness(Paths[i].X * w - size / 2, Paths[i].Y * h - size / 2, 0, 0);
            }
            if (_running) { Stop(); Start(); }
        }

        public void Start()
        {
            if (_running || ActualWidth <= 0 || !IsVisible) return;
            _running = true;
            var w = ActualWidth;
            var h = ActualHeight;
            for (int i = 0; i < Blobs; i++)
            {
                var p = Paths[i];
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                var period = TimeSpan.FromSeconds(p.Secs);
                _move[i].BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, p.Dx * w, period)
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
                _move[i].BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, p.Dy * h, period * 0.83)
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
                var breathe = new DoubleAnimation(0.85, 1.2, period * 0.61)
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
                _scale[i].BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
                _scale[i].BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
            }
        }

        public void Stop()
        {
            _running = false;
            for (int i = 0; i < Blobs; i++)
            {
                _move[i].BeginAnimation(TranslateTransform.XProperty, null);
                _move[i].BeginAnimation(TranslateTransform.YProperty, null);
                _scale[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _scale[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
        }
    }
}
