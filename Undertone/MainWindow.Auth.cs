using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Undertone.Core;

namespace Undertone
{
    /// <summary>
    /// Вход: выбор сервиса → Яндекс (код на ya.ru/device или ручной токен) или SoundCloud (браузер + PKCE).
    /// Токены пишутся в .env в зашифрованном виде (у SoundCloud — ключи soundcloud_…).
    /// </summary>
    public partial class MainWindow
    {
        private CancellationTokenSource? _authCts;
        private DeviceCode? _deviceCode;
        private string? _scAuthUrl;
        /// <summary>Экран входа открыт поверх работающего аккаунта (подключаем второй) — его можно закрыть.</summary>
        private bool _authCancelable;

        /// <param name="service">null — экран выбора сервиса.</param>
        /// <param name="cancelable">Есть активный аккаунт, на который можно вернуться кнопкой «Отмена».</param>
        private void ShowAuth(MusicServiceKind? service = null, string? reason = null, bool cancelable = false)
        {
            _authCts?.Cancel();
            _authCancelable = cancelable && _api != null;
            AuthOverlay.Visibility = Visibility.Visible;
            AuthCancel.Visibility = _authCancelable ? Visibility.Visible : Visibility.Collapsed;

            AuthChoosePanel.Visibility = service == null ? Visibility.Visible : Visibility.Collapsed;
            AuthYandexPanel.Visibility = service == MusicServiceKind.Yandex ? Visibility.Visible : Visibility.Collapsed;
            AuthScPanel.Visibility = service == MusicServiceKind.SoundCloud ? Visibility.Visible : Visibility.Collapsed;

            switch (service)
            {
                case null:
                    AuthChooseHint.Text = reason ?? (_authCancelable
                        ? "Какой сервис подключить? Переключаться между аккаунтами можно в меню профиля."
                        : "Выберите, откуда слушать музыку. Второй сервис можно подключить потом в меню профиля.");
                    YandexConnected.Visibility = _yaTokens.AccessToken != null ? Visibility.Visible : Visibility.Collapsed;
                    ScConnected.Visibility = _scTokens.AccessToken != null ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case MusicServiceKind.Yandex:
                    AuthHint.Text = reason ?? "Откройте страницу на любом устройстве и введите код:";
                    AuthEnvPath.Text = $"Токены сохранятся в зашифрованном виде в {_env.FilePath}";
                    AuthManual.Text = "";
                    _ = RunDeviceFlowAsync();
                    break;

                case MusicServiceKind.SoundCloud:
                    ScHint.Text = reason ?? "Откроется браузер — войдите в SoundCloud и разрешите доступ Undertone.";
                    ScEnvPath.Text = $"Токены сохранятся в зашифрованном виде в {_env.FilePath}";
                    _ = RunSoundCloudFlowAsync();
                    break;
            }
        }

        private void HideAuth()
        {
            _authCts?.Cancel();
            AuthOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>Вход выполнен: старый аккаунт (если был) уступает место новому.</summary>
        private async Task CompleteAuthAsync(MusicServiceKind service, string accessToken)
        {
            HideAuth();
            Log.Info($"Вход выполнен: {service.Title()}");
            ShowToast($"Вход в {service.Title()} выполнен, токены сохранены в .env");
            ResetSession(keepData: true);
            await ConnectAsync(service, accessToken);
        }

        // ────────── выбор сервиса ──────────

        private void AuthChooseYandex_Click(object sender, RoutedEventArgs e) =>
            ChooseService(MusicServiceKind.Yandex);

        private void AuthChooseSc_Click(object sender, RoutedEventArgs e) =>
            ChooseService(MusicServiceKind.SoundCloud);

        /// <summary>Уже вошли в этот сервис — просто переключаемся, иначе — экран входа.</summary>
        private void ChooseService(MusicServiceKind service)
        {
            if (TokensFor(service).AccessToken is { Length: > 0 })
            {
                HideAuth();
                _ = SwitchToAsync(service);
                return;
            }
            ShowAuth(service, cancelable: _authCancelable);
        }

        private void AuthBack_Click(object sender, RoutedEventArgs e) => ShowAuth(null, cancelable: _authCancelable);

        private void AuthCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_authCancelable) HideAuth();
        }

        // ────────── Яндекс: вход по коду ──────────

