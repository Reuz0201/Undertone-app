using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Undertone.Core;

namespace Undertone
{
    /// <summary>
    /// Spotify Soloist: официальный консольный плеер Spotify вместо встроенного (WebView2).
    /// Окно владеет процессом Soloist (он живёт, пока открыт Undertone), плеер Spotify только играет через него.
    /// </summary>
    public partial class MainWindow
    {
        private SoloistDaemon? _soloist;
        private bool _soloistWasLoggedIn;
        private string? _soloistVersion;
        private bool _soloistInstalling;

        private string? SoloistKey() => SecretProtector.Unprotect(_env.Get(SoloistDaemon.KeyEnvName));

        /// <summary>Soloist для плеера Spotify: включён, скачан и есть ключ — иначе null (играем как раньше).</summary>
        private SoloistDaemon? GetSoloist()
        {
            if (!_settings.SoloistEnabled || _soloistInstalling || !SoloistDaemon.IsInstalled) return null;
            if (_soloist != null) return _soloist;
            var key = SoloistKey();
            if (string.IsNullOrWhiteSpace(key)) return null;
            _soloist = new SoloistDaemon(key, Dispatcher, text => ShowToast(text, 4));
            _soloist.Changed += OnSoloistChanged;
            _soloistWasLoggedIn = false;
            return _soloist;
        }

        private void DisposeSoloist()
        {
            if (_soloist == null) return;
            _soloist.Changed -= OnSoloistChanged;
            _soloist.Dispose();
            _soloist = null;
            _soloistWasLoggedIn = false;
        }

        /// <summary>Запуск заранее — чтобы устройство «Undertone» сразу появилось в Spotify и его можно было выбрать.</summary>
        private async Task StartSoloistAsync()
        {
            var d = GetSoloist();
            if (d == null) return;
            try
            {
                await d.StartAsync(CancellationToken.None);
                if (!d.State.LoggedIn) ShowToast(new SoloistNotPairedException().Message, 7);
            }
            catch (Exception ex)
            {
                Log.Warn("Soloist: не запустился", ex);
                ShowToast("Soloist: " + ex.Message, 4);
            }
            UpdateSoloistStatus();
        }

        private void OnSoloistChanged(SoloistChange change)
        {
            if (!change.HasFlag(SoloistChange.Auth) || _soloist == null) return;
            var logged = _soloist.State.LoggedIn;
            if (logged && !_soloistWasLoggedIn)
            {
                ShowToast("Spotify Soloist подключён — теперь Spotify играет через него, без браузера", 4);
                // сейчас играет трек Spotify через встроенный плеер или Connect — переезжаем на Soloist с того же места
                if (_service == MusicServiceKind.Spotify && _player.IsExternal && _isPlaying &&
                    _spotifyPlayer is { UsesSoloist: false } && _queue.Current is { } track)
                {
                    _resumeAt = (track.Id, _player.Position);
                    _ = PlayCurrentAsync();
                }
            }
            _soloistWasLoggedIn = logged;
            UpdateSoloistStatus();
        }

        /// <summary>
        /// Поменяли включение, ключ или сборку: старый процесс закрываем.
        /// Если звук шёл через Soloist — трек перезапускается с того же места по-новому (или ждёт ▶, если стоял на паузе).
        /// </summary>
        private void ApplySoloistChange()
        {
            var current = _queue.Current;
            var position = _player.Position;
            var playing = _isPlaying;
            var restart = _spotifyPlayer?.ResetSoloist() == true;
            DisposeSoloist();
            if (!restart || current == null) return;

            _player.Close();
            _hasMedia = false;
            _isPlaying = false;
            _resumeAt = (current.Id, position);
            UpdatePlayState();
            if (playing) _ = PlayCurrentAsync();
        }

        // ────────── страница «Настройки» ──────────

        private void FillSoloistSettings()
        {
            SoloistToggle.IsChecked = _settings.SoloistEnabled;
            SoloistKeyBox.Text = "";
            SoloistKeyBox.Tag = SoloistKey() != null
                ? "Ключ сохранён — вставьте новый, чтобы заменить"
                : "Вставьте ключ со страницы Spotify for Developers";
            UpdateSoloistStatus();
            if (SoloistDaemon.IsInstalled && _soloistVersion == null) _ = LoadSoloistVersionAsync();
        }

