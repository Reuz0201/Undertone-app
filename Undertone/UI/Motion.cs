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

        /// <summary>Страница выплывает, когда становится видимой; её списки появляются лесенкой.</summary>
        public static void PageOnShow(FrameworkElement page, params ItemsControl[] lists)
        {
            page.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is not true) return;
                Enter(page, rise: 18, ms: 320);
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

        /// <summary>Сменился трек: обложка встаёт на место из чуть меньшей, название и артисты поднимаются следом.</summary>
        public static void TrackChanged(UIElement cover, UIElement? smallCover, params UIElement[] texts)
        {
            if (!On) return;
            PopIn(cover, from: 0.9, ms: 520, fade: false);
            cover.BeginAnimation(UIElement.OpacityProperty, Tween(0.35, 1, 380));
            if (smallCover != null) PopIn(smallCover, from: 0.8, ms: 420);
            for (int i = 0; i < texts.Length; i++)
                Enter(texts[i], rise: 12, ms: 340, delay: 60 + i * 50);
        }

        /// <summary>Всплывающая подсказка снизу: поднимается вместе с проявлением.</summary>
        public static void ToastIn(UIElement toast)
        {
            if (!On || Transforms(toast) is not { } t) return;
            t.Move.BeginAnimation(TranslateTransform.YProperty, Tween(16, 0, 380, 0, OutBack));
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(0.96, 1, 300));
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(0.96, 1, 300));
        }
    }
}
