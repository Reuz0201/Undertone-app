using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// Комната «Слушать вместе», надстройки: реакции, режим «управляет только хозяин», предложить трек,
    /// кто поставил трек в очередь.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly string[] Reactions = { "fire", "heart", "laugh", "mind", "clap", "skull" };
        private readonly Dictionary<string, ImageSource> _reactionImages = new();
        private DateTime _lastReaction = DateTime.MinValue;
        /// <summary>Треки, которые хозяин только что добавил по чужому предложению: id → кто предложил.</summary>
        private readonly Dictionary<string, string> _suggestedBy = new();

        // ────────── хозяин ──────────

        private bool IsRoomHost => _friendsFeed?.Room is { } r && (r.Host.Length == 0 || r.Host == _friendsFeed.MemberId);

        /// <summary>Управляет только хозяин, он на месте, и это не мы.</summary>
        private bool RoomLocked
        {
            get
            {
                if (!_inRoom || _friendsFeed?.Room is not { Active: true, HostOnly: true } room) return false;
                if (room.Host == _friendsFeed.MemberId) return false;
                // хозяин ушёл — замок снимается сам, иначе комната застрянет
                return RoomFriends().Any(f => f.Member == room.Host);
            }
        }

        /// <summary>Кнопки «следующий», «назад», волна: в закрытой комнате ничего не делают.</summary>
        private bool RoomBlocksControl()
        {
            if (!RoomLocked) return false;
            ShowToast($"Сейчас управляет {Format.PublicName(_friendsFeed!.Room!.HostName)} — можно предложить трек", 3);
            return true;
        }

        /// <summary>В закрытой комнате выбор трека превращается в предложение хозяину.</summary>
        private bool RoomSuggestInstead(YaTrack track)
        {
            if (!RoomLocked || _friendsFeed is not { } feed) return false;
            feed.PublishEvent(new RoomEvent { Type = "suggest", Name = FriendsDisplayName, Track = track });
            ShowToast($"Предложил «{track.FullTitle}» — {Format.PublicName(feed.Room!.HostName)} добавит в очередь", 3);
            return true;
        }

        private void TogetherHostOnly_Changed(object sender, RoutedEventArgs e)
        {
            if (!_inRoom || !IsRoomHost || _friendsFeed?.Room is not { } room) return;
            var on = TogetherHostOnly.IsChecked == true;
            if (room.HostOnly == on) return;
            // меняем флаг и рассылаем как обычное изменение комнаты
            room.HostOnly = on;
            RoomPublishLocal();
            ShowToast(on ? "Теперь управляешь только ты, друзья предлагают треки" : "Управлять снова могут все");
        }

        /// <summary>Кто что поставил: прежние отметки сохраняем, новые треки в очереди — наши (или того, кто предложил).</summary>
        private Dictionary<string, string> NextAddedMap(RoomState? prev, YaTrack? track, IReadOnlyList<YaTrack> upcoming)
        {
            var result = new Dictionary<string, string>();
            // включили другой список целиком — это не «добавил», отметки начинаем заново
            if (prev == null || prev.Track?.Id != track?.Id && !prev.Queue.Any(t => t.Id == track?.Id)) return result;
            var known = prev.Queue.Select(t => t.Id).ToHashSet();
            if (prev.Track != null) known.Add(prev.Track.Id);
            var me = FriendsDisplayName;
            foreach (var t in upcoming)
            {
                if (_suggestedBy.Remove(t.Id, out var who)) result[t.Id] = who;
                else if (prev.Added.TryGetValue(t.Id, out var was)) result[t.Id] = was;
                else if (!known.Contains(t.Id)) result[t.Id] = me;
            }
            return result;
        }

        // ────────── события: реакции и предложения ──────────

        private void OnRoomEvent(RoomEvent ev) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (!_inRoom) return;
                switch (ev.Type)
                {
                    case "react" when Array.IndexOf(Reactions, ev.Emoji) >= 0:
                        ShowReaction(ev.Emoji, Format.PublicName(ev.Name));
                        break;
                    case "suggest" when ev.Track != null && IsRoomHost:
                        var name = Format.PublicName(ev.Name);
                        if (_queue.Current?.Id == ev.Track.Id || _queue.Upcoming(RoomQueueMax).Any(t => t.Id == ev.Track.Id))
                        {
                            ShowToast($"{name} предлагает «{ev.Track.FullTitle}» — он уже в очереди");
                            return;
                        }
                        _suggestedBy[ev.Track.Id] = name;
                        if (_queue.Current == null)
                        {
                            PlayNext(ev.Track);
                        }
                        else
                        {
                            _queue.AddToEnd(ev.Track);
                            QueueEdited();
                        }
                        ShowToast($"{name} предложил «{ev.Track.FullTitle}» — добавил в конец очереди", 3.5);
                        break;
                }
            });

        private void Reaction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string emoji } || _friendsFeed is not { } feed || !_inRoom) return;
            // не чаще трёх в секунду, чтобы не завалить брокер
            if (DateTime.UtcNow - _lastReaction < TimeSpan.FromMilliseconds(330)) return;
            _lastReaction = DateTime.UtcNow;
            feed.PublishEvent(new RoomEvent { Type = "react", Name = FriendsDisplayName, Emoji = emoji });
            ShowReaction(emoji, "");
        }

        private ImageSource? ReactionImage(string emoji)
        {
            if (_reactionImages.TryGetValue(emoji, out var cached)) return cached;
            try
            {
                var img = new BitmapImage(new Uri($"pack://application:,,,/Assets/Reactions/{emoji}.png"));
                img.Freeze();
                return _reactionImages[emoji] = img;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Реакция всплывает над обложкой и тает: эмодзи и имя того, кто её отправил.</summary>
        private void ShowReaction(string emoji, string name)
        {
            if (ReactionImage(emoji) is not { } image) return;
            _party?.ShowReaction(image, name);
            var layer = ReactionLayer;
            var w = Math.Max(80, layer.ActualWidth);
            var h = Math.Max(120, layer.ActualHeight);

            var bubble = new StackPanel { IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 1) };
            bubble.Children.Add(new Image { Source = image, Width = 46, Height = 46 });
            if (name.Length > 0)
                bubble.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x11, 0x15)),
                    CornerRadius = new CornerRadius(100),
                    Padding = new Thickness(8, 2, 8, 3),
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new TextBlock { Text = name, FontSize = 11, Foreground = Brushes.White },
                });
            var move = new TranslateTransform();
            var scale = new ScaleTransform(0.6, 0.6);
            bubble.RenderTransform = new TransformGroup { Children = { scale, move } };

            var x = Random.Shared.NextDouble() * (w - 70) + 6;
            Canvas.SetLeft(bubble, x);
            Canvas.SetTop(bubble, h - 70);
            layer.Children.Add(bubble);

            var dur = TimeSpan.FromMilliseconds(2400);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -(h * 0.75), dur) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, (Random.Shared.NextDouble() - 0.5) * 60, dur) { EasingFunction = ease });
            var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.6 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            var fade = new DoubleAnimationUsingKeyFrames { Duration = dur };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.6)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            fade.Completed += (_, _) => layer.Children.Remove(bubble);
            bubble.BeginAnimation(OpacityProperty, fade);
        }
    }
}
