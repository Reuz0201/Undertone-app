using System;
using System.Windows;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Трей: крестик сворачивает в трей, выход — из меню значка.</summary>
    public partial class MainWindow
    {
        private TrayIcon? _tray;
        private bool _exiting;

        private void InitTray()
        {
            _tray = new TrayIcon();
            _tray.ShowRequested += ShowFromTray;
            _tray.PlayPauseRequested += TogglePlay;
            _tray.NextRequested += () => _ = NextAsync(auto: false);
            _tray.PrevRequested += () => _ = PrevAsync();
            _tray.MiniRequested += ToggleMiniPlayer;
            _tray.ExitRequested += ExitApp;

            // выход из Windows / перезагрузка — закрываемся по-настоящему
            Application.Current.SessionEnding += (_, _) => _exiting = true;
        }

        private void HideToTray()
        {
            Hide();
            if (_settings.TrayHintShown) return;
            _settings.TrayHintShown = true;
            PersistSettings();
            _tray?.ShowHint("Undertone работает в трее",
                "Музыка продолжает играть. Открыть — клик по значку, выйти — правой кнопкой → «Выход».");
        }

        /// <summary>Показать окно (клик по значку в трее или повторный запуск exe).</summary>
        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // вытаскиваем поверх других окон, даже если Windows не даёт фокус
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void ExitApp()
        {
            _exiting = true;
            Application.Current.Shutdown();
        }

        private void UpdateTray() =>
            _tray?.SetTrack(_queue.Current?.ToString(), _isPlaying);
    }
}
