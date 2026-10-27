using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Ключ приложения Spotify (developer.spotify.com/dashboard). Нужен только Client ID — вход по PKCE,
    /// секрет не используется и в сборку не попадает. Источники — те же, что у SoundCloud:
    /// переменная SPOTIFY_CLIENT_ID, .env (spotify_client_id), .env исходников, вшитое CI (SpotifyClientId).
    /// </summary>
    public sealed record SpotifyConfig(string ClientId, string RedirectUri)
    {
        /// <summary>Spotify пускает только адреса 127.0.0.1 (не localhost) — ровно так его и надо вписать в Dashboard.</summary>
        public const string DefaultRedirectUri = "http://127.0.0.1:47824/callback";

        internal const string IdKey = "spotify_client_id";
        internal const string RedirectKey = "spotify_redirect_uri";

        public static SpotifyConfig? Load(EnvFile env) => Resolve(SoundCloudConfig.Sources(env));

        internal static SpotifyConfig? Resolve(IEnumerable<Func<string, string?>> sources)
        {
            var list = sources.ToList();
            string? Find(string key) => list.Select(s => s(key)?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            var id = Find(IdKey);
            return id == null ? null : new SpotifyConfig(id, Find(RedirectKey) ?? DefaultRedirectUri);
        }
    }

    /// <summary>OAuth Spotify: authorization code + PKCE через браузер (без секрета), обновление по refresh_token.</summary>
    public static class SpotifyAuth
    {
        public const string AuthorizeUrl = "https://accounts.spotify.com/authorize";
        public const string TokenUrl = "https://accounts.spotify.com/api/token";

        /// <summary>
        /// streaming — играть через Web Playback SDK (нужен Premium); library/playlist — лайки и плейлисты;
        /// playback-state — запасной путь: управлять приложением Spotify (Spotify Connect).
        /// </summary>
        public const string Scopes =
            "streaming user-read-email user-read-private " +
            "user-library-read user-library-modify " +
            "playlist-read-private playlist-read-collaborative playlist-modify-private playlist-modify-public " +
            "user-read-playback-state user-modify-playback-state user-read-currently-playing user-follow-read " +
            // история и топ — основа своей волны, миксов и «похожих»
            "user-top-read user-read-recently-played";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        public static async Task<OAuthToken> SignInAsync(SpotifyConfig cfg, Action<string> open, CancellationToken ct)
        {
            if (!Uri.TryCreate(cfg.RedirectUri, UriKind.Absolute, out var redirect))
                throw new AuthException($"Неверный redirect_uri: {cfg.RedirectUri}");

            var pkce = LoopbackOAuth.CreatePkce();
            var state = LoopbackOAuth.CreateState();
            var url = LoopbackOAuth.BuildUrl(AuthorizeUrl, new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = cfg.RedirectUri,
                ["code_challenge_method"] = "S256",
                ["code_challenge"] = pkce.Challenge,
                ["state"] = state,
                ["scope"] = Scopes,
            });

            var code = await LoopbackOAuth.WaitForCodeAsync(redirect, url, state, open, ct);
            return await TokenRequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = cfg.RedirectUri,
                ["client_id"] = cfg.ClientId,
                ["code_verifier"] = pkce.Verifier,
            }, ct);
        }

        /// <summary>С PKCE Spotify может прислать новый refresh_token — TokenStore.Save его сохранит.</summary>
        public static Task<OAuthToken> RefreshAsync(SpotifyConfig cfg, string refreshToken, CancellationToken ct) =>
            TokenRequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = cfg.ClientId,
            }, ct);

        private static async Task<OAuthToken> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
        {
            using var content = new FormUrlEncodedContent(form);
            using var resp = await Http.PostAsync(TokenUrl, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException) { throw new AuthException($"Spotify OAuth: HTTP {(int)resp.StatusCode}"); }
            using (doc)
            {
                try
                {
                    return SoundCloudAuth.ParseTokenResponse(doc.RootElement);
                }
                catch (AuthException ex) when (ex.Error == "invalid_client")
                {
                    throw new AuthException("Spotify не узнал Client ID — проверь его в .env (spotify_client_id)", ex.Error);
                }
            }
        }
    }
}
