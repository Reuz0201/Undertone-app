using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Undertone.Audio;

namespace Undertone.UI
{
    public enum AmbientMode
    {
        Off = 0,
        /// <summary>Звёздная пыль: светлячки цветов обложки плывут вверх, на ударах вспыхивают и ускоряются.</summary>
        Particles = 1,
        /// <summary>Волны: слои мягких волн у низа экрана, высота — по басам, серединам и верхам.</summary>
        Waves = 2,
        /// <summary>Пульс: от обложки на каждый удар расходятся кольца, под ней дышит свечение.</summary>
        Pulse = 3,
    }

    /// <summary>
    /// Визуализация на фоне экрана «Сейчас играет». Слушает тот же анализатор спектра, что и визуализатор
    /// под треком: басы, середина, верха и удары. Когда звука нет (пауза или Spotify, который не отдаёт звук
    /// сторонним программам) — двигается сама, спокойно. Рисует только пока видна; без музыки — вдвое реже.
    /// </summary>
    public sealed class AmbientViz : FrameworkElement
    {
        private AmbientMode _mode = AmbientMode.Off;
        public AmbientMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                _rings.Clear();
                Sync();
                InvalidateVisual();
            }
        }

        /// <summary>Откуда брать звук.</summary>
        public AudioEngine? Engine { get; set; }

        /// <summary>Играет ли сейчас музыка.</summary>
        public Func<bool>? IsActive { get; set; }

        /// <summary>Вокруг чего расходятся кольца «Пульса» (обычно — большая обложка).</summary>
        public FrameworkElement? FocusElement { get; set; }

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _last;
        private bool _hooked;
        private int _frame;

        // ── звук ──
        private readonly float[] _bands = new float[24];
        private float _bass, _mid, _high, _bassAvg, _flash, _energy;
        private double _lastBeat;
        private bool _beat;

        // ── цвета ──
        private Color[] _colors = { Color.FromRgb(0x8E, 0x9C, 0xD8), Color.FromRgb(0xC9, 0x8E, 0xD8) };
        private Brush[] _glow = Array.Empty<Brush>();
        private Brush[] _waveFill = Array.Empty<Brush>();
        private Pen[] _wavePen = Array.Empty<Pen>();

        // ── частицы ──
        private struct Mote { public double X, Y, Speed, Size, Phase, Drift; public int Color; }
        private readonly Mote[] _motes = new Mote[110];
        private readonly Random _rnd = new(7);

        // ── волны ──
        private double _wavePhase;

        // ── пульс ──
        private struct Ring { public double Born, Strength; public int Color; }
        private readonly List<Ring> _rings = new();
        private double _idleBeat;

        public AmbientViz()
        {
            IsHitTestVisible = false;
            for (int i = 0; i < _motes.Length; i++) _motes[i] = NewMote(randomY: true);
            BuildBrushes();
            IsVisibleChanged += (_, _) => Sync();
            Unloaded += (_, _) => Unhook();
        }

        /// <summary>Цвета обложки (2–4). Пятна, волны и кольца перекрашиваются со следующего кадра.</summary>
        public void SetColors(Color[] colors)
        {
            if (colors.Length == 0) return;
            // фон тёмный: чуть высветляем и насыщаем, иначе тёмные обложки дадут невидимую картинку
            var list = new Color[colors.Length];
            for (int i = 0; i < colors.Length; i++) list[i] = Lift(colors[i]);
            _colors = list;
            BuildBrushes();
            InvalidateVisual();
        }

        private static Color Lift(Color c)
        {
            double max = Math.Max(c.R, Math.Max(c.G, c.B));
            double k = max < 150 ? 150 / Math.Max(max, 1) : 1;
            byte F(byte v) => (byte)Math.Min(255, v * k * 0.85 + 255 * 0.15);
            return Color.FromRgb(F(c.R), F(c.G), F(c.B));
        }

        private void BuildBrushes()
        {
            int n = _colors.Length;
            _glow = new Brush[n];
            _waveFill = new Brush[n];
            _wavePen = new Pen[n];
            for (int i = 0; i < n; i++)
            {
                var c = _colors[i];
                var glow = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0xFF, c.R, c.G, c.B), 0),
                        new GradientStop(Color.FromArgb(0x55, c.R, c.G, c.B), 0.35),
                        new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
                    },
                };
                glow.Freeze();
                _glow[i] = glow;

                var fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x46, c.R, c.G, c.B), 0),
                        new GradientStop(Color.FromArgb(0x08, c.R, c.G, c.B), 1),
                    },
                };
                fill.Freeze();
                _waveFill[i] = fill;

                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x8C, c.R, c.G, c.B)), 1.6) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                _wavePen[i] = pen;
            }
        }

        private Mote NewMote(bool randomY) => new()
        {
            X = _rnd.NextDouble(),
            Y = randomY ? _rnd.NextDouble() : 1.04 + _rnd.NextDouble() * 0.1,
            Speed = 0.012 + _rnd.NextDouble() * 0.03,
            Size = 1.6 + Math.Pow(_rnd.NextDouble(), 2.0) * 6.5,
            Phase = _rnd.NextDouble() * Math.PI * 2,
            Drift = 0.004 + _rnd.NextDouble() * 0.012,
            Color = _rnd.Next(16),
        };

        // ────────── кадры ──────────

        private void Sync()
        {
            if (IsVisible && _mode != AmbientMode.Off) Hook();
            else Unhook();
        }

        private void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            _last = _clock.Elapsed.TotalSeconds;
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
            var active = Engine?.HasMedia == true && IsActive?.Invoke() == true;
            // без музыки картинка спокойная — хватает 30 кадров
            if (!active && (++_frame & 1) == 1) return;

            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - _last, 0, 0.1);
            _last = now;

            Listen(active, now, (float)dt);
            switch (_mode)
            {
                case AmbientMode.Particles: StepMotes(dt); break;
                case AmbientMode.Waves: _wavePhase += dt * (0.5 + _energy * 1.6); break;
                case AmbientMode.Pulse: StepRings(now); break;
            }
            InvalidateVisual();
        }

        /// <summary>Басы / середина / верха (0..1, сглажены) и удары по басам.</summary>
        private void Listen(bool active, double now, float dt)
        {
            float bass = 0, mid = 0, high = 0;
            bool heard = false;
            if (active && Engine is { } engine && engine.Analyzer.ComputeBands(_bands, engine.AnalyzerDelaySamples))
            {
                for (int i = 0; i < 4; i++) bass += _bands[i];
                for (int i = 4; i < 13; i++) mid += _bands[i];
                for (int i = 13; i < _bands.Length; i++) high += _bands[i];
                bass /= 4; mid /= 9; high /= _bands.Length - 13;
                heard = bass + mid + high > 0.02f;
            }
            if (!heard)
            {
                // звука нет — мягкое «дыхание», чтобы фон не замирал
                float s = (float)(0.5 + 0.5 * Math.Sin(now * 0.9));
                bass = 0.18f + s * 0.08f;
                mid = 0.14f + (float)(0.5 + 0.5 * Math.Sin(now * 0.6 + 1)) * 0.06f;
                high = 0.1f;
            }

            float k = Math.Min(1f, dt * 14f);
            _bass += (bass - _bass) * k;
            _mid += (mid - _mid) * Math.Min(1f, dt * 8f);
            _high += (high - _high) * Math.Min(1f, dt * 10f);
            _energy += ((_bass + _mid + _high) / 3 - _energy) * Math.Min(1f, dt * 3f);

            // удар: бас резко выше своего среднего за последние секунды
            _beat = false;
            if (heard)
            {
                // сравниваем с коротким средним: на плотном басе (фонк, трэп) он всё время высокий,
                // и порог «в 1.2 раза выше» почти не срабатывал бы
                if (bass > _bassAvg + 0.055f && bass > 0.22f && now - _lastBeat > 0.25)
                {
                    _beat = true;
                    _lastBeat = now;
                    _flash = 1;
                }
                else if (bass > 0.25f && now - _lastBeat > 1.4)
                {
                    // ударов давно не было (ровный звук) — тихое кольцо, чтобы фон не замирал
                    _beat = true;
                    _lastBeat = now;
                    _flash = Math.Max(_flash, 0.45f);
                }
                _bassAvg += (bass - _bassAvg) * Math.Min(1f, dt * 4f);
            }
            else if (now - _idleBeat > 1.6)
            {
                // без звука кольца «Пульса» расходятся сами, редко и тихо
                _idleBeat = now;
                _beat = true;
                _flash = Math.Max(_flash, 0.35f);
            }
            _flash = Math.Max(0, _flash - dt * 2.6f);
        }

        private void StepMotes(double dt)
        {
            double lift = 1 + _bass * 2.2 + _flash * 2.5;
            for (int i = 0; i < _motes.Length; i++)
            {
                ref var m = ref _motes[i];
                m.Y -= m.Speed * lift * dt;
                m.Phase += dt * (0.6 + m.Drift * 40);
                m.X += Math.Sin(m.Phase) * m.Drift * dt;
                if (m.Y < -0.05 || m.X < -0.05 || m.X > 1.05) m = NewMote(randomY: false);
            }
        }

        private void StepRings(double now)
        {
            if (_beat && _rings.Count < 14)
                _rings.Add(new Ring { Born = now, Strength = Math.Clamp(0.35 + _bass, 0.3, 1), Color = _rings.Count + _rnd.Next(3) });
            _rings.RemoveAll(r => now - r.Born > RingLife);
        }

        private const double RingLife = 2.6;

        // ────────── рисование ──────────

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 10 || h < 10 || _mode == AmbientMode.Off || _glow.Length == 0) return;
            switch (_mode)
            {
                case AmbientMode.Particles: DrawMotes(dc, w, h); break;
                case AmbientMode.Waves: DrawWaves(dc, w, h); break;
                case AmbientMode.Pulse: DrawPulse(dc, w, h); break;
            }
        }

        private void DrawMotes(DrawingContext dc, double w, double h)
        {
            double twinkleT = _clock.Elapsed.TotalSeconds;
            for (int i = 0; i < _motes.Length; i++)
            {
                var m = _motes[i];
                // верхи — мерцание, удар — вспышка крупных
                double twinkle = 0.55 + 0.45 * Math.Sin(twinkleT * (1.5 + m.Drift * 120) + m.Phase * 3);
                double r = m.Size * (1 + _high * 0.9 + _flash * (m.Size > 4 ? 0.9 : 0.35)) * 3.4;
                double alpha = Math.Clamp((0.45 + _high * 0.5 + _flash * 0.45) * twinkle, 0, 1);
                // у нижнего края частицы только проявляются, у верхнего — гаснут
                alpha *= Math.Clamp(Math.Min((1.04 - m.Y) * 6, m.Y * 5), 0, 1);
                if (alpha < 0.02) continue;
                dc.PushOpacity(alpha);
                dc.DrawEllipse(_glow[m.Color % _glow.Length], null, new Point(m.X * w, m.Y * h), r, r);
                dc.Pop();
            }
        }

        private void DrawWaves(DrawingContext dc, double w, double h)
        {
            // четыре слоя от дальнего к ближнему: дальние выше и спокойнее, ближние ниже и живее
            float[] drive = { _mid, _high, _bass, _bass * 0.7f + _mid * 0.3f };
            for (int layer = 0; layer < 4; layer++)
            {
                double baseY = h * (0.58 + layer * 0.085);
                double amp = h * (0.018 + drive[layer] * (0.05 + layer * 0.022)) * (1 + _flash * 0.35);
                double k1 = (1.4 + layer * 0.45) * Math.PI * 2 / w;
                double k2 = k1 * 2.3;
                double ph = _wavePhase * (0.7 + layer * 0.22) + layer * 1.7;
                double dir = layer % 2 == 0 ? 1 : -1;

                var geo = new StreamGeometry();
                var line = new StreamGeometry();
                using (var g = geo.Open())
                using (var l = line.Open())
                {
                    const double step = 14;
                    bool first = true;
                    for (double x = -step; x <= w + step; x += step)
                    {
                        double y = baseY - amp * (0.65 * Math.Sin(x * k1 + ph * dir) + 0.35 * Math.Sin(x * k2 - ph * 1.3 * dir));
                        var p = new Point(x, y);
                        if (first)
                        {
                            g.BeginFigure(new Point(x, h + 2), isFilled: true, isClosed: true);
                            g.LineTo(p, false, false);
                            l.BeginFigure(p, isFilled: false, isClosed: false);
                            first = false;
                        }
                        else
                        {
                            g.LineTo(p, false, true);
                            l.LineTo(p, true, true);
                        }
                    }
                    g.LineTo(new Point(w + step, h + 2), false, false);
                }
                geo.Freeze();
                line.Freeze();
                int c = layer % _waveFill.Length;
                dc.DrawGeometry(_waveFill[c], null, geo);
                dc.PushOpacity(0.55 + layer * 0.12);
                dc.DrawGeometry(null, _wavePen[c], line);
                dc.Pop();
            }
        }

        private void DrawPulse(DrawingContext dc, double w, double h)
        {
            var center = new Point(w * 0.22, h * 0.3);
            double coverR = Math.Min(w, h) * 0.16;
            if (FocusElement is { IsVisible: true, ActualWidth: > 0 } focus)
            {
                try
                {
                    center = focus.TranslatePoint(new Point(focus.ActualWidth / 2, focus.ActualHeight / 2), this);
                    coverR = focus.ActualWidth / 2;
                }
                catch (InvalidOperationException) { }
            }

            // свечение под обложкой дышит с басом
            double glowR = coverR * (1.8 + _bass * 0.6 + _flash * 0.35);
            dc.PushOpacity(Math.Clamp(0.32 + _bass * 0.45 + _flash * 0.3, 0, 0.9));
            dc.DrawEllipse(_glow[0], null, center, glowR, glowR);
            dc.Pop();

            double now = _clock.Elapsed.TotalSeconds;
            double reach = Math.Max(w, h) * 0.75;
            foreach (var ring in _rings)
            {
                double age = (now - ring.Born) / RingLife; // 0..1
                double ease = 1 - Math.Pow(1 - age, 2);
                double r = coverR * 0.75 + (reach - coverR) * ease;
                double alpha = (1 - age) * 0.9 * ring.Strength;
                if (alpha < 0.01) continue;
                var c = _colors[ring.Color % _colors.Length];
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, c.R, c.G, c.B)), 1.5 + 5 * (1 - age) * ring.Strength);
                pen.Freeze();
                dc.PushOpacity(alpha);
                dc.DrawEllipse(null, pen, center, r, r);
                dc.Pop();
            }
        }
    }
}