        private async Task LoadSoloistVersionAsync()
        {
            _soloistVersion = await SoloistDaemon.GetVersionAsync() ?? "";
            UpdateSoloistStatus();
        }

        private void UpdateSoloistStatus()
        {
            var installed = SoloistDaemon.IsInstalled;
            if (!_soloistInstalling) SoloistInstallBtn.Content = installed ? "Обновить Soloist" : "Скачать Soloist";
            SoloistInstallBtn.IsEnabled = !_soloistInstalling;

            var version = string.IsNullOrEmpty(_soloistVersion) ? "" : $" · {_soloistVersion}";
            SoloistStatusText.Text =
                _soloistInstalling ? "Скачиваю Soloist…"
                : !installed ? "Soloist ещё не скачан"
                : SoloistKey() == null ? "Нет API-ключа — нажми «Получить ключ»" + version
                : !_settings.SoloistEnabled ? "Скачан, выключен" + version
                : _soloist is { IsRunning: true, State.LoggedIn: true } ? "Работает · подключён к Spotify" + version
                : _soloist is { IsRunning: true } ? "Работает · ждёт: выбери «Undertone» в устройствах Spotify" + version
                : "Включён · запустится вместе с треком Spotify" + version;
        }

        private void Soloist_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.SoloistEnabled = SoloistToggle.IsChecked == true;
            PersistSettings();
            SaveSoloistKey();
            ApplySoloistChange();
            if (_settings.SoloistEnabled)
            {
                if (!SoloistDaemon.IsInstalled) ShowToast("Скачай Soloist кнопкой ниже — он весит немного", 3);
                else if (SoloistKey() == null) ShowToast("Вставь API-ключ Soloist — кнопка «Получить ключ»", 3);
                else _ = StartSoloistAsync();
            }
            UpdateSoloistStatus();
        }

        private void SoloistKey_LostFocus(object sender, RoutedEventArgs e) => CommitSoloistKey();

        private void SoloistKey_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            CommitSoloistKey();
        }

        private void CommitSoloistKey()
        {
            if (!SaveSoloistKey()) return;
            ApplySoloistChange();
            ShowToast("Ключ Soloist сохранён", 2);
            if (_settings.SoloistEnabled && SoloistDaemon.IsInstalled) _ = StartSoloistAsync();
            UpdateSoloistStatus();
        }

        /// <summary>true — ключ изменился. Хранится в .env зашифрованным (DPAPI), как токены.</summary>
        private bool SaveSoloistKey()
        {
            var key = SoloistKeyBox.Text.Trim();
            if (key.Length == 0 || key == SoloistKey()) return false;
            _env.Set(new Dictionary<string, string?> { [SoloistDaemon.KeyEnvName] = SecretProtector.Protect(key) });
            _env.Reload();
            SoloistKeyBox.Text = "";
            SoloistKeyBox.Tag = "Ключ сохранён — вставьте новый, чтобы заменить";
            return true;
        }

        private void SoloistGetKey_Click(object sender, RoutedEventArgs e) => OpenUrl(SoloistDaemon.KeyPageUrl);

        private void SoloistDocs_Click(object sender, RoutedEventArgs e) => OpenUrl(SoloistDaemon.DocsUrl);

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("Не удалось открыть браузер", ex); }
        }

        private async void SoloistInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_soloistInstalling) return;
            // процесс держит soloist.exe — сначала закрываем
            ApplySoloistChange();
            _soloistInstalling = true;
            UpdateSoloistStatus();
            var progress = new Progress<double>(p => SoloistInstallBtn.Content = $"Скачиваю… {p:P0}");
            try
            {
                await SoloistDaemon.InstallAsync(progress, CancellationToken.None);
                _soloistVersion = null;
                ShowToast("Spotify Soloist скачан", 2.5);
            }
            catch (Exception ex)
            {
                Log.Warn("Soloist: не удалось скачать", ex);
                ShowToast("Не получилось скачать Soloist: " + Short(ex.Message), 4);
            }
            finally
            {
                _soloistInstalling = false;
                UpdateSoloistStatus();
            }
            if (SoloistDaemon.IsInstalled)
            {
                _ = LoadSoloistVersionAsync();
                if (_settings.SoloistEnabled && SoloistKey() != null) await StartSoloistAsync();
            }
        }
    }
}
