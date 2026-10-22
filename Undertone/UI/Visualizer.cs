using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Undertone.Audio;

namespace Undertone.UI
{
    public enum VisualizerMode
    {
        /// <summary>Сплошные столбики зелёный → жёлтый → красный, «шапки» пиков — как в Winamp 2.</summary>
        Classic,
        /// <summary>То же, но сегментами — светодиодная линейка.</summary>
        Led,
        /// <summary>Серебро в стиле логотипа, скруглённые столбики с отражением.</summary>
        Undertone,
        /// <summary>Осциллограф.</summary>
        Scope,
    }

    /// <summary>
    /// Визуализатор в духе Winamp. Рисует сам (OnRender) на каждом кадре, пока играет музыка;
    /// когда тихо и всё «упало» — отписывается от кадров и не тратит CPU. Клик — следующий стиль.
    /// </summary>
    public sealed class Visualizer : FrameworkElement
    {
        public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
            nameof(Mode), typeof(VisualizerMode), typeof(Visualizer),
            new FrameworkPropertyMetadata(VisualizerMode.Classic, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BarCountProperty = DependencyProperty.Register(
            nameof(BarCount), typeof(int), typeof(Visualizer),
            new FrameworkPropertyMetadata(48, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Visualizer)d).Allocate()));

        public VisualizerMode Mode
        {
            get => (VisualizerMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        public int BarCount
        {
            get => (int)GetValue(BarCountProperty);
            set => SetValue(BarCountProperty, value);
        }

        /// <summary>Откуда брать звук.</summary>
        public AudioEngine? Engine { get; set; }

        /// <summary>Играет ли сейчас музыка (на паузе столбики плавно опускаются).</summary>
        public Func<bool>? IsActive { get; set; }

        /// <summary>Пользователь кликнул — стиль сменился.</summary>
        public event EventHandler? ModeCycled;

        private float[] _targets = Array.Empty<float>();
        private float[] _bars = Array.Empty<float>();
        private float[] _peaks = Array.Empty<float>();
        private float[] _peakVel = Array.Empty<float>();
        private float[] _peakHold = Array.Empty<float>();
        private readonly float[] _wave = new float[1024];
        private readonly float[] _waveShown = new float[1024];

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastFrame;
        private bool _hooked;
        private readonly DispatcherTimer _poll;

        private Brush? _classicBrush;
        private Brush? _silverBrush;
        private double _brushHeight = -1;

        private static readonly Brush PeakBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE6)));
        private static readonly Brush SilverPeakBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
        private static readonly Pen ScopeGlowPen = FrozenPen(Color.FromArgb(0x40, 0x9F, 0xC4, 0xFF), 5);
        private static readonly Pen ScopePen = FrozenPen(Color.FromRgb(0xEE, 0xF2, 0xFA), 1.6);
        private static readonly Pen ScopeMidPen = FrozenPen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 1);

        public Visualizer()
        {
            Allocate();
            Cursor = Cursors.Hand;
            ToolTip = "Клик — сменить стиль визуализации";
            SnapsToDevicePixels = true;

            // раз в 200 мс проверяем, не пора ли снова рисовать (дёшево, в отличие от подписки на каждый кадр)
            _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            _poll.Tick += (_, _) => { if (ShouldRun()) Hook(); };

            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) { _poll.Start(); if (ShouldRun()) Hook(); }
                else { _poll.Stop(); Unhook(); }
            };
            Unloaded += (_, _) => { _poll.Stop(); Unhook(); };
        }

        private bool Active => Engine?.HasMedia == true && IsActive?.Invoke() == true;
        private bool ShouldRun() => IsVisible && Active;

        private void Allocate()
        {
            int n = Math.Max(4, BarCount);
            _targets = new float[n];
            _bars = new float[n];
            _peaks = new float[n];
            _peakVel = new float[n];
            _peakHold = new float[n];
        }

        private void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            _lastFrame = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }

        private void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Clamp(now - _lastFrame, 0, 0.1);
            _lastFrame = now;

            var active = Active;
            var engine = Engine;
            int delay = engine?.AnalyzerDelaySamples ?? 0;

            if (Mode == VisualizerMode.Scope)
            {
                if (active && engine != null) engine.Analyzer.ComputeWave(_wave, delay);
                else Array.Clear(_wave);
                float k = active ? 1f : Math.Min(1f, dt * 6f);
                float energy = 0;
                for (int i = 0; i < _wave.Length; i++)
                {
                    _waveShown[i] += (_wave[i] - _waveShown[i]) * (active ? 1f : k);
                    energy += Math.Abs(_waveShown[i]);
                }
                InvalidateVisual();
                if (!active && energy < 0.01f) Unhook();
                return;
            }

            if (active && engine != null) engine.Analyzer.ComputeBands(_targets, delay);
            else Array.Clear(_targets);

            float total = 0;
            for (int i = 0; i < _bars.Length; i++)
            {
                float t = _targets[i];
                float b = _bars[i];
                // быстрая атака, плавное падение — как у Winamp
                b = t > b ? b + (t - b) * Math.Min(1f, dt * 30f) : Math.Max(t, b - dt * 1.5f);
                _bars[i] = b;

                if (b >= _peaks[i])
                {
                    _peaks[i] = b;
                    _peakHold[i] = 0.35f;
                    _peakVel[i] = 0;
                }
                else if (_peakHold[i] > 0)
                {
                    _peakHold[i] -= dt;
                }
                else
                {
                    _peakVel[i] += dt * 2.4f; // «гравитация» для шапок
                    _peaks[i] = Math.Max(b, _peaks[i] - _peakVel[i] * dt);
                }
                total += b + _peaks[i];
            }

            InvalidateVisual();
            if (!active && total < 0.001f) Unhook();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Mode = (VisualizerMode)(((int)Mode + 1) % 4);
            ModeCycled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        // ────────── рисование ──────────

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // ловим клики по всей площади
            EnsureBrushes(h);

            switch (Mode)
            {
                case VisualizerMode.Classic: DrawBars(dc, w, h, segmented: false); break;
                case VisualizerMode.Led: DrawBars(dc, w, h, segmented: true); break;
                case VisualizerMode.Undertone: DrawSilver(dc, w, h); break;
                case VisualizerMode.Scope: DrawScope(dc, w, h); break;
            }
        }

        private void DrawBars(DrawingContext dc, double w, double h, bool segmented)
        {
            int n = _bars.Length;
            double slot = w / n;
            double gap = Math.Max(1, Math.Round(slot * 0.22));
            double bw = Math.Max(1, slot - gap);
            double seg = h >= 80 ? 4 : 2, segGap = h >= 80 ? 2 : 1;

            var bars = new StreamGeometry();
            var peaks = new StreamGeometry();
            using (var g = bars.Open())
            using (var p = peaks.Open())
            {
                for (int i = 0; i < n; i++)
                {
                    double x = Math.Round(i * slot);
                    double bh = _bars[i] * h;
                    if (segmented)
                    {
                        int count = (int)(bh / (seg + segGap));
                        for (int s = 0; s < count; s++)
                        {
                            double y = h - (s + 1) * (seg + segGap) + segGap;
                            AddRect(g, x, y, bw, seg);
                        }
                    }
                    else if (bh >= 1)
                    {
                        AddRect(g, x, h - bh, bw, bh);
                    }

                    if (_peaks[i] > 0.01)
                    {
                        double py = Math.Min(h - 2, h - _peaks[i] * h - 3);
                        AddRect(p, x, py, bw, segmented ? seg : 2);
                    }
                }
            }
            bars.Freeze();
            peaks.Freeze();
            dc.DrawGeometry(_classicBrush, null, bars);
            dc.DrawGeometry(PeakBrush, null, peaks);
        }

        private void DrawSilver(DrawingContext dc, double w, double h)
        {
            int n = _bars.Length;
            double slot = w / n;
            double gap = Math.Max(1.5, slot * 0.3);
            double bw = Math.Max(1.5, slot - gap);
            bool reflect = h >= 60;
            double baseY = reflect ? h * 0.78 : h;
            double radius = Math.Min(bw / 2, 4);

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (int i = 0; i < n; i++)
                {
                    double bh = Math.Max(2, _bars[i] * baseY);
                    AddRoundedTop(g, i * slot + gap / 2, baseY - bh, bw, bh, radius);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(_silverBrush, null, geo);

            if (reflect)
            {
                dc.PushOpacity(0.16);
                dc.PushTransform(new MatrixTransform(1, 0, 0, -0.3, 0, baseY * 1.3));
                dc.DrawGeometry(_silverBrush, null, geo);
                dc.Pop();
                dc.Pop();
            }

            var peaks = new StreamGeometry();
            using (var p = peaks.Open())
            {
                for (int i = 0; i < n; i++)
                    if (_peaks[i] > 0.01)
                        AddRect(p, i * slot + gap / 2, baseY - _peaks[i] * baseY - 4, bw, 1.5);
            }
            peaks.Freeze();
            dc.DrawGeometry(SilverPeakBrush, null, peaks);
        }

        private void DrawScope(DrawingContext dc, double w, double h)
        {
            double mid = h / 2;
            dc.DrawLine(ScopeMidPen, new Point(0, mid), new Point(w, mid));

            int points = Math.Min(_waveShown.Length, Math.Max(64, (int)(w / 1.5)));
            int step = Math.Max(1, _waveShown.Length / points);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (int i = 0, k = 0; i < _waveShown.Length; i += step, k++)
                {
                    double x = (double)i / (_waveShown.Length - 1) * w;
                    double v = Math.Clamp(_waveShown[i] * 1.6, -1, 1);
                    var pt = new Point(x, mid - v * mid * 0.9);
                    if (k == 0) g.BeginFigure(pt, false, false);
                    else g.LineTo(pt, true, false);
                }
            }
            geo.Freeze();
            if (h >= 60) dc.DrawGeometry(null, ScopeGlowPen, geo);
            dc.DrawGeometry(null, ScopePen, geo);
        }

        private static void AddRect(StreamGeometryContext g, double x, double y, double w, double h)
        {
            g.BeginFigure(new Point(x, y), true, true);
            g.LineTo(new Point(x + w, y), false, false);
            g.LineTo(new Point(x + w, y + h), false, false);
            g.LineTo(new Point(x, y + h), false, false);
        }

        private static void AddRoundedTop(StreamGeometryContext g, double x, double y, double w, double h, double r)
        {
            r = Math.Min(r, Math.Min(w / 2, h));
            g.BeginFigure(new Point(x, y + h), true, true);
            g.LineTo(new Point(x, y + r), false, false);
            g.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(x + w - r, y), false, false);
            g.ArcTo(new Point(x + w, y + r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(x + w, y + h), false, false);
        }

        /// <summary>Градиенты привязаны к высоте контрола (а не столбика) — цвет зависит от уровня, как в Winamp.</summary>
        private void EnsureBrushes(double h)
        {
            if (Math.Abs(h - _brushHeight) < 0.5 && _classicBrush != null) return;
            _brushHeight = h;

            var classic = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, h),
                EndPoint = new Point(0, 0),
            };
            classic.GradientStops.Add(new GradientStop(Color.FromRgb(0x12, 0xB0, 0x3C), 0.0));
            classic.GradientStops.Add(new GradientStop(Color.FromRgb(0x5C, 0xD6, 0x2A), 0.35));
            classic.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0xD0, 0x1E), 0.65));
            classic.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0x7A, 0x1A), 0.82));
            classic.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0x2A, 0x2A), 1.0));
            classic.Freeze();
            _classicBrush = classic;

            var silver = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, h),
                EndPoint = new Point(0, 0),
            };
            silver.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x40, 0x52), 0.0));
            silver.GradientStops.Add(new GradientStop(Color.FromRgb(0x8E, 0x97, 0xAC), 0.45));
            silver.GradientStops.Add(new GradientStop(Color.FromRgb(0xD9, 0xDE, 0xE8), 0.8));
            silver.GradientStops.Add(new GradientStop(Colors.White, 1.0));
            silver.Freeze();
            _silverBrush = silver;
        }

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }

        private static Pen FrozenPen(Color c, double thickness)
        {
            var p = new Pen(new SolidColorBrush(c), thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }
    }
}
