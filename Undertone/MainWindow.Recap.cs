using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// «Итоги» как Spotify Wrapped: истории на весь экран — минуты, артист и трек №1, топ-5, время суток,
    /// рекорды, вкус и итоговая карточка, которую можно сохранить картинкой и скинуть друзьям.
    /// </summary>
    public partial class MainWindow
    {
        private sealed record RecapCard(Func<FrameworkElement> Build, double Seconds);

        private List<PlayEvent> _recapLog = new();
        private Recap? _recap;
        private List<RecapCard> _recapCards = new();
        private int _recapIndex;
        private double _recapElapsed;
        private bool _recapPaused;
        private DateTime _recapLastTick;
        private DispatcherTimer? _recapTimer;
        private readonly List<ScaleTransform> _recapFills = new();
        private Color _recapA = Color.FromRgb(0x6C, 0x4B, 0xF0), _recapB = Color.FromRgb(0xF0, 0x4B, 0x8E);
        private FrameworkElement? _recapShareCard;

        private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

        // ────────── запись прослушиваний ──────────

        /// <summary>Трек доиграл или его переключили: в журнал итогов — сколько реально звучало (от 30 секунд).</summary>
        private void LogPlayForRecap(YaTrack track, double positionSeconds)
        {
            if (_account == null || positionSeconds <= 0) return;
            // на скорости 0.85× трек звучит дольше, чем идёт его позиция — считаем реальные минуты
            var seconds = (int)Math.Round(positionSeconds / Math.Max(0.1, _player.Effects.EffectiveSpeed));
            if (seconds < PlayLog.MinSeconds) return;
            PlayLog.Append(_account.Uid, new PlayEvent(_reportStartedAt == default ? DateTime.Now : _reportStartedAt, track.Id, seconds));
        }

        /// <summary>В первые дни месяца — напоминание, что готовы итоги прошлого (один раз).</summary>
        private void MaybeAnnounceRecap()
        {
            if (_account == null) return;
            var now = DateTime.Now;
            if (now.Day > 7) return;
            var prev = RecapPeriod.Month(now.AddMonths(-1));
            var key = $"{_account.Uid}:{prev.Id}";
            if (_settings.RecapAnnounced == key) return;
            var plays = PlayLog.Load(_account.Uid).Count(e => prev.Contains(e.At) && e.Seconds >= PlayLog.MinSeconds);
            if (plays < 20) return;
            _settings.RecapAnnounced = key;
            PersistSettings();
            ShowToast($"Готовы итоги: {prev.Title.ToLower(RuCulture)} — открой «Итоги» слева (Y)", 6);
        }

        // ────────── открыть / закрыть ──────────

        private void Recap_Click(object sender, RoutedEventArgs e) => OpenRecap();

        private void OpenRecap()
        {
            if (_account == null)
            {
                ShowToast("Сначала войди в аккаунт");
                return;
            }
            _recapLog = PlayLog.Load(_account.Uid);
            var periods = AvailablePeriods(out var preferred);
            BuildPeriodPills(periods, preferred);

            RecapOverlay.Opacity = 0;
            RecapOverlay.Visibility = Visibility.Visible;
            RecapOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
            RecapOverlay.Focus();
            StartBlobs();
            ShowRecapFor(preferred);
        }

        private void CloseRecap()
        {
            if (RecapOverlay.Visibility != Visibility.Visible) return;
            _recapTimer?.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) =>
            {
                RecapOverlay.BeginAnimation(OpacityProperty, null);
                RecapOverlay.Visibility = Visibility.Collapsed;
                RecapStage.Content = null;
                _recapShareCard = null;
                RecapBlobAMove.BeginAnimation(TranslateTransform.XProperty, null);
                RecapBlobAMove.BeginAnimation(TranslateTransform.YProperty, null);
                RecapBlobBMove.BeginAnimation(TranslateTransform.XProperty, null);
                RecapBlobBMove.BeginAnimation(TranslateTransform.YProperty, null);
            };
            RecapOverlay.BeginAnimation(OpacityProperty, fade);
        }

        private void RecapClose_Click(object sender, RoutedEventArgs e) => CloseRecap();

        /// <summary>Клавиши, пока открыты итоги. true — обработано.</summary>
        private bool HandleRecapKey(Key key)
        {
            if (RecapOverlay.Visibility != Visibility.Visible) return false;
            switch (key)
            {
                case Key.Escape: CloseRecap(); return true;
                case Key.Right: case Key.PageDown: RecapGo(+1); return true;
                case Key.Left: case Key.PageUp: RecapGo(-1); return true;
                case Key.Space: _recapPaused = !_recapPaused; return true;
                default: return false;
            }
        }

        private void RecapOverlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // кнопки и переключатели периода — сами по себе, не листают
            for (var d = e.OriginalSource as DependencyObject; d != null && d != RecapOverlay;
                 d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ButtonBase) return;
            var x = e.GetPosition(RecapOverlay).X;
            RecapGo(x < RecapOverlay.ActualWidth * 0.33 ? -1 : +1);
        }

        // ────────── периоды ──────────

        private List<RecapPeriod> AvailablePeriods(out RecapPeriod preferred)
        {
            var now = DateTime.Now;
            var thisMonth = RecapPeriod.Month(now);
            var prevMonth = RecapPeriod.Month(now.AddMonths(-1));
            var thisYear = RecapPeriod.Year(now.Year);
            int PlaysIn(RecapPeriod p) => _recapLog.Count(e => p.Contains(e.At) && e.Seconds >= PlayLog.MinSeconds);

            var list = new List<RecapPeriod> { thisMonth };
            var prevPlays = PlaysIn(prevMonth);
            if (prevPlays > 0) list.Insert(0, prevMonth);
            if (PlaysIn(thisYear) > PlaysIn(thisMonth)) list.Add(thisYear);
            list.Add(RecapPeriod.AllTime);

            preferred = now.Day <= 7 && prevPlays >= 10 ? prevMonth
                : PlaysIn(thisMonth) >= 5 ? thisMonth
                : RecapPeriod.AllTime;
            return list;
        }

        private void BuildPeriodPills(List<RecapPeriod> periods, RecapPeriod selected)
        {
            RecapPeriods.Children.Clear();
            foreach (var period in periods)
            {
                var p = period;
                var pill = new RadioButton
                {
                    Style = (Style)FindResource("ChoicePill"),
                    GroupName = "RecapPeriod",
                    Content = p.Id == RecapPeriod.Month(DateTime.Now).Id ? "Этот месяц" : p.Title,
                    IsChecked = p.Id == selected.Id,
                    Margin = new Thickness(4, 0, 4, 0),
                };
                pill.Checked += (_, _) => ShowRecapFor(p);
                RecapPeriods.Children.Add(pill);
            }
        }

        private void ShowRecapFor(RecapPeriod period)
        {
            _recap = period.Id == RecapPeriod.AllTime.Id
                ? Recap.FromStats(_stats)
                : Recap.FromLog(_recapLog, period, id => _stats.Get(id)?.Track, id => _stats.Get(id)?.First);
            _recapCards = BuildCards(_recap);
            BuildSegments();
            PickColors(_recap);
            _recapPaused = false;
            ShowCard(0, 0);

            _recapTimer ??= CreateRecapTimer();
            _recapLastTick = DateTime.UtcNow;
            _recapTimer.Start();
        }

        // ────────── листание и прогресс ──────────

        private DispatcherTimer CreateRecapTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += (_, _) =>
            {
                var now = DateTime.UtcNow;
                var dt = (now - _recapLastTick).TotalSeconds;
                _recapLastTick = now;
                if (_recapPaused || _recapIndex >= _recapCards.Count) return;
                var card = _recapCards[_recapIndex];
                if (card.Seconds <= 0) return; // последняя карточка стоит, пока не закроют
                _recapElapsed += dt;
                if (_recapIndex < _recapFills.Count) _recapFills[_recapIndex].ScaleX = Math.Min(1, _recapElapsed / card.Seconds);
                if (_recapElapsed >= card.Seconds) RecapGo(+1);
            };
            return timer;
        }

        private void RecapGo(int delta)
        {
            var next = _recapIndex + delta;
            if (next < 0) next = 0;
            if (next >= _recapCards.Count)
            {
                // дальше последней — закрываем, как в историях
                if (delta > 0 && _recapIndex == _recapCards.Count - 1) CloseRecap();
                return;
            }
            ShowCard(next, delta);
        }

        private void BuildSegments()
        {
            RecapSegments.Children.Clear();
            _recapFills.Clear();
            foreach (var _ in _recapCards)
            {
                var scale = new ScaleTransform(0, 1);
                var fill = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(2), RenderTransform = scale };
                var track = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(3, 0, 3, 0),
                    Child = fill,
                    ClipToBounds = true,
                };
                _recapFills.Add(scale);
                RecapSegments.Children.Add(track);
            }
            RecapSegments.Visibility = _recapCards.Count > 1 ? Visibility.Visible : Visibility.Hidden;
        }

        private void ShowCard(int index, int direction)
        {
            _recapIndex = index;
            _recapElapsed = 0;
            for (int i = 0; i < _recapFills.Count; i++)
                _recapFills[i].ScaleX = i < index ? 1 : i == index && _recapCards[i].Seconds <= 0 ? 1 : 0;

            var content = _recapCards[index].Build();
            RecapStage.Content = content;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            RecapStageMove.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(direction * 60, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
            if (content is Panel panel) Stagger(panel);
        }

        /// <summary>Строчки карточки выезжают по очереди снизу.</summary>
        private static void Stagger(Panel panel)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var i = 0;
            foreach (UIElement child in panel.Children)
            {
                var begin = TimeSpan.FromMilliseconds(80 + i * 110);
                var move = new TranslateTransform(0, 26);
                child.RenderTransform = move;
                child.Opacity = 0;
                child.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = begin });
                move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(520)) { BeginTime = begin, EasingFunction = ease });
                i++;
            }
        }

        // ────────── живой фон ──────────

        private void PickColors(Recap recap)
        {
            _recapA = (TryFindResource("LiveAccent") as SolidColorBrush)?.Color ?? Color.FromRgb(0x6C, 0x4B, 0xF0);
            _recapB = Shift(_recapA);
            ApplyBlobColors();

            var url = recap.TopTracks.FirstOrDefault()?.Track.CoverUrl(200);
            if (Covers.Load(url, 200) is not BitmapSource image) return;
            void Use(BitmapSource img)
            {
                var palette = CoverPalette.PaletteFromImage(img);
                if (palette.Length == 0) return;
                _recapA = Vivid(palette[0]);
                _recapB = Vivid(palette.Length > 1 ? palette[1] : Shift(palette[0]));
                ApplyBlobColors();
            }
            if (image is BitmapImage { IsDownloading: true } downloading)
                downloading.DownloadCompleted += (_, _) => { if (RecapOverlay.IsVisible) Use(downloading); };
            else Use(image);
        }

        private void ApplyBlobColors()
        {
            RecapBlobA.Fill = Blob(_recapA);
            RecapBlobB.Fill = Blob(_recapB);
        }

        private static Brush Blob(Color c) => new RadialGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(0xC0, c.R, c.G, c.B), 0),
                new(Color.FromArgb(0x50, c.R, c.G, c.B), 0.45),
                new(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
            });

        /// <summary>Тусклые цвета обложки → поярче, чтобы фон «светился».</summary>
        private static Color Vivid(Color c)
        {
            var max = Math.Max(c.R, Math.Max(c.G, c.B));
            if (max == 0) return Color.FromRgb(0x6C, 0x4B, 0xF0);
            var k = 230.0 / max;
            return Color.FromRgb((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k));
        }

        /// <summary>Второй цвет, если у обложки он один: тот же, повёрнутый по кругу.</summary>
        private static Color Shift(Color c) => Color.FromRgb(c.B, c.R, c.G);

        private void StartBlobs()
        {
            void Float(TranslateTransform t, DependencyProperty p, double by, double seconds) =>
                t.BeginAnimation(p, new DoubleAnimation(-by, by, TimeSpan.FromSeconds(seconds))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            Float(RecapBlobAMove, TranslateTransform.XProperty, 120, 11);
            Float(RecapBlobAMove, TranslateTransform.YProperty, 80, 14);
            Float(RecapBlobBMove, TranslateTransform.XProperty, 140, 13);
            Float(RecapBlobBMove, TranslateTransform.YProperty, 100, 9);
        }

        // ────────── карточки ──────────

        private List<RecapCard> BuildCards(Recap r)
        {
            var cards = new List<RecapCard>();
            if (r.IsEmpty)
            {
                cards.Add(new RecapCard(() => Card(
                    Label("ИТОГИ · " + r.Period.Title.ToUpper(RuCulture)),
                    Huge("Пока тихо", 54),
                    Body(r.Period.Id == RecapPeriod.AllTime.Id
                        ? "Включи что-нибудь — итоги начнут собираться с первого трека."
                        : "За этот период ещё мало музыки. Слушай дальше — итоги соберутся сами. А пока загляни во «Всё время»."))
                    , 0));
                return cards;
            }

            cards.Add(new RecapCard(() => IntroCard(r), 5));
            cards.Add(new RecapCard(() => MinutesCard(r), 7));
            if (r.TopArtists.Count > 0) cards.Add(new RecapCard(() => TopArtistCard(r), 7));
            if (r.TopArtists.Count > 1) cards.Add(new RecapCard(() => ListCard("Твои артисты", r.TopArtists.Select(a =>
                (a.TopTrack, a.Name, $"{a.Minutes} мин · {a.Plays} {Recap.Plural(a.Plays, "раз", "раза", "раз")}", true)).ToList()), 9));
            if (r.TopTracks.Count > 0) cards.Add(new RecapCard(() => TopTrackCard(r), 7));
            if (r.TopTracks.Count > 1) cards.Add(new RecapCard(() => ListCard("Твои треки", r.TopTracks.Select(t =>
                (t.Track, t.Track.FullTitle, $"{t.Track.ArtistsText} · {t.Plays} {Recap.Plural(t.Plays, "раз", "раза", "раз")}", false)).ToList(),
                playTop: true), 9));
            if (r.Hours.Sum() > 0) cards.Add(new RecapCard(() => HoursCard(r), 8));
            if (r.Exact && r.BestDay != null) cards.Add(new RecapCard(() => RecordsCard(r), 7));
            if (r.DistinctTracks >= 5) cards.Add(new RecapCard(() => TasteCard(r), 6));
            cards.Add(new RecapCard(() => SummaryCard(r), 0));
            return cards;
        }

        private FrameworkElement IntroCard(Recap r)
        {
            var covers = new Grid { Height = 210, Margin = new Thickness(0, 0, 0, 26), HorizontalAlignment = HorizontalAlignment.Center, Width = 420 };
            var tracks = r.TopTracks.Take(5).ToList();
            for (int i = 0; i < tracks.Count; i++)
            {
                var offset = (i - (tracks.Count - 1) / 2.0);
                var cover = Cover(tracks[i].Track, 150, 16);
                cover.HorizontalAlignment = HorizontalAlignment.Center;
                cover.VerticalAlignment = VerticalAlignment.Center;
                var tg = new TransformGroup();
                tg.Children.Add(new RotateTransform(offset * 8, 75, 75));
                tg.Children.Add(new TranslateTransform(offset * 62, Math.Abs(offset) * 12));
                cover.RenderTransform = tg;
                Panel.SetZIndex(cover, 10 - (int)Math.Abs(offset * 2));
                covers.Children.Add(cover);
            }
            return Card(
                covers,
                Label("UNDERTONE · ИТОГИ"),
                Huge(r.Period.Title, 60),
                Body(r.Period.Id == RecapPeriod.AllTime.Id
                    ? "Всё, что ты слушал в Undertone. Поехали!"
                    : "Давай посмотрим, как звучал этот период. Поехали!"));
        }

        private FrameworkElement MinutesCard(Recap r)
        {
            var number = Huge("0", 108);
            number.Foreground = AccentText();
            CountUp(number, r.Minutes);
            return Card(
                Label("ТЫ СЛУШАЛ МУЗЫКУ"),
                number,
                Huge(Recap.Plural(r.Minutes, "минуту", "минуты", "минут") + (r.Exact ? "" : " (примерно)"), 30),
                Body(r.MinutesComparison()),
                Chips(
                    $"{r.Plays} {Recap.Plural(r.Plays, "прослушивание", "прослушивания", "прослушиваний")}",
                    $"{r.DistinctTracks} {Recap.Plural(r.DistinctTracks, "трек", "трека", "треков")}",
                    $"{r.DistinctArtists} {Recap.Plural(r.DistinctArtists, "артист", "артиста", "артистов")}"));
        }

        private FrameworkElement TopArtistCard(Recap r)
        {
            var a = r.TopArtists[0];
            var cover = Cover(a.TopTrack, 230, 115);
            cover.Margin = new Thickness(0, 0, 0, 22);
            return Card(
                Label("ТВОЙ АРТИСТ №1"),
                cover,
                Huge(a.Name, 52),
                Body($"{a.Minutes} {Recap.Plural(a.Minutes, "минута", "минуты", "минут")} · {a.Plays} {Recap.Plural(a.Plays, "прослушивание", "прослушивания", "прослушиваний")}"),
                Body(r.Minutes > 0 ? $"Это {Math.Round(100.0 * a.Minutes / r.Minutes)}% всей твоей музыки." : ""));
        }

        private FrameworkElement TopTrackCard(Recap r)
        {
            var t = r.TopTracks[0];
            var cover = Cover(t.Track, 250, 22);
            cover.Margin = new Thickness(0, 0, 0, 22);
            var play = Pill("▶  Включить", async () =>
            {
                await PlayFromListAsync(new[] { new TrackItem(t.Track, 1) }, 0, QueueSource.Collection, "ИЗ ИТОГОВ");
            });
            return Card(
                Label("ТРЕК, КОТОРЫЙ ТЫ ЗАСЛУШАЛ"),
                cover,
                Huge(t.Track.FullTitle, 44),
                Body(t.Track.ArtistsText),
                Body($"{t.Plays} {Recap.Plural(t.Plays, "раз", "раза", "раз")} · {t.Minutes} {Recap.Plural(t.Minutes, "минута", "минуты", "минут")}"),
                play);
        }

        private FrameworkElement ListCard(string title, List<(YaTrack Cover, string Title, string Sub, bool Round)> rows, bool playTop = false)
        {
            var panel = Card(Label(title.ToUpper(RuCulture)));
            for (int i = 0; i < rows.Count; i++)
            {
                var (track, name, sub, round) = rows[i];
                var row = new Grid { Margin = new Thickness(0, 8, 0, 0), Width = 480 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var num = new TextBlock
                {
                    Text = (i + 1).ToString(), FontSize = i == 0 ? 34 : 26, FontWeight = FontWeights.Bold,
                    Foreground = i == 0 ? AccentText() : Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                };
                var cover = Cover(track, 56, round ? 28 : 8);
                Grid.SetColumn(cover, 1);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock
                {
                    Text = name, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                text.Children.Add(new TextBlock
                {
                    Text = sub, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
                });
                Grid.SetColumn(text, 2);
                row.Children.Add(num);
                row.Children.Add(cover);
                row.Children.Add(text);
                panel.Children.Add(row);
            }
            if (playTop) panel.Children.Add(PlayTopButton());
            return panel;
        }

        private FrameworkElement HoursCard(Recap r)
        {
            var (title, text) = r.Chronotype();
            var peak = r.PeakHour();
            var max = Math.Max(1, r.Hours.Max());
            var chart = new Grid { Height = 150, Width = 480, Margin = new Thickness(0, 18, 0, 6) };
            for (int h = 0; h < 24; h++)
            {
                chart.ColumnDefinitions.Add(new ColumnDefinition());
                var near = Math.Min(Math.Abs(h - peak), 24 - Math.Abs(h - peak)) <= 1;
                var scale = new ScaleTransform(1, 0);
                var bar = new Border
                {
                    Height = Math.Max(4, 150.0 * r.Hours[h] / max),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(2, 0, 2, 0),
                    Background = near ? AccentText() : new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = scale,
                    ToolTip = $"{h:00}:00 — {r.Hours[h]} мин",
                };
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600))
                {
                    BeginTime = TimeSpan.FromMilliseconds(500 + h * 30),
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
                });
                Grid.SetColumn(bar, h);
                chart.Children.Add(bar);
            }
            var axis = new Grid { Width = 480 };
            foreach (var (label, align) in new[] { ("0:00", HorizontalAlignment.Left), ("6:00", HorizontalAlignment.Left),
                         ("12:00", HorizontalAlignment.Center), ("18:00", HorizontalAlignment.Right), ("24:00", HorizontalAlignment.Right) })
            {
                var t = new TextBlock { Text = label, FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = align };
                if (label == "6:00") t.Margin = new Thickness(480 / 4.0 - 14, 0, 0, 0);
                if (label == "18:00") t.Margin = new Thickness(0, 0, 480 / 4.0 - 18, 0);
                axis.Children.Add(t);
            }
            return Card(
                Label("КОГДА ТЫ СЛУШАЕШЬ"),
                Huge(title, 54),
                Body(text + $" Самое музыкальное время — около {peak}:00."),
                chart,
                axis);
        }

        private FrameworkElement RecordsCard(Recap r)
        {
            var day = r.BestDay!.Value;
            var dayText = day.ToString("d MMMM", RuCulture);
            var stats = new UniformGrid { Columns = 3, Width = 520, Margin = new Thickness(0, 26, 0, 0) };
            stats.Children.Add(Stat(r.BestDayMinutes.ToString(), "минут в рекордный день"));
            stats.Children.Add(Stat(r.Streak.ToString(), $"{Recap.Plural(r.Streak, "день", "дня", "дней")} подряд с музыкой"));
            stats.Children.Add(Stat(r.ActiveDays.ToString(), $"{Recap.Plural(r.ActiveDays, "день", "дня", "дней")} с музыкой всего"));
            return Card(
                Label("РЕКОРДЫ"),
                Huge(dayText, 60),
                Body($"Твой самый музыкальный день — {day.ToString("dddd", RuCulture)}, {dayText}. " +
                     $"{r.BestDayMinutes} {Recap.Plural(r.BestDayMinutes, "минута", "минуты", "минут")} музыки за сутки."),
                stats);
        }

        private FrameworkElement TasteCard(Recap r)
        {
            var (title, text) = r.Taste();
            var newPart = r.Exact && r.NewTracks > 0
                ? Chips($"{r.NewTracks} {Recap.Plural(r.NewTracks, "новый трек", "новых трека", "новых треков")}",
                        $"{r.DistinctArtists} {Recap.Plural(r.DistinctArtists, "артист", "артиста", "артистов")}")
                : Chips($"{r.DistinctTracks} {Recap.Plural(r.DistinctTracks, "трек", "трека", "треков")}",
                        $"{r.DistinctArtists} {Recap.Plural(r.DistinctArtists, "артист", "артиста", "артистов")}");
            return Card(Label("ТВОЙ ВКУС"), Huge(title, 60), Body(text), newPart);
        }

        /// <summary>Итоговая карточка — её можно сохранить картинкой и отправить друзьям.</summary>
        private FrameworkElement SummaryCard(Recap r)
        {
            var dim = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
            var share = new StackPanel();
            var grad = new LinearGradientBrush(Darken(_recapA, 0.55), Darken(_recapB, 0.35), 45);
            var card = new Border
            {
                Width = 440,
                CornerRadius = new CornerRadius(26),
                Padding = new Thickness(30, 26, 30, 24),
                Background = grad,
                Child = share,
                Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = 0.5 },
            };
            share.Children.Add(new TextBlock { Text = "UNDERTONE · ИТОГИ", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = dim });
            share.Children.Add(new TextBlock { Text = r.Period.Title, FontSize = 32, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 14) });

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition());
            var mins = new StackPanel();
            mins.Children.Add(new TextBlock { Text = "Минуты", FontSize = 12.5, Foreground = dim });
            mins.Children.Add(new TextBlock { Text = r.Minutes.ToString("N0", RuCulture), FontSize = 34, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            var artist = new StackPanel();
            artist.Children.Add(new TextBlock { Text = "Артист №1", FontSize = 12.5, Foreground = dim });
            artist.Children.Add(new TextBlock
            {
                Text = r.TopArtists.FirstOrDefault()?.Name ?? "—", FontSize = 24, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0),
            });
            Grid.SetColumn(artist, 1);
            head.Children.Add(mins);
            head.Children.Add(artist);
            share.Children.Add(head);

            share.Children.Add(new TextBlock { Text = "Топ треков", FontSize = 12.5, Foreground = dim, Margin = new Thickness(0, 18, 0, 6) });
            for (int i = 0; i < r.TopTracks.Count; i++)
            {
                var t = r.TopTracks[i];
                var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = (i + 1).ToString(), FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
                var cover = Cover(t.Track, 34, 6);
                cover.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetColumn(cover, 1);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = t.Track.FullTitle, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = t.Track.ArtistsText, FontSize = 12, Foreground = dim, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(text, 2);
                row.Children.Add(cover);
                row.Children.Add(text);
                share.Children.Add(row);
            }

            var (chrono, _) = r.Chronotype();
            var (taste, _) = r.Taste();
            share.Children.Add(Chips(new[] { chrono, r.DistinctTracks >= 5 ? taste : "" }.Where(s => s.Length > 0).ToArray(), compact: true));
            _recapShareCard = card;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            buttons.Children.Add(Pill("Сохранить картинку", SaveRecapImage, primary: true));
            if (r.TopTracks.Count > 0) buttons.Children.Add(PlayTopButton(inline: true));
            buttons.Children.Add(Pill("Закрыть", CloseRecap));

            var outer = Card(card, buttons);
            return outer;
        }

        // ────────── сохранить картинкой ──────────

        private void SaveRecapImage()
        {
            if (_recapShareCard is not { ActualWidth: > 0 } card || _recap == null) return;
            try
            {
                const double scale = 2; // чётко на телефоне
                var w = card.ActualWidth;
                var h = card.ActualHeight;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), null, new Rect(0, 0, w + 48, h + 48));
                    dc.DrawRectangle(new VisualBrush(card) { Stretch = Stretch.None }, null, new Rect(24, 24, w, h));
                }
                var bmp = new RenderTargetBitmap((int)((w + 48) * scale), (int)((h + 48) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bmp.Render(visual);
                bmp.Freeze();

                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Undertone");
                Directory.CreateDirectory(dir);
                var name = string.Concat($"Итоги — {_recap.Period.Title}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var path = Path.Combine(dir, name + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(path)) encoder.Save(fs);

                var copied = false;
                try { Clipboard.SetImage(bmp); copied = true; }
                catch (Exception ex) { Log.Warn("Картинка итогов не скопирована в буфер", ex); }
                ShowToast(copied
                    ? "Картинка в буфере обмена — вставь в чат (Ctrl+V). Копия в «Изображения\\Undertone»"
                    : "Сохранено в «Изображения\\Undertone»", 5);
            }
            catch (Exception ex)
            {
                Log.Error("Картинка итогов не сохранилась", ex);
                ShowToast("Не получилось сохранить картинку");
            }
        }

        // ────────── кирпичики ──────────

        private static StackPanel Card(params FrameworkElement[] children)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var c in children)
            {
                if (c.HorizontalAlignment == HorizontalAlignment.Stretch) c.HorizontalAlignment = HorizontalAlignment.Center;
                panel.Children.Add(c);
            }
            return panel;
        }

        private static TextBlock Label(string text) => new()
        {
            Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center,
        };

        private static TextBlock Huge(string text, double size) => new()
        {
            Text = text, FontSize = size, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 600, LineHeight = size * 1.08, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };

        private static TextBlock Body(string text) => new()
        {
            Text = text, FontSize = 16.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)), MaxWidth = 520,
            Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
        };

        private Brush AccentText() => new SolidColorBrush(Vivid(_recapA));

        private static FrameworkElement Stat(string value, string caption)
        {
            var p = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
            p.Children.Add(new TextBlock { Text = value, FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            p.Children.Add(new TextBlock
            {
                Text = caption, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            });
            return p;
        }

        private static FrameworkElement Chips(params string[] items) => Chips(items, compact: false);

        private static FrameworkElement Chips(string[] items, bool compact)
        {
            var wrap = new WrapPanel { HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center, Margin = new Thickness(0, compact ? 10 : 22, 0, 0) };
            foreach (var item in items)
                wrap.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(100),
                    Padding = compact ? new Thickness(10, 4, 10, 4) : new Thickness(14, 7, 14, 7),
                    Margin = new Thickness(0, 0, 8, 8),
                    Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                    Child = new TextBlock { Text = item, FontSize = compact ? 12 : 14, Foreground = Brushes.White },
                });
            return wrap;
        }

        private static Border Cover(YaTrack track, double size, double radius)
        {
            var image = Covers.Load(track.CoverUrl(size > 120 ? 400 : 200), size > 120 ? 400 : 200);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = image != null
                    ? new ImageBrush(image) { Stretch = Stretch.UniformToFill }
                    : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Effect = size > 100 ? new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.5 } : null,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        private Button Pill(string text, Action click, bool primary = false)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)FindResource(primary ? "PillButton" : "PillSecondary"),
                Margin = new Thickness(5, 14, 5, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            b.Click += (_, _) => click();
            return b;
        }

        private Button PlayTopButton(bool inline = false)
        {
            var b = Pill("▶  Включить топ", async () =>
            {
                if (_recap == null) return;
                var items = _recap.TopTracks.Select((t, i) => new TrackItem(t.Track, i + 1)).ToList();
                if (items.Count == 0) return;
                await PlayFromListAsync(items, 0, QueueSource.Collection, "ИЗ ИТОГОВ");
                ShowToast("Играет твой топ");
            }, primary: !inline);
            if (!inline) b.Margin = new Thickness(0, 22, 0, 0);
            return b;
        }

        /// <summary>Число набегает от 0 — как в Wrapped.</summary>
        private static void CountUp(TextBlock target, int value)
        {
            var start = DateTime.UtcNow + TimeSpan.FromMilliseconds(350);
            const double duration = 1.6;
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var t = Math.Clamp((DateTime.UtcNow - start).TotalSeconds / duration, 0, 1);
                var eased = 1 - Math.Pow(1 - t, 3);
                target.Text = ((int)Math.Round(value * eased)).ToString("N0", RuCulture);
                if (t >= 1 || !target.IsVisible && t > 0) timer.Stop();
            };
            timer.Start();
        }

        private static Color Darken(Color c, double k) =>
            Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
    }
}
