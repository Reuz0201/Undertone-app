using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Undertone.UI
{
    /// <summary>
    /// Анимации интерфейса: страницы выплывают, списки появляются «лесенкой», обложка нового трека мягко
    /// встаёт на место, сердечко лайка подпрыгивает, кнопки пружинят, окна диалогов раскрываются.
    ///
    /// Всё в коде, а не в стилях: одним флагом <see cref="Enabled"/> анимации выключаются целиком,
    /// и уважается системная настройка Windows «Показывать анимацию».
    /// Анимации не держат значения (FillBehavior.Stop): после них свойства возвращаются к своим, и ничего
    /// в остальном коде не ломается, даже если анимация оборвалась на полпути.
    /// </summary>
    public static class Motion
    {
        public static bool Enabled { get; set; } = true;

        /// <summary>Анимировать ли сейчас: и в настройках включено, и Windows не просит обойтись без анимаций.</summary>
        public static bool On => Enabled && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

        private static readonly IEasingFunction OutCubic = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
        private static readonly IEasingFunction OutQuint = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
        private static readonly IEasingFunction OutBack = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
        private static readonly IEasingFunction Spring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 });

        private static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

        // ────────── базовые кирпичики ──────────

        /// <summary>
        /// from → to за ms после задержки delay. Ключевые кадры, а не BeginTime: во время задержки
        /// держится from (иначе элемент мигнул бы своим обычным видом перед стартом).
        /// </summary>
        private static AnimationTimeline Tween(double from, double to, int ms, int delay = 0, IEasingFunction? ease = null)
        {
            var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            if (delay > 0) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + ms)), ease ?? OutCubic));
            a.Freeze();
            return a;
        }

        /// <summary>Наши трансформации: масштаб и сдвиг. null — у элемента уже своя, её не трогаем.</summary>
        private static (ScaleTransform Scale, TranslateTransform Move)? Transforms(UIElement el)
        {
            if (el.RenderTransform is TransformGroup { Children.Count: 2 } g &&
                g.Children[0] is ScaleTransform { IsFrozen: false } s && g.Children[1] is TranslateTransform { IsFrozen: false } t)
                return (s, t);
            if (el.RenderTransform is not null && el.RenderTransform != Transform.Identity &&
                !(el.RenderTransform is MatrixTransform m && m.Matrix.IsIdentity))
                return null;
            var scale = new ScaleTransform();
            var move = new TranslateTransform();
            el.RenderTransform = new TransformGroup { Children = { scale, move } };
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            return (scale, move);
        }

        /// <summary>Появление: прозрачность 0 → 1 и подъём на rise пикселей снизу.</summary>
        public static void Enter(UIElement el, double rise = 14, int ms = 300, int delay = 0)
        {
            if (!On) return;
            el.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, ms, delay));
            if (rise != 0 && Transforms(el) is { } t)
                t.Move.BeginAnimation(TranslateTransform.YProperty, Tween(rise, 0, ms + 60, delay, OutQuint));
        }

        /// <summary>Раскрытие: масштаб from → 1 с лёгким перелётом.</summary>
        public static void PopIn(UIElement el, double from = 0.94, int ms = 340, bool fade = true)
        {
            if (!On) return;
            if (fade) el.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, ms - 80));
            if (Transforms(el) is { } t)
            {
                t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(from, 1, ms, 0, OutBack));
                t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(from, 1, ms, 0, OutBack));
            }
        }

        /// <summary>Прыжок: 1 → peak → 1 с пружиной (лайк, ▶).</summary>
        public static void Bounce(UIElement el, double peak = 1.3, int ms = 520)
        {
            if (!On || Transforms(el) is not { } t) return;
            var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms * 0.28)), OutCubic));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), Spring));
            a.Freeze();
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        private static void ScaleTo(UIElement el, double to, int ms, IEasingFunction ease)
        {
            if (Transforms(el) is not { } t) return;
            if (!On)
            {
                t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                return;
            }
            // от текущего значения: наведение и уход мыши могут прервать друг друга посередине
            var x = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            x.Freeze();
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, x);
        }

        // ────────── сценарии ──────────

        /// <summary>
        /// Куда идём по меню: +1 — вниз (новая страница въезжает снизу), −1 — вверх (сверху), 0 — без направления.
        /// Ставится перед сменой страницы и читается в момент её показа.
        /// </summary>
        public static int PageDirection { get; set; }

        /// <summary>Страница выплывает, когда становится видимой; её списки появляются лесенкой.</summary>
        public static void PageOnShow(FrameworkElement page, params ItemsControl[] lists)
        {
            page.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is not true || !On) return;
                var dir = PageDirection;
                page.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, 300));
                if (Transforms(page) is { } t)
                {
                    t.Move.BeginAnimation(TranslateTransform.YProperty, Tween(dir == 0 ? 18 : 34 * dir, 0, 460, 0, OutQuint));
                    t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(0.985, 1, 420, 0, OutQuint));
                    t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(0.985, 1, 420, 0, OutQuint));
                }
                foreach (var list in lists) Cascade(list, delay: 70);
            };
            foreach (var list in lists) CascadeOnNewItems(list);
        }

        /// <summary>
        /// Видимые строки списка появляются по очереди, сверху вниз. Только первые max: остальные всё равно
        /// за краем, а длинная лесенка выглядит как тормоза.
        /// </summary>
        public static void Cascade(ItemsControl list, int max = 16, int step = 24, int delay = 0)
        {
            if (!On) return;
            // контейнеры строк появляются после разметки — ждём её
            list.Dispatcher.InvokeAsync(() =>
            {
                if (!list.IsVisible) return;
                for (int i = 0, shown = 0; i < list.Items.Count && shown < max; i++)
                {
                    if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row) break;
                    if (!row.IsVisible) continue;
                    Enter(row, rise: 10, ms: 260, delay: delay + shown * step);
                    shown++;
                }
            }, DispatcherPriority.Loaded);
        }

        /// <summary>Новый набор элементов (сменился ItemsSource) — лесенкой, если список на экране.</summary>
        private static void CascadeOnNewItems(ItemsControl list)
        {
            DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ItemsControl))
                .AddValueChanged(list, (_, _) => { if (list.IsVisible) Cascade(list); });
        }

        /// <summary>Затемнённое окно поверх (диалог, обновление): само проявляется, карточка внутри раскрывается.</summary>
        public static void OverlayOnShow(Panel overlay)
        {
            overlay.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is not true || !On) return;
                overlay.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, 200));
                foreach (UIElement child in overlay.Children)
                    if (child is Border or Grid or StackPanel)
                    {
                        PopIn(child, from: 0.93, ms: 380, fade: false);
                        break;
                    }
            };
        }

        /// <summary>Нажатие: кнопка чуть проседает и пружинит обратно.</summary>
        public static void Pressable(ButtonBase button, double pressed = 0.9)
        {
            button.PreviewMouseLeftButtonDown += (_, _) => ScaleTo(button, pressed, 90, OutCubic);
            button.PreviewMouseLeftButtonUp += (_, _) => ScaleTo(button, 1, 480, Spring);
            button.MouseLeave += (_, _) => ScaleTo(button, 1, 220, OutCubic);
        }

        /// <summary>Карточки списка (полки «Похожие», альбомы) чуть приподнимаются под мышью.</summary>
        public static void HoverLift(ItemsControl list, double scale = 1.045)
        {
            list.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler((_, e) => Hover(list, e, scale)), true);
            list.MouseLeave += (_, _) => Unhover(list);
        }

        private static readonly DependencyProperty HoveredProperty =
            DependencyProperty.RegisterAttached("Hovered", typeof(FrameworkElement), typeof(Motion));

        private static void Hover(ItemsControl list, MouseEventArgs e, double scale)
        {
            var container = e.OriginalSource is DependencyObject d ? list.ContainerFromElement(d) as FrameworkElement : null;
            var current = list.GetValue(HoveredProperty) as FrameworkElement;
            if (container == current) return;
            if (current != null) ScaleTo(current, 1, 220, OutCubic);
            if (container != null) ScaleTo(container, scale, 200, OutBack);
            list.SetValue(HoveredProperty, container);
        }

        private static void Unhover(ItemsControl list)
        {
            if (list.GetValue(HoveredProperty) is FrameworkElement current) ScaleTo(current, 1, 220, OutCubic);
            list.SetValue(HoveredProperty, null);
        }

        /// <summary>
        /// Сменился трек. direction: +1 — «следующий» (новая обложка въезжает справа), −1 — «предыдущий» (слева),
        /// 0 — выбран из списка: обложка встаёт на место из чуть меньшей. Название и артисты следом.
        /// </summary>
        public static void TrackChanged(UIElement cover, UIElement? smallCover, int direction, params UIElement[] texts)
        {
            if (!On) return;
            if (direction == 0)
            {
                PopIn(cover, from: 0.9, ms: 520, fade: false);
                cover.BeginAnimation(UIElement.OpacityProperty, Tween(0.35, 1, 380));
                if (smallCover != null) PopIn(smallCover, from: 0.8, ms: 420);
                for (int i = 0; i < texts.Length; i++)
                    Enter(texts[i], rise: 12, ms: 340, delay: 60 + i * 50);
                return;
            }
            Slide(cover, 70 * direction, 560, scaleFrom: 0.92);
            cover.BeginAnimation(UIElement.OpacityProperty, Tween(0.15, 1, 420));
            if (smallCover != null)
            {
                Slide(smallCover, 18 * direction, 420, scaleFrom: 0.85);
                smallCover.BeginAnimation(UIElement.OpacityProperty, Tween(0.2, 1, 320));
            }
            for (int i = 0; i < texts.Length; i++)
            {
                texts[i].BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, 340, 70 + i * 45));
                Slide(texts[i], 40 * direction, 480, delay: 70 + i * 45);
            }
        }

        /// <summary>Сдвиг по горизонтали from → 0 (и масштаб scaleFrom → 1).</summary>
        private static void Slide(UIElement el, double from, int ms, double scaleFrom = 1, int delay = 0)
        {
            if (Transforms(el) is not { } t) return;
            t.Move.BeginAnimation(TranslateTransform.XProperty, Tween(from, 0, ms, delay, OutQuint));
            if (scaleFrom != 1)
            {
                t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(scaleFrom, 1, ms, delay, OutQuint));
                t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(scaleFrom, 1, ms, delay, OutQuint));
            }
        }

        /// <summary>Всплывающая подсказка снизу: поднимается вместе с проявлением.</summary>
        public static void ToastIn(UIElement toast)
        {
            if (!On || Transforms(toast) is not { } t) return;
            t.Move.BeginAnimation(TranslateTransform.YProperty, Tween(16, 0, 380, 0, OutBack));
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(0.96, 1, 300));
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(0.96, 1, 300));
        }

        /// <summary>Подсказка уходит: опускается, пока гаснет.</summary>
        public static void ToastOut(UIElement toast)
        {
            if (!On || Transforms(toast) is not { } t) return;
            t.Move.BeginAnimation(TranslateTransform.YProperty, Tween(0, 14, 300, 0, OutCubic));
        }

        // ────────── меню слева ──────────

        /// <summary>
        /// Подсветка выбранного пункта меню переезжает к новому пункту с лёгким перелётом.
        /// target == null — ни один пункт не выбран (открыт артист или альбом): подсветка гаснет.
        /// </summary>
        public static void MoveIndicator(FrameworkElement pill, FrameworkElement? target, FrameworkElement container)
        {
            if (pill.RenderTransform is not TranslateTransform move || move.IsFrozen)
                pill.RenderTransform = move = new TranslateTransform();

            if (target == null || !target.IsVisible || target.ActualHeight <= 0)
            {
                if (On) pill.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
                else { pill.BeginAnimation(UIElement.OpacityProperty, null); pill.Opacity = 0; }
                return;
            }

            double y, h = target.ActualHeight;
            try { y = target.TranslatePoint(new Point(0, 0), container).Y; }
            catch (InvalidOperationException) { return; }

            var wasHidden = pill.Opacity < 0.5;
            var fromY = move.Y;
            move.Y = y;
            pill.Height = h;
            if (!On || wasHidden)
            {
                move.BeginAnimation(TranslateTransform.YProperty, null);
                pill.BeginAnimation(UIElement.OpacityProperty, On ? new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) : null);
                pill.Opacity = 1;
                return;
            }
            pill.BeginAnimation(UIElement.OpacityProperty, null);
            pill.Opacity = 1;
            if (Math.Abs(fromY - y) < 0.5) return;
            var slide = new DoubleAnimation(fromY, y, TimeSpan.FromMilliseconds(420))
                { EasingFunction = OutBackSoft, FillBehavior = FillBehavior.Stop };
            slide.Freeze();
            move.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private static readonly IEasingFunction OutBackSoft = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 });

        // ────────── иконки кнопок ──────────

        /// <summary>Наши трансформации содержимого кнопки: масштаб и поворот (вокруг центра иконки).</summary>
        private static (ScaleTransform Scale, RotateTransform Rotate)? GlyphTransforms(Control button)
        {
            if (FindChild<ContentPresenter>(button) is not { } presenter) return null;
            if (presenter.RenderTransform is TransformGroup { Children.Count: 2 } g &&
                g.Children[0] is ScaleTransform { IsFrozen: false } s && g.Children[1] is RotateTransform { IsFrozen: false } r)
                return (s, r);
            var scale = new ScaleTransform();
            var rotate = new RotateTransform();
            presenter.RenderTransform = new TransformGroup { Children = { scale, rotate } };
            presenter.RenderTransformOrigin = new Point(0.5, 0.5);
            return (scale, rotate);
        }

        /// <summary>Иконка сменилась (▶ ⇄ ❚❚): новая вкручивается из маленькой.</summary>
        public static void GlyphSwap(Control button)
        {
            if (!On || GlyphTransforms(button) is not { } t) return;
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(0.35, 1, 380, 0, OutBack));
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(0.35, 1, 380, 0, OutBack));
            t.Rotate.BeginAnimation(RotateTransform.AngleProperty, Tween(-90, 0, 380, 0, OutQuint));
        }

        /// <summary>Иконка делает оборот (повтор) или подпрыгивает (перемешивание).</summary>
        public static void GlyphSpin(Control button, double degrees)
        {
            if (!On || GlyphTransforms(button) is not { } t) return;
            if (degrees != 0)
                t.Rotate.BeginAnimation(RotateTransform.AngleProperty, Tween(-degrees, 0, 560, 0, OutBack));
            var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), OutCubic));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), Spring));
            a.Freeze();
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // ────────── лайк: вспышка ──────────

        /// <summary>Кольцо и брызги вокруг кнопки (рисуются на прозрачном слое поверх окна).</summary>
        public static void Burst(Canvas layer, FrameworkElement anchor, Color color, Color accent)
        {
            if (!On || !anchor.IsVisible || anchor.ActualWidth <= 0) return;
            Point c;
            try { c = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2), layer); }
            catch (InvalidOperationException) { return; }

            var made = new System.Collections.Generic.List<UIElement>();

            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 64, Height = 64, StrokeThickness = 2.5, IsHitTestVisible = false,
                Stroke = new SolidColorBrush(color), RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var ringScale = new ScaleTransform();
            ring.RenderTransform = ringScale;
            Canvas.SetLeft(ring, c.X - 32);
            Canvas.SetTop(ring, c.Y - 32);
            layer.Children.Add(ring);
            made.Add(ring);
            ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(0.25, 1.35, 520));
            ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(0.25, 1.35, 520));
            ring.BeginAnimation(UIElement.OpacityProperty, Tween(0.95, 0, 520));
            ring.Opacity = 0;

            const int dots = 10;
            for (int i = 0; i < dots; i++)
            {
                double angle = (i * 360.0 / dots + (i % 2 == 0 ? 0 : 14)) * Math.PI / 180;
                double dist = i % 2 == 0 ? 40 : 30;
                double size = i % 2 == 0 ? 6 : 4.5;
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = size, Height = size, IsHitTestVisible = false,
                    Fill = new SolidColorBrush(i % 3 == 0 ? accent : color),
                };
                var move = new TranslateTransform();
                dot.RenderTransform = move;
                Canvas.SetLeft(dot, c.X - size / 2);
                Canvas.SetTop(dot, c.Y - size / 2);
                layer.Children.Add(dot);
                made.Add(dot);
                move.BeginAnimation(TranslateTransform.XProperty, Tween(Math.Cos(angle) * 10, Math.Cos(angle) * dist, 560, 0, OutQuint));
                move.BeginAnimation(TranslateTransform.YProperty, Tween(Math.Sin(angle) * 10, Math.Sin(angle) * dist, 560, 0, OutQuint));
                dot.BeginAnimation(UIElement.OpacityProperty, Tween(1, 0, 380, 200));
                dot.Opacity = 0;
            }

            var cleanup = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            cleanup.Tick += (_, _) =>
            {
                cleanup.Stop();
                foreach (var el in made) layer.Children.Remove(el);
            };
            cleanup.Start();
        }

        // ────────── текст песни ──────────

        private static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.RegisterAttached(
            "ScrollOffset", typeof(double), typeof(Motion),
            new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

        /// <summary>Плавная прокрутка к offset (а не прыжком).</summary>
        public static void ScrollTo(ScrollViewer viewer, double offset, int ms = 520)
        {
            if (!On || Math.Abs(viewer.VerticalOffset - offset) < 1)
            {
                viewer.BeginAnimation(ScrollOffsetProperty, null);
                viewer.ScrollToVerticalOffset(offset);
                return;
            }
            // от того места, где сейчас стоит прокрутка: колесо мыши могло её сдвинуть
            var a = new DoubleAnimation(viewer.VerticalOffset, offset, TimeSpan.FromMilliseconds(ms)) { EasingFunction = OutCubic };
            a.Freeze();
            viewer.BeginAnimation(ScrollOffsetProperty, a);
        }

        /// <summary>Текущая строка текста чуть крупнее остальных (масштаб от левого края).</summary>
        public static void Emphasize(FrameworkElement el, bool on)
        {
            if (Transforms(el) is not { } t) return;
            el.RenderTransformOrigin = new Point(0, 0.5);
            var to = on ? 1.045 : 1;
            if (!On)
            {
                t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                t.Scale.ScaleX = t.Scale.ScaleY = to;
                return;
            }
            t.Scale.ScaleX = t.Scale.ScaleY = to;
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(on ? 420 : 320))
                { EasingFunction = on ? OutBack : OutCubic, FillBehavior = FillBehavior.Stop };
            a.Freeze();
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // ────────── очередь ──────────

        /// <summary>
        /// Обновилась очередь. Если голова ушла (трек сменился) — оставшиеся строки подъезжают вверх на её место;
        /// новые треки въезжают справа.
        /// </summary>
        public static void QueueChanged(ItemsControl list, System.Collections.Generic.IReadOnlyList<string> before,
                                        System.Collections.Generic.IReadOnlyList<string> after)
        {
            if (!On || !list.IsVisible || after.Count == 0) return;
            if (before.Count == 0) { Cascade(list, max: 10, step: 30); return; }

            // сколько строк ушло сверху: после — это хвост «до»
            int shift = 0;
            for (int k = 1; k <= Math.Min(3, before.Count); k++)
            {
                bool tail = true;
                for (int i = 0; i < Math.Min(after.Count, before.Count - k) && tail; i++)
                    tail = before[i + k] == after[i];
                if (tail && before.Count - k > 0) { shift = k; break; }
            }
            var known = new System.Collections.Generic.HashSet<string>(before);

            list.Dispatcher.InvokeAsync(() =>
            {
                double rowHeight = 0;
                for (int i = 0, shown = 0; i < after.Count && shown < 16; i++)
                {
                    if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row) break;
                    if (!row.IsVisible) continue;
                    shown++;
                    if (rowHeight <= 0) rowHeight = row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
                    if (!known.Contains(after[i]))
                    {
                        row.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, 300, shown * 30));
                        Slide(row, 36, 420, delay: shown * 30);
                    }
                    else if (shift > 0 && Transforms(row) is { } t)
                    {
                        t.Move.BeginAnimation(TranslateTransform.YProperty, Tween(rowHeight * shift, 0, 420, i * 18, OutQuint));
                    }
                }
            }, DispatcherPriority.Loaded);
        }

        private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                if (FindChild<T>(child) is { } deeper) return deeper;
            }
            return null;
        }
    }
}
