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
    /// Вход: выбор сервиса → Яндекс (код на ya.ru/device или ручной токен), SoundCloud или Spotify (браузер + PKCE).
    /// Токены пишутся в .env в зашифрованном виде (у SoundCloud — ключи soundcloud_…, у Spotify — spotify_…).
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
            // SoundCloud и Spotify входят одинаково — через браузер, панель общая
            AuthScPanel.Visibility = service is MusicServiceKind.SoundCloud or MusicServiceKind.Spotify ? Visibility.Visible : Visibility.Collapsed;

            switch (service)
            {
                case null:
                    AuthChooseHint.Text = reason ?? (_authCancelable
                        ? "Какой сервис подключить? Переключаться между аккаунтами можно в меню профиля."
                        : "Выберите, откуда слушать музыку. Второй сервис можно подключить потом в меню профиля.");
                    YandexConnected.Visibility = _yaTokens.AccessToken != null ? Visibility.Visible : Visibility.Collapsed;
                    ScConnected.Visibility = _scTokens.AccessToken != null ? Visibility.Visible : Visibility.Collapsed;
                    SpConnected.Visibility = _spTokens.AccessToken != null ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case MusicServiceKind.Yandex:
                    AuthHint.Text = reason ?? "Откройте страницу на любом устройстве и введите код:";
                    AuthEnvPath.Text = $"Токены сохранятся в зашифрованном виде в {_env.FilePath}";
                    AuthManual.Text = "";
                    _ = RunDeviceFlowAsync();
                    break;

                case MusicServiceKind.SoundCloud:
                    _browserAuthService = MusicServiceKind.SoundCloud;
                    ScTitle.Text = "Вход в SoundCloud";
                    ScNote.Text = "Полностью играются треки, которые автор открыл для всех. Треки по подписке Go+ — только 30-секундный фрагмент.";
                    ScHint.Text = reason ?? "Откроется браузер — войдите в SoundCloud и разрешите доступ Undertone.";
                    ScEnvPath.Text = $"Токены сохранятся в зашифрованном виде в {_env.FilePath}";
                    _ = RunSoundCloudFlowAsync();
                    break;

                case MusicServiceKind.Spotify:
                    _browserAuthService = MusicServiceKind.Spotify;
                    ScTitle.Text = "Вход в Spotify";
                    ScNote.Text = "Слушать можно только с Spotify Premium. Звук играет встроенный плеер Spotify (устройство «Undertone»), " +
                                  "а если он не запустится — приложение Spotify на этом ПК или телефоне. Эквалайзер, эффекты и офлайн для Spotify не работают.";
                    ScHint.Text = reason ?? "Откроется браузер — войдите в Spotify и разрешите доступ Undertone.";
                    ScEnvPath.Text = $"Токены сохранятся в зашифрованном виде в {_env.FilePath}";
                    _ = RunSpotifyFlowAsync();
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

        private void AuthChooseSpotify_Click(object sender, RoutedEventArgs e) =>
            ChooseService(MusicServiceKind.Spotify);

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

            KeyPanel.Visibility = Visibility.Collapsed;
            var cfg = _scConfig ??= SoundCloudConfig.Load(_env);
            if (cfg == null)
            {
                ShowKeyPanel(MusicServiceKind.SoundCloud);
                return;
            }
            KeyChange.Visibility = Visibility.Visible;

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
                if (ex is AuthException { Error: "invalid_client" })
                {
                    ShowKeyPanel(MusicServiceKind.SoundCloud, "SoundCloud не принял этот ключ — проверь Client ID и Client Secret и вставь ещё раз.");
                    return;
                }
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
            _spConfig = null;
            _env.Reload();
            if (_browserAuthService == MusicServiceKind.Spotify) _ = RunSpotifyFlowAsync();
            else _ = RunSoundCloudFlowAsync();
        }

        // ────────── Spotify: вход через браузер (PKCE, без секрета) ──────────

        private MusicServiceKind _browserAuthService = MusicServiceKind.SoundCloud;

        private async Task RunSpotifyFlowAsync()
        {
            _authCts?.Cancel();
            var cts = _authCts = new CancellationTokenSource();
            _scAuthUrl = null;
            ScOpen.IsEnabled = false;
            ScRetry.Visibility = Visibility.Collapsed;
            ScSpinner.Visibility = Visibility.Visible;

            KeyPanel.Visibility = Visibility.Collapsed;
            var cfg = _spConfig ??= SpotifyConfig.Load(_env);
            if (cfg == null)
            {
                ShowKeyPanel(MusicServiceKind.Spotify);
                return;
            }
            KeyChange.Visibility = Visibility.Visible;

            try
            {
                ScStatus.Text = "Открываю браузер…";
                var token = await SpotifyAuth.SignInAsync(cfg, url =>
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

                _spTokens.Save(token);
                ScStatus.Text = "Готово!";
                await CompleteAuthAsync(MusicServiceKind.Spotify, token.AccessToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) return;
                Log.Warn("Ошибка входа в Spotify", ex);
                if (ex is AuthException { Error: "invalid_client" })
                {
                    ShowKeyPanel(MusicServiceKind.Spotify, "Spotify не узнал этот Client ID — проверь его в Dashboard и вставь ещё раз.");
                    return;
                }
                ScSpinner.Visibility = Visibility.Collapsed;
                ScRetry.Visibility = Visibility.Visible;
                ScStatus.Text = "Не получилось: " + Short(ex.Message);
            }
        }

        // ────────── ключ приложения: вводится на экране входа, хранится в .env на этом ПК ──────────

        private const string SpotifyDashboardUrl = "https://developer.spotify.com/dashboard";
        private const string SoundCloudAppsUrl = "https://soundcloud.com/you/apps";

        private void ShowKeyPanel(MusicServiceKind service, string? reason = null)
        {
            _authCts?.Cancel();
            _browserAuthService = service;
            var spotify = service == MusicServiceKind.Spotify;
            ScSpinner.Visibility = Visibility.Collapsed;
            ScOpen.IsEnabled = false;
            ScRetry.Visibility = Visibility.Collapsed;
            KeyChange.Visibility = Visibility.Collapsed;
            KeyPanel.Visibility = Visibility.Visible;
            KeySecret.Visibility = spotify ? Visibility.Collapsed : Visibility.Visible;
            KeyRedirect.Text = spotify ? SpotifyConfig.DefaultRedirectUri : SoundCloudConfig.DefaultRedirectUri;
            KeyOpenText.Text = spotify ? "Открыть Spotify Dashboard" : "Открыть soundcloud.com/you/apps";
            ScStatus.Text = spotify ? "Нужен Client ID приложения Spotify" : "Нужен ключ приложения SoundCloud";
            KeyHint.Text = reason ?? (spotify
                ? "Один раз: создай приложение в Spotify Dashboard. Redirect URI — ровно адрес ниже, APIs — Web API и Web Playback SDK, " +
                  "в User Management добавь свой e-mail Spotify. Потом вставь сюда Client ID — он сохранится только на этом компьютере."
                : "Один раз: зарегистрируй приложение SoundCloud с Redirect URI ниже и вставь Client ID и Client Secret — " +
                  "они сохранятся только на этом компьютере (секрет — в зашифрованном виде).");
            KeyId.Text = spotify ? SpotifyConfig.Load(_env)?.ClientId ?? "" : SoundCloudConfig.Load(_env)?.ClientId ?? "";
            KeySecret.Text = "";
            KeyId.Focus();
            KeyId.SelectAll();
        }

        private void KeySave_Click(object sender, RoutedEventArgs e) => SaveKeyAndSignIn();

        private void Key_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SaveKeyAndSignIn();
        }

        private void SaveKeyAndSignIn()
        {
            var spotify = _browserAuthService == MusicServiceKind.Spotify;
            var id = KeyId.Text.Trim();
            var secret = KeySecret.Text.Trim();
            if (id.Length < 8 || id.Contains(' '))
            {
                KeyHint.Text = "Это не похоже на Client ID — скопируй его со страницы приложения целиком.";
                KeyId.Focus();
                return;
            }
            if (!spotify && secret.Length == 0)
            {
                KeyHint.Text = "SoundCloud без Client Secret не пускает — вставь и его.";
                KeySecret.Focus();
                return;
            }

            _env.Set(spotify
                ? new Dictionary<string, string?> { [SpotifyConfig.IdKey] = id }
                : new Dictionary<string, string?>
                {
                    [SoundCloudConfig.IdKey] = id,
                    [SoundCloudConfig.SecretKey] = SecretProtector.Protect(secret),
                });
            _env.Reload();
            _scConfig = null;
            _spConfig = null;
            KeyPanel.Visibility = Visibility.Collapsed;
            Log.Info($"Ключ приложения {_browserAuthService.Title()} сохранён в .env");
            if (spotify) _ = RunSpotifyFlowAsync();
            else _ = RunSoundCloudFlowAsync();
        }

        private void KeyChange_Click(object sender, RoutedEventArgs e) => ShowKeyPanel(_browserAuthService);

        private void KeyOpenDashboard_Click(object sender, RoutedEventArgs e) =>
            OpenInBrowser(_browserAuthService == MusicServiceKind.Spotify ? SpotifyDashboardUrl : SoundCloudAppsUrl, ScStatus);

        private void KeyCopyRedirect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(KeyRedirect.Text);
                ShowToast("Redirect URI скопирован");
            }
            catch (Exception ex)
            {
                Log.Warn("Буфер обмена занят", ex);
            }
        }

        private static void OpenInBrowser(string url, System.Windows.Controls.TextBlock status)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "Не удалось открыть браузер: " + Short(ex.Message); }
        }
    }
}
