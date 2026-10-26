using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    public sealed record OAuthToken(string AccessToken, string? RefreshToken, long? ExpiresIn);

    public sealed record DeviceCode(
        string Code, string UserCode, string VerificationUrl, TimeSpan Interval, DateTime ExpiresAt);

    public sealed class AuthException : Exception
    {
        public string? Error { get; }
        public AuthException(string message, string? error = null) : base(message) => Error = error;
    }

    /// <summary>
    /// OAuth Яндекса: вход по коду (device flow) и обновление токена.
    /// </summary>
    public static class YandexAuth
    {
        // client_id/secret официального Android-клиента — те же, что у публичной библиотеки yandex-music
        private const string ClientId = "23cabbbdc6cd418abb4b39c32c41195d";
        private const string ClientSecret = "53bc75238f0c4d08a118e51fe9203300";
        private const string OAuthBase = "https://oauth.yandex.ru";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public static async Task<DeviceCode> RequestDeviceCodeAsync(CancellationToken ct = default)
        {
            var deviceId = Guid.NewGuid().ToString("N");
            using var root = await PostAsync("/device/code", new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_id"] = deviceId,
                ["device_name"] = "Undertone",
            }, ct);

            var r = root.RootElement;
            if (r.TryGetProperty("error", out var err))
                throw new AuthException(Describe(r), err.GetString());

            return new DeviceCode(
                r.GetProperty("device_code").GetString()!,
                r.GetProperty("user_code").GetString()!,
                r.TryGetProperty("verification_url", out var u) ? u.GetString()! : "https://ya.ru/device",
                TimeSpan.FromSeconds(r.TryGetProperty("interval", out var i) ? Math.Max(1, i.GetInt32()) : 5),
                DateTime.UtcNow.AddSeconds(r.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 300));
        }

        /// <summary>Ждёт, пока пользователь подтвердит код на ya.ru/device.</summary>
        public static async Task<OAuthToken> WaitForTokenAsync(DeviceCode code, CancellationToken ct = default)
        {
            var interval = code.Interval;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > code.ExpiresAt)
                    throw new AuthException("Код истёк — запросите новый.", "expired_token");

                await Task.Delay(interval, ct);

                using var doc = await PostAsync("/token", new Dictionary<string, string>
                {
                    ["grant_type"] = "device_code",
                    ["code"] = code.Code,
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                }, ct);

                var r = doc.RootElement;
                if (r.TryGetProperty("access_token", out _)) return ParseToken(r);

                var error = r.TryGetProperty("error", out var e) ? e.GetString() : null;
                switch (error)
                {
                    case "authorization_pending": continue;
                    case "slow_down": interval += TimeSpan.FromSeconds(2); continue;
                    default: throw new AuthException(Describe(r), error);
                }
            }
        }

        public static async Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            using var doc = await PostAsync("/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
            }, ct);
            var r = doc.RootElement;
            if (!r.TryGetProperty("access_token", out _))
                throw new AuthException(Describe(r), r.TryGetProperty("error", out var e) ? e.GetString() : null);
            var token = ParseToken(r);
            // Яндекс может не вернуть новый refresh_token — оставляем старый
            return token.RefreshToken == null ? token with { RefreshToken = refreshToken } : token;
        }

        private static OAuthToken ParseToken(JsonElement r) => new(
            r.GetProperty("access_token").GetString()!,
            r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            r.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetInt64() : null);

        private static string Describe(JsonElement r)
        {
            if (r.TryGetProperty("error_description", out var d) && d.GetString() is { Length: > 0 } s) return s;
            if (r.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } s2) return s2;
            return "Неизвестная ошибка авторизации";
        }

        private static async Task<JsonDocument> PostAsync(string path, Dictionary<string, string> form, CancellationToken ct)
        {
            using var content = new FormUrlEncodedContent(form);
            using var resp = await Http.PostAsync(OAuthBase + path, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            try { return JsonDocument.Parse(body); }
            catch (JsonException) { throw new AuthException($"OAuth: HTTP {(int)resp.StatusCode}"); }
        }
    }

    /// <summary>
    /// Токены из .env + сохранение обновлённых обратно в .env.
    /// В файле они зашифрованы DPAPI (<see cref="SecretProtector"/>); старые открытые значения
    /// шифруются при первом запуске новой версии.
    /// У SoundCloud свои ключи с префиксом soundcloud_ (<see cref="ForSoundCloud"/>) — в том же .env.
    /// </summary>
    public sealed class TokenStore
    {
        public const string SoundCloudPrefix = "soundcloud_";
        public const string SpotifyPrefix = "spotify_";

        private readonly EnvFile _env;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly string _prefix;
        private readonly string _name;
        private readonly Func<string, CancellationToken, Task<OAuthToken>> _refresh;

        /// <summary>Токены Яндекса: access_token / refresh_token / expires_in.</summary>
        public TokenStore(EnvFile env) : this(env, "", "Яндекс", (rt, ct) => YandexAuth.RefreshAsync(rt, ct))
        {
        }

        internal TokenStore(EnvFile env, string prefix, string name, Func<string, CancellationToken, Task<OAuthToken>> refresh)
        {
            _env = env;
            _prefix = prefix;
            _name = name;
            _refresh = refresh;
            MigrateToEncrypted();
        }

        /// <summary>Токены SoundCloud: soundcloud_access_token / soundcloud_refresh_token / soundcloud_expires_in.</summary>
        public static TokenStore ForSoundCloud(EnvFile env, Func<string, CancellationToken, Task<OAuthToken>> refresh) =>
            new(env, SoundCloudPrefix, "SoundCloud", refresh);

        /// <summary>Токены Spotify: spotify_access_token / spotify_refresh_token / spotify_expires_in.</summary>
        public static TokenStore ForSpotify(EnvFile env, Func<string, CancellationToken, Task<OAuthToken>> refresh) =>
            new(env, SpotifyPrefix, "Spotify", refresh);

        public string EnvPath => _env.FilePath;

        private string K(string key) => _prefix + key;

        /// <summary>TOKEN из переменной окружения имеет приоритет (удобно для отладки) — только для Яндекса.</summary>
        public string? AccessToken =>
            _prefix.Length == 0 && Environment.GetEnvironmentVariable("TOKEN")?.Trim() is { Length: > 0 } t ? t : Read("access_token");

        public string? RefreshToken => Read("refresh_token");

        private string? Read(string key) =>
            SecretProtector.Unprotect(_env.Get(K(key))) is { Length: > 0 } v ? v : null;

        public void Save(OAuthToken token)
        {
            // null — сервер не прислал новый refresh_token, оставляем старый; "" — явно стереть (ручной вход)
            var refresh = token.RefreshToken ?? RefreshToken ?? "";
            _env.Set(new Dictionary<string, string?>
            {
                [K("access_token")] = SecretProtector.Protect(token.AccessToken),
                [K("refresh_token")] = SecretProtector.Protect(refresh),
                [K("expires_in")] = token.ExpiresIn?.ToString() ?? "",
            });
        }

        /// <summary>Стирает токены (выход из аккаунта).</summary>
        public void Clear() =>
            _env.Set(new Dictionary<string, string?>
            {
                [K("access_token")] = "",
                [K("refresh_token")] = "",
                [K("expires_in")] = "",
            });

        private void MigrateToEncrypted()
        {
            try
            {
                var access = _env.Get(K("access_token"));
                var refresh = _env.Get(K("refresh_token"));
                var plainAccess = access != null && !SecretProtector.IsProtected(access);
                var plainRefresh = refresh != null && !SecretProtector.IsProtected(refresh);
                if (!plainAccess && !plainRefresh) return;

                var values = new Dictionary<string, string?>();
                if (plainAccess) values[K("access_token")] = SecretProtector.Protect(access!);
                if (plainRefresh) values[K("refresh_token")] = SecretProtector.Protect(refresh!);
                _env.Set(values);
                Log.Info($"Токены {_name} в .env зашифрованы (DPAPI)");
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось зашифровать токены", ex);
            }
        }

        /// <summary>Обновляет access_token по refresh_token. null — обновить нельзя.</summary>
        public async Task<string?> TryRefreshAsync(string failedToken, CancellationToken ct)
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                _env.Reload();
                // другой запрос уже обновил токен
                if (AccessToken is { } current && current != failedToken) return current;
                if (RefreshToken is not { } rt) return null;
                // у SoundCloud refresh_token одноразовый: Save сохраняет новый из ответа
                var token = await _refresh(rt, ct);
                Save(token);
                return token.AccessToken;
            }
            catch (AuthException ex)
            {
                Log.Warn($"Токен {_name} не обновился: {ex.Error ?? ex.Message}");
                return null;
            }
            finally
            {
                _refreshLock.Release();
            }
        }
    }
}
