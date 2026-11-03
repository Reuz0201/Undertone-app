using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Undertone.UI
{
    /// <summary>
    /// Заглушка списка треков на время загрузки: серые строки (обложка и две полоски текста),
    /// по которым проходит блик. Рисует сам и только пока виден — спрятанный ничего не стоит.
    /// </summary>
    public sealed class Skeleton : FrameworkElement
    {
        public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
            nameof(Rows), typeof(int), typeof(Skeleton),
            new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public int Rows
        {
            get => (int)GetValue(RowsProperty);
            set => SetValue(RowsProperty, value);
        }

        private const double RowHeight = 58;
        private static readonly Brush Base = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _hooked;

        public Skeleton()
        {
            IsHitTestVisible = false;
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible && !_hooked) { _hooked = true; CompositionTarget.Rendering += OnFrame; }
                else if (!IsVisible && _hooked) { _hooked = false; CompositionTarget.Rendering -= OnFrame; }
            };
        }

        private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 400 : available.Width, Rows * RowHeight);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            if (w < 40) return;

            // блик: светлая полоса едет слева направо раз в 1.6 с; строки ниже — с небольшим отставанием
            double t = _clock.Elapsed.TotalSeconds;
            for (int r = 0; r < Rows; r++)
            {
                double y = r * RowHeight + 6;
                // дальние строки бледнее: список «растворяется» книзу
                dc.PushOpacity(1 - r / (double)(Rows + 2));

                double phase = ((t / 1.6) - r * 0.06) % 1;
                if (phase < 0) phase += 1;
                var shine = new LinearGradientBrush
                {
                    StartPoint = new Point(phase * 1.6 - 0.4, 0),
                    EndPoint = new Point(phase * 1.6 - 0.1, 0),
                    MappingMode = BrushMappingMode.RelativeToBoundingBox,
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
                        new GradientStop(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), 0.5),
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
                    },
                };
                shine.Freeze();

                // длина полосок у каждой строки своя — выглядит как настоящий список
                double titleW = Math.Min(w - 80, 140 + (r * 53 % 7) * 22);
                double subW = Math.Min(w - 80, 90 + (r * 31 % 5) * 18);

                var cover = new Rect(8, y, 44, 44);
                var title = new Rect(66, y + 8, titleW, 11);
                var sub = new Rect(66, y + 27, subW, 9);
                var time = new Rect(Math.Max(66 + titleW + 20, w - 50), y + 17, 32, 9);

                foreach (var rect in new[] { cover, title, sub, time })
                {
                    if (rect.Right > w) continue;
                    double radius = rect == cover ? 8 : 4.5;
                    dc.DrawRoundedRectangle(Base, null, rect, radius, radius);
                }
                // блик поверх всей строки сразу — полосы и обложка блестят одной волной
                dc.PushClip(RowClip(w, y, cover, title, sub, time));
                dc.DrawRectangle(shine, null, new Rect(0, y, w, 44));
                dc.Pop();
                dc.Pop();
            }
        }

        private static Geometry RowClip(double w, double y, params Rect[] rects)
        {
            var group = new GeometryGroup();
            foreach (var r in rects)
                if (r.Right <= w) group.Children.Add(new RectangleGeometry(r, 5, 5));
            group.Freeze();
            return group;
        }

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    }
}
