using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Undertone.Core;

namespace Undertone
{
    /// <summary>
    /// Автообновление без вопросов: проверка каждые 5 минут (запрос с ETag почти ничего не стоит),
    /// новая версия — окно по центру с затемнением и списком изменений, скачивание и установка сразу.
    /// Перезапуск — незаметный: когда музыка не играет, или в конце текущего трека; новая версия продолжает
    /// с того же места и сама включает звук. Совсем без перезапуска нельзя: Windows не даёт подменить
    /// работающий exe — его можно только переименовать и запустить новый.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(5);
        /// <summary>Если музыка играет без остановки (длинный микс, повтор трека) — перезапуск не позже этого.</summary>
        private static readonly TimeSpan MaxRestartWait = TimeSpan.FromMinutes(30);

        private Updater? _updater;
        private UpdateInfo? _pendingUpdate;
        private bool _updating;
        /// <summary>Новая версия установлена, ждём удобного момента для перезапуска.</summary>
        private bool _restartPending;
        private DateTime _restartPendingSince;
        private DispatcherTimer? _restartTimer;
        private readonly DispatcherTimer _updateTimer = new() { Interval = UpdateCheckInterval };

        /// <summary>Подпись под логотипом: версия и текущий сервис.</summary>
        private void UpdateVersionText()
        {
            var version = Updater.AppVersion;
            AppVersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build} · {_service.Title()}";
        }

        private void InitUpdater()
        {
            var version = Updater.AppVersion;
            UpdateVersionText();

            if (App.UpdatedFrom != null)
                ShowToast($"Обновлено до версии {version.ToString(3)}", 4);

            // из исходников (start.bat) не обновляемся — следующая сборка всё равно перезапишет exe
            if (EnvFile.IsRunningFromSource) return;

            // репозиторий вшивает CI; можно переопределить в .env: update_repo: owner/repo
            var repo = _env.Get("update_repo") ?? Updater.EmbeddedRepo;
            if (repo == null || !repo.Contains('/')) return;

            _updater = new Updater(repo, version, AppContext.BaseDirectory);
            _updateTimer.Tick += async (_, _) =>
            {
                if (_restartPending) MaybeRestartForUpdate(force: DateTime.UtcNow - _restartPendingSince > MaxRestartWait);
                else await CheckForUpdateAsync();
            };
            _updateTimer.Start();
            _ = CheckForUpdateAsync();
        }

        private async Task CheckForUpdateAsync()
        {
            if (_updater == null || _updating || _restartPending) return;
            UpdateInfo? update;
            try
            {
                update = await _updater.CheckAsync();
            }
            catch (Exception ex)
            {
                // нет сети / лимит GitHub — молча попробуем через 5 минут
                Log.Warn("Проверка обновлений не удалась", ex);
                return;
            }
            if (update == null) return;

            _pendingUpdate = update;
            Log.Info($"Вышла версия {update.Version.ToString(3)} — обновляюсь");
            ShowUpdateOverlay(update);
            if (!IsVisible)
                _tray?.ShowHint($"Вышел Undertone {update.Version.ToString(3)}",
                    "Обновлюсь сам и продолжу музыку с того же места.");
            await InstallUpdateAsync(update);
        }

        /// <summary>Скачивает, проверяет и раскладывает новую версию; потом ждёт момента для перезапуска.</summary>
        private async Task InstallUpdateAsync(UpdateInfo update)
        {
            if (_updater == null || _updating) return;
            _updating = true;
            var v = update.Version.ToString(3);
            SetUpdateBanner("Обновление…", $"Версия {v}: скачиваю", buttonEnabled: false);
            try
            {
                var progress = new Progress<double>(p =>
                {
                    SetUpdateProgress(p, $"Скачиваю… {p:P0}");
                    UpdateSub.Text = $"Версия {v}: {p:P0}";
                });
                var staging = await _updater.DownloadAsync(update, progress);

                SetUpdateProgress(1, "Устанавливаю…");
                await Task.Run(() => _updater.Apply(staging));

                _restartPending = true;
                _restartPendingSince = DateTime.UtcNow;
                Log.Info($"Версия {v} установлена, жду момента для перезапуска");
                MaybeRestartForUpdate(force: false);
            }
            catch (Exception ex)
            {
                Log.Error("Обновление не установилось", ex);
                _updating = false;
                SetUpdateProgress(0, $"Не получилось обновиться: {Short(ex.Message)}. Попробую ещё раз позже.");
                SetUpdateBanner("Не удалось обновить", Short(ex.Message), buttonEnabled: true, button: "Повторить");
            }
        }

        /// <summary>
        /// Перезапуск, когда он не помешает: музыка не играет — через 3 секунды (можно передумать, нажав ▶),
        /// играет — в конце трека (см. NextAsync) или по паузе. force — ждать больше нельзя.
        /// </summary>
        private void MaybeRestartForUpdate(bool force)
        {
            if (!_restartPending) return;
            var v = _pendingUpdate?.Version.ToString(3) ?? "";
            if (_isPlaying && !force)
            {
                _restartTimer?.Stop();
                SetUpdateProgress(1, "Готово! Перезапущусь, когда закончится этот трек — музыка продолжится сама.");
                SetUpdateBanner($"Версия {v} готова", "Перезапуск после этого трека", buttonEnabled: true, button: "Сейчас");
                // окно не держим: дослушивать удобнее без затемнения
                HideUpdateOverlaySoon();
                return;
            }

            if (_restartTimer == null)
            {
                _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _restartTimer.Tick += (_, _) => RestartTick();
            }
            _restartCountdown = force ? 0 : 3;
            _restartTimer.Start();
            RestartTick();
        }

        private int _restartCountdown;

        private void RestartTick()
        {
            // пока считали — включили музыку: подождём конца трека
            if (_isPlaying && _restartCountdown > 0)
            {
                _restartTimer?.Stop();
                MaybeRestartForUpdate(force: false);
                return;
            }
            if (_restartCountdown <= 0)
            {
                _restartTimer?.Stop();
                RestartForUpdate(atNextTrack: false);
                return;
            }
            SetUpdateProgress(1, $"Готово! Перезапуск через {_restartCountdown}…");
            _restartCountdown--;
        }

        /// <summary>
        /// Сохраняет, что играло, и запускает новую версию. atNextTrack — очередь уже на следующем треке,
        /// начинать его с нуля. Музыка играла — новая версия сразу её продолжит (--resume-play).
        /// </summary>
        private void RestartForUpdate(bool atNextTrack)
        {
            var wasPlaying = _isPlaying || atNextTrack;
            try
            {
                SaveSessionForUpdate(atNextTrack);
                var exe = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Undertone.exe");
                var start = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                    ArgumentList = { "--updated", Environment.ProcessId.ToString() },
                };
                if (wasPlaying) start.ArgumentList.Add(App.ResumePlayArg);
                if (!IsVisible) start.ArgumentList.Add(Autostart.TrayArg);
                Log.Info($"Перезапуск в новую версию{(wasPlaying ? ", музыка продолжится" : "")}");
                Process.Start(start);
                ExitApp();
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось перезапуститься в новую версию", ex);
                _restartPending = false;
                _updating = false;
                SetUpdateProgress(1, "Новая версия установлена — она запустится при следующем старте.");
                SetUpdateBanner("Обновление установлено", "Перезапустите Undertone", buttonEnabled: false);
            }
        }

        /// <summary>Трек доиграл, а новая версия ждёт: вместо следующего трека — перезапуск, и тот начнётся уже в ней.</summary>
        private bool RestartInsteadOfNextTrack()
        {
            if (!_restartPending || _inRoom) return false;
            RestartForUpdate(atNextTrack: true);
            return true;
        }

        /// <summary>Поставили паузу, пока ждём перезапуска, — самое время.</summary>
        private void OnPlayStateChangedForUpdate()
        {
            if (_restartPending && !_isPlaying && _restartTimer is not { IsEnabled: true })
                MaybeRestartForUpdate(force: false);
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_restartPending)
            {
                RestartForUpdate(atNextTrack: false);
                return;
            }
            if (_pendingUpdate == null || _updating) return;
            ShowUpdateOverlay(_pendingUpdate);
            await InstallUpdateAsync(_pendingUpdate);
        }

        // ────────── окно обновления ──────────

        private void ShowUpdateOverlay(UpdateInfo update)
        {
            UpdOverlayVersion.Text = $"Версия {update.Version.ToString(3)} · сейчас {Updater.AppVersion.ToString(3)}";
            var notes = PlainReleaseNotes(update.Notes);
            UpdOverlayNotes.Text = notes.Length > 0 ? notes : "Исправления и улучшения.";
            SetUpdateProgress(0, "Скачиваю…");
            UpdateOverlay.Opacity = 0;
            UpdateOverlay.Visibility = Visibility.Visible;
            UpdateOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        }

        private void HideUpdateOverlay()
        {
            if (UpdateOverlay.Visibility != Visibility.Visible) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) =>
            {
                UpdateOverlay.BeginAnimation(OpacityProperty, null);
                UpdateOverlay.Visibility = Visibility.Collapsed;
            };
            UpdateOverlay.BeginAnimation(OpacityProperty, fade);
        }

        private void HideUpdateOverlaySoon()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_restartPending && _isPlaying) HideUpdateOverlay();
            };
            timer.Start();
        }

        private void UpdOverlayHide_Click(object sender, RoutedEventArgs e) => HideUpdateOverlay();

        private void SetUpdateProgress(double fraction, string status)
        {
            UpdOverlayStatus.Text = status;
            // масштаб, а не ширина: работает и пока окно ещё не разложено (ActualWidth = 0)
            UpdOverlayBarScale.ScaleX = Math.Clamp(fraction, 0, 1);
        }

        private void SetUpdateBanner(string title, string sub, bool buttonEnabled, string button = "Обновить")
        {
            UpdateTitle.Text = title;
            UpdateSub.Text = sub;
            UpdateBanner.ToolTip = _pendingUpdate is { Notes.Length: > 0 } u ? PlainReleaseNotes(u.Notes) : null;
            UpdateBtn.Content = button;
            UpdateBtn.IsEnabled = buttonEnabled;
            UpdateBanner.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Заметки релиза (Markdown из CHANGELOG) — в простой текст для окна: без **, `, заголовков и ссылок;
        /// хвост «Установка: …» (он для страницы релиза) отрезается.
        /// </summary>
        internal static string PlainReleaseNotes(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";
            var text = markdown.Replace("\r\n", "\n");
            var install = text.IndexOf("**Установка:**", StringComparison.Ordinal);
            if (install >= 0) text = text[..install];

            var lines = text.Split('\n').Select(line =>
            {
                var l = line.TrimEnd();
                l = Regex.Replace(l, @"^\s{0,3}#{1,6}\s*", "");
                l = Regex.Replace(l, @"^(\s*)[-*]\s+", "$1• ");
                l = Regex.Replace(l, @"\[([^\]]+)\]\([^)]+\)", "$1");
                return l.Replace("**", "").Replace("`", "");
            });
            var result = Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n");
            return result.Trim();
        }
    }
}
