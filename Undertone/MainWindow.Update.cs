using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Undertone.Core;

namespace Undertone
{
    /// <summary>Автообновление: проверка при старте и раз в 6 часов, установка по кнопке.</summary>
    public partial class MainWindow
    {
        private Updater? _updater;
        private UpdateInfo? _pendingUpdate;
        private bool _updating;
        private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

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
                ShowToast($"Обновлено до версии {version.Major}.{version.Minor}.{version.Build}", 4);

            // из исходников (start.bat) не обновляемся — следующая сборка всё равно перезапишет exe
            if (EnvFile.IsRunningFromSource) return;

            // репозиторий вшивает CI; можно переопределить в .env: update_repo: owner/repo
            var repo = _env.Get("update_repo") ?? Updater.EmbeddedRepo;
            if (repo == null || !repo.Contains('/')) return;

            _updater = new Updater(repo, version, AppContext.BaseDirectory);
            _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
            _updateTimer.Start();
            _ = CheckForUpdateAsync();
        }

        private async Task CheckForUpdateAsync()
        {
            if (_updater == null || _updating) return;
            try
            {
                var update = await _updater.CheckAsync();
                if (update == null) return;
                _pendingUpdate = update;
                UpdateTitle.Text = "Доступно обновление";
                UpdateSub.Text = $"Версия {update.Version.Major}.{update.Version.Minor}.{update.Version.Build}";
                UpdateBanner.ToolTip = string.IsNullOrWhiteSpace(update.Notes) ? null : update.Notes;
                UpdateBtn.IsEnabled = true;
                UpdateBtn.Content = "Обновить";
                UpdateBanner.Visibility = Visibility.Visible;
                if (!IsVisible)
                    _tray?.ShowHint("Доступно обновление Undertone",
                        $"Версия {update.Version.Major}.{update.Version.Minor}.{update.Version.Build} — откройте приложение и нажмите «Обновить».");
            }
            catch (Exception ex)
            {
                // нет сети / лимит GitHub — молча попробуем позже
                Log.Warn("Проверка обновлений не удалась", ex);
            }
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_updater == null || _pendingUpdate == null || _updating) return;
            _updating = true;
            UpdateBtn.IsEnabled = false;
            UpdateTitle.Text = "Обновление…";

            try
            {
                var progress = new Progress<double>(p => UpdateSub.Text = $"Скачиваю… {p:P0}");
                var staging = await _updater.DownloadAsync(_pendingUpdate, progress);

                UpdateSub.Text = "Устанавливаю…";
                await Task.Run(() => _updater.Apply(staging));

                UpdateSub.Text = "Перезапуск…";
                var exe = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Undertone.exe");
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                    ArgumentList = { "--updated", Environment.ProcessId.ToString() },
                });
                ExitApp();
            }
            catch (Exception ex)
            {
                Log.Error("Обновление не установилось", ex);
                _updating = false;
                UpdateTitle.Text = "Не удалось обновить";
                UpdateSub.Text = Short(ex.Message);
                UpdateBtn.Content = "Повторить";
                UpdateBtn.IsEnabled = true;
            }
        }
    }
}