        private async Task RunDeviceFlowAsync()
        {
            _authCts?.Cancel();
            var cts = _authCts = new CancellationTokenSource();

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    AuthStatus.Text = "Получаю код…";
                    AuthCode.Text = "· · · · · ·";
                    _deviceCode = await YandexAuth.RequestDeviceCodeAsync(cts.Token);
                    if (cts.IsCancellationRequested) return;
                    AuthCode.Text = _deviceCode.UserCode;
                    AuthOpenText.Text = "Открыть " + new Uri(_deviceCode.VerificationUrl).Host + new Uri(_deviceCode.VerificationUrl).AbsolutePath.TrimEnd('/');
                    AuthStatus.Text = "Жду подтверждения…";

                    var token = await YandexAuth.WaitForTokenAsync(_deviceCode, cts.Token);
                    if (cts.IsCancellationRequested) return;

                    _yaTokens.Save(token);
                    AuthStatus.Text = "Готово!";
                    await CompleteAuthAsync(MusicServiceKind.Yandex, token.AccessToken);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (AuthException ex) when (ex.Error == "expired_token")
                {
                    // код протух — берём новый
                }
                catch (Exception ex)
                {
                    if (cts.IsCancellationRequested) return;
                    Log.Warn("Ошибка входа по коду", ex);
                    AuthStatus.Text = $"Ошибка: {Short(ex.Message)}. Повтор через 5 сек…";
                    try { await Task.Delay(5000, cts.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private void AuthOpen_Click(object sender, RoutedEventArgs e) =>
            OpenInBrowser(_deviceCode?.VerificationUrl ?? "https://ya.ru/device", AuthStatus);

        private void AuthCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_deviceCode == null) return;
            try
            {
                Clipboard.SetText(_deviceCode.UserCode);
                AuthStatus.Text = "Код скопирован. Жду подтверждения…";
            }
            catch (Exception) { /* буфер занят другим приложением */ }
        }

        private async void AuthManual_Click(object sender, RoutedEventArgs e) => await SaveManualTokenAsync();

        private async void AuthManual_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SaveManualTokenAsync();
        }

        private async Task SaveManualTokenAsync()
        {
            var token = AuthManual.Text.Trim();
            if (token.StartsWith("access_token", StringComparison.OrdinalIgnoreCase))
                token = token.Substring("access_token".Length).TrimStart(':', '=', ' ');
            if (token.Length < 20)
            {
                AuthStatus.Text = "Это не похоже на токен Яндекса.";
                return;
            }
            _authCts?.Cancel();
            _yaTokens.Save(new OAuthToken(token, "", null));
            await CompleteAuthAsync(MusicServiceKind.Yandex, token);
        }

        // ────────── SoundCloud: вход через браузер ──────────

        private async Task RunSoundCloudFlowAsync()
        {
            _authCts?.Cancel();
            var cts = _authCts = new CancellationTokenSource();
            _scAuthUrl = null;
            ScOpen.IsEnabled = false;
            ScRetry.Visibility = Visibility.Collapsed;
            ScSpinner.Visibility = Visibility.Visible;

            var cfg = _scConfig ??= SoundCloudConfig.Load(_env);
            if (cfg == null)
            {
                ScSpinner.Visibility = Visibility.Collapsed;
                ScRetry.Visibility = Visibility.Visible;
                ScStatus.Text = "Не задан ключ приложения SoundCloud. Зарегистрируйте приложение на soundcloud.com/you/apps " +
                                $"и впишите soundcloud_client_id и soundcloud_client_secret в {_env.FilePath} " +
                                $"(redirect URI приложения: {SoundCloudConfig.DefaultRedirectUri}), затем нажмите «Повторить».";
                return;
            }

            try
            {
                ScStatus.Text = "Открываю браузер…";
                var token = await SoundCloudAuth.SignInAsync(cfg, url =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _scAuthUrl = url;
                        ScOpen.IsEnabled = true;
                        ScStatus.Text = "Жду, пока вы разрешите доступ в браузере…";
                        OpenInBrowser(url, ScStatus);
                    });
                }, cts.Token);
                if (cts.IsCancellationRequested) return;

                _scTokens.Save(token);
                ScStatus.Text = "Готово!";
                await CompleteAuthAsync(MusicServiceKind.SoundCloud, token.AccessToken);
            }
            catch (OperationCanceledException)
            {
                // ушли с экрана
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) return;
                Log.Warn("Ошибка входа в SoundCloud", ex);
                ScSpinner.Visibility = Visibility.Collapsed;
                ScRetry.Visibility = Visibility.Visible;
                ScStatus.Text = "Не получилось: " + Short(ex.Message);
            }
        }

        private void ScOpen_Click(object sender, RoutedEventArgs e)
        {
            if (_scAuthUrl != null) OpenInBrowser(_scAuthUrl, ScStatus);
        }

        private void ScRetry_Click(object sender, RoutedEventArgs e)
        {
            _scConfig = null; // могли только что вписать ключи в .env
            _env.Reload();
            _ = RunSoundCloudFlowAsync();
        }

        private static void OpenInBrowser(string url, System.Windows.Controls.TextBlock status)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "Не удалось открыть браузер: " + Short(ex.Message); }
        }
    }
}
