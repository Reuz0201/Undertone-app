using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Ключи приложения SoundCloud (soundcloud.com/you/apps). Откуда берутся, по порядку:
    /// переменные окружения SOUNDCLOUD_CLIENT_ID / SOUNDCLOUD_CLIENT_SECRET / SOUNDCLOUD_REDIRECT_URI,
    /// .env в %LOCALAPPDATA%\Undertone, .env рядом с exe или в корне исходников (start.bat),
    /// и наконец то, что CI вшил в сборку (AssemblyMetadata SoundCloudClientId / SoundCloudClientSecret).
    /// </summary>
    public sealed record SoundCloudConfig(string ClientId, string? ClientSecret, string RedirectUri)
    {
        public const string DefaultRedirectUri = "http://127.0.0.1:47823/callback";

        internal const string IdKey = "soundcloud_client_id";
        internal const string SecretKey = "soundcloud_client_secret";
        internal const string RedirectKey = "soundcloud_redirect_uri";

        public static SoundCloudConfig? Load(EnvFile env) =>
            Resolve(Sources(env));

        /// <summary>Первое непустое значение каждого ключа по источникам. Нет client_id — null.</summary>
        internal static SoundCloudConfig? Resolve(IEnumerable<Func<string, string?>> sources)
        {
            var list = sources.ToList();
            string? Find(string key) => list.Select(s => s(key)?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

            var id = Find(IdKey);
            if (id == null) return null;
            return new SoundCloudConfig(id, Find(SecretKey), Find(RedirectKey) ?? DefaultRedirectUri);
        }

        /// <summary>Источники ключей по порядку: окружение, .env, .env исходников, вшитое CI. Общие для SoundCloud и Spotify.</summary>
        internal static IEnumerable<Func<string, string?>> Sources(EnvFile env)
        {
            yield return key => Environment.GetEnvironmentVariable(key.ToUpperInvariant());
            // секрет, введённый на экране входа, лежит зашифрованным (dpapi:…); открытые значения — как есть
            yield return key => SecretProtector.Unprotect(env.Get(key));
            // запуск из исходников (start.bat): .env в корне репозитория, рядом со start.bat
            foreach (var path in DevEnvPaths())
            {
                EnvFile? dev = null;
                try { dev = EnvFile.Open(path); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                if (dev != null) yield return dev.Get;
            }
            yield return Embedded;
        }

        /// <summary>.env рядом с exe и выше по папкам (bin/Release/… → корень исходников), кроме основного.</summary>
        private static IEnumerable<string> DevEnvPaths()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
            {
                var path = System.IO.Path.Combine(dir.FullName, EnvFile.FileName);
                if (System.IO.File.Exists(path)) yield return path;
            }
        }

        private static string? Embedded(string key)
        {
            var name = key switch
            {
                IdKey => "SoundCloudClientId",
                SecretKey => "SoundCloudClientSecret",
                SpotifyConfig.IdKey => "SpotifyClientId",
                _ => null,
            };
            if (name == null) return null;
            var asm = Assembly.GetEntryAssembly() ?? typeof(SoundCloudConfig).Assembly;
            return asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == name)?.Value;
        }
    }

    /// <summary>OAuth 2.1 SoundCloud: authorization code + PKCE через браузер, обновление по одноразовому refresh_token.</summary>
    public static class SoundCloudAuth
    {
        public const string AuthorizeUrl = "https://secure.soundcloud.com/authorize";
        public const string TokenUrl = "https://secure.soundcloud.com/oauth/token";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>Открывает браузер (open), ждёт разрешения доступа и меняет code на токены.</summary>
        public static async Task<OAuthToken> SignInAsync(SoundCloudConfig cfg, Action<string> open, CancellationToken ct)
        {
            if (!Uri.TryCreate(cfg.RedirectUri, UriKind.Absolute, out var redirect))
                throw new AuthException($"Неверный redirect_uri: {cfg.RedirectUri}");

            var pkce = LoopbackOAuth.CreatePkce();
            var state = LoopbackOAuth.CreateState();
            var url = LoopbackOAuth.BuildUrl(AuthorizeUrl, new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId,
                ["redirect_uri"] = cfg.RedirectUri,
                ["response_type"] = "code",
                ["code_challenge"] = pkce.Challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
            });

            var code = await LoopbackOAuth.WaitForCodeAsync(redirect, url, state, open, ct);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = cfg.ClientId,
                ["redirect_uri"] = cfg.RedirectUri,
                ["code_verifier"] = pkce.Verifier,
                ["code"] = code,
            };
            if (!string.IsNullOrEmpty(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret;
            return await TokenRequestAsync(form, ct);
        }

        /// <summary>
        /// refresh_token у SoundCloud одноразовый: в ответе приходит новый, его обязательно сохранить
        /// (TokenStore.Save так и делает).
        /// </summary>
        public static Task<OAuthToken> RefreshAsync(SoundCloudConfig cfg, string refreshToken, CancellationToken ct)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = cfg.ClientId,
                ["refresh_token"] = refreshToken,
            };
            if (!string.IsNullOrEmpty(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret;
            return TokenRequestAsync(form, ct);
        }

        private static async Task<OAuthToken> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
        {
            using var content = new FormUrlEncodedContent(form);
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = content };
            req.Headers.Accept.ParseAdd("application/json; charset=utf-8");
            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException) { throw new AuthException($"SoundCloud OAuth: HTTP {(int)resp.StatusCode}"); }
            using (doc)
                return ParseTokenResponse(doc.RootElement);
        }

        internal static OAuthToken ParseTokenResponse(JsonElement r)
        {
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("access_token", out var at) &&
                at.GetString() is { Length: > 0 } access)
            {
                var refresh = r.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                    ? rt.GetString() : null;
                long? expires = r.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number
                    ? ex.GetInt64() : null;
                return new OAuthToken(access, refresh, expires);
            }

            string? error = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var e) &&
                            e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var description = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error_description", out var d) &&
                              d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            throw new AuthException(description ?? error ?? "Неизвестная ошибка авторизации SoundCloud", error);
        }
    }
}
