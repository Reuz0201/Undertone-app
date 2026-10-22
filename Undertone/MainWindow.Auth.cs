using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Undertone.Core;

namespace Undertone
{
    /// <summary>Вход: код на ya.ru/device (как main.py) или ручная вставка токена. Результат пишется в .env.</summary>
    public partial class MainWindow
    {
        private CancellationTokenSource? _authCts;
        private DeviceCode? _deviceCode;

        private void ShowAuth(string? reason = null)
        {
            AuthOverlay.Visibility = Visibility.Visible;
            AuthHint.Text = reason ?? "Откройте страницу на любом устройстве и введите код:";
            AuthEnvPath.Text = $"Токены сохранятся в {_env.FilePath}";
            AuthManual.Text = "";
            _ = RunDeviceFlowAsync();
        }

        private void HideAuth()
        {
            _authCts?.Cancel();
            AuthOverlay.Visibility = Visibility.Collapsed;
        }

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
                    AuthCode.Text = _deviceCode.UserCode;
                    AuthOpenText.Text = "Открыть " + new Uri(_deviceCode.VerificationUrl).Host + new Uri(_deviceCode.VerificationUrl).AbsolutePath.TrimEnd('/');
                    AuthStatus.Text = "Жду подтверждения…";

                    var token = await YandexAuth.WaitForTokenAsync(_deviceCode, cts.Token);
                    if (cts.IsCancellationRequested) return;

                    _tokens.Save(token);
                    AuthStatus.Text = "Готово!";
                    await CompleteAuthAsync(token.AccessToken);
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
                    AuthStatus.Text = $"Ошибка: {Short(ex.Message)}. Повтор через 5 сек…";
                    try { await Task.Delay(5000, cts.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private async Task CompleteAuthAsync(string accessToken)
        {
            HideAuth();
            ShowToast("Вход выполнен, токены сохранены в .env");
            await ConnectAsync(accessToken);
        }

        private void AuthOpen_Click(object sender, RoutedEventArgs e)
        {
            var url = _deviceCode?.VerificationUrl ?? "https://ya.ru/device";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { AuthStatus.Text = "Не удалось открыть браузер: " + Short(ex.Message); }
        }

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
            _tokens.Save(new OAuthToken(token, "", null));
            await CompleteAuthAsync(token);
        }
    }
}
