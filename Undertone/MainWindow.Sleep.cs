using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Undertone.Core;

namespace Undertone
{
    /// <summary>Таймер сна: через N минут или в конце текущего трека музыка плавно затихает и встаёт на паузу.</summary>
    public partial class MainWindow
    {
        private DispatcherTimer? _sleepTimer;
        private DateTime _sleepAt;
        /// <summary>Остановиться, когда доиграет этот трек.</summary>
        private string? _sleepAfterTrack;
        private bool _sleepFading;

        private void Sleep_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            if (SleepActive)
            {
                menu.Items.Add(Item(SleepStatus(), ""));
                menu.Items.Add(new Separator());
            }
            foreach (var minutes in new[] { 15, 30, 45, 60, 90 })
            {
                var m = minutes;
                menu.Items.Add(Item($"Через {m} мин", "", () => StartSleep(TimeSpan.FromMinutes(m))));
            }
            menu.Items.Add(Item("Когда закончится этот трек", "", () => StartSleepAfterTrack()));
            if (SleepActive)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Выключить таймер", "", () => { StopSleep(); ShowToast("Таймер сна выключен"); }));
            }
            OpenMenu(menu, (FrameworkElement)sender);
        }

        private bool SleepActive => _sleepTimer?.IsEnabled == true || _sleepAfterTrack != null;

        private string SleepStatus()
        {
            if (_sleepAfterTrack != null) return "Остановлю после этого трека";
            var left = _sleepAt - DateTime.Now;
            return left <= TimeSpan.Zero ? "Засыпаю…" : $"Остановлю через {Math.Ceiling(left.TotalMinutes)} мин";
        }

        private void StartSleep(TimeSpan after)
        {
            StopSleep();
            _sleepAt = DateTime.Now + after;
            _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sleepTimer.Tick += (_, _) => SleepTick();
            _sleepTimer.Start();
            UpdateSleepButton();
            ShowToast($"Музыка остановится в {_sleepAt:HH:mm}");
        }

        private void StartSleepAfterTrack()
        {
            StopSleep();
            if (_queue.Current is not { } t)
            {
                ShowToast("Сейчас ничего не играет");
                return;
            }
            _sleepAfterTrack = t.Id;
            // следим за позицией: за 8 секунд до конца начинаем затихать
            _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sleepTimer.Tick += (_, _) => SleepTick();
            _sleepTimer.Start();
            UpdateSleepButton();
            ShowToast($"Остановлю после «{t.FullTitle}»");
        }

        private void SleepTick()
        {
            if (_sleepFading) return;
            if (_sleepAfterTrack != null)
            {
                // трек сменился раньше (переключили) — останавливаемся на стыке
                if (_queue.Current?.Id != _sleepAfterTrack) { _ = FallAsleepAsync(TimeSpan.Zero); return; }
                var duration = _player.NaturalDuration ?? _queue.Current.Duration;
                if (_hasMedia && duration > TimeSpan.Zero && duration - _player.Position <= TimeSpan.FromSeconds(8))
                    _ = FallAsleepAsync(duration - _player.Position);
                return;
            }
            var left = _sleepAt - DateTime.Now;
            if (left <= TimeSpan.FromSeconds(12)) _ = FallAsleepAsync(TimeSpan.FromSeconds(12));
            else UpdateSleepButton();
        }

        /// <summary>Плавно убавляем громкость и ставим на паузу; громкость потом возвращается как была.</summary>
        private async System.Threading.Tasks.Task FallAsleepAsync(TimeSpan fade)
        {
            _sleepFading = true;
            _sleepTimer?.Stop();
            var start = VolumeSlider.Value;
            var steps = Math.Max(1, (int)(fade.TotalMilliseconds / 100));
            for (int i = 1; i <= steps && _isPlaying; i++)
            {
                _player.Volume = start * (1 - (double)i / steps);
                await System.Threading.Tasks.Task.Delay(100);
            }
            // в комнате засыпаем только сами: выходим, чтобы пауза не остановила музыку у друзей
            if (_inRoom) LeaveTogether(announce: true);
            if (_isPlaying) TogglePlay();
            _player.Volume = start;
            _sleepFading = false;
            StopSleep();
            ShowToast("Спокойной ночи — музыка на паузе", 4);
        }

        private void StopSleep()
        {
            _sleepTimer?.Stop();
            _sleepTimer = null;
            _sleepAfterTrack = null;
            UpdateSleepButton();
        }

        private void UpdateSleepButton()
        {
            var on = SleepActive;
            if (on) SleepBtn.SetResourceReference(ForegroundProperty, "LiveAccent");
            else SleepBtn.ClearValue(ForegroundProperty);
            SleepBtn.ToolTip = on ? SleepStatus() : "Таймер сна";
        }
    }
}
