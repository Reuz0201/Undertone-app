using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Транспорт для api.music.yandex.net: подставляет токен, обновляет его при 401
    /// и повторяет GET-запросы при обрыве связи и ошибках сервера 5xx.
    /// Вынесен из YaApi, чтобы YaApi остался чистым клиентом (и его можно было тестировать на фиктивном обработчике).
    /// </summary>
    public sealed class YaHttpHandler : DelegatingHandler
    {
        /// <summary>Запрос с этой опцией не пытается обновить токен при 401 (например, аватар: там 401 — «нет прав»).</summary>
        public static readonly HttpRequestOptionsKey<bool> NoRefresh = new("undertone.noRefresh");

        private const int GetAttempts = 3;
        private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

        private readonly Func<string, CancellationToken, Task<string?>>? _refresh;
        private readonly string _serverName;
        private readonly string _scheme;
        private string _token;

        /// <param name="serverName">Для сообщения «Сервер … не отвечает» (тот же транспорт у SoundCloud и Spotify).</param>
        /// <param name="scheme">Схема заголовка Authorization: «OAuth» у Яндекса и SoundCloud, «Bearer» у Spotify.</param>
        public YaHttpHandler(string token, Func<string, CancellationToken, Task<string?>>? refresh, HttpMessageHandler inner,
            string serverName = "Яндекс Музыки", string scheme = "OAuth")
            : base(inner)
        {
            _token = token;
            _refresh = refresh;
            _serverName = serverName;
            _scheme = scheme;
        }

        public string Token => _token;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = _token;
            var resp = await SendWithRetryAsync(request, token, ct).ConfigureAwait(false);

            var noRefresh = request.Options.TryGetValue(NoRefresh, out var skip) && skip;
            if (resp.StatusCode != HttpStatusCode.Unauthorized || _refresh == null || noRefresh) return resp;

            resp.Dispose();
            var fresh = await _refresh(token, ct).ConfigureAwait(false);
            if (fresh == null)
                throw new YaApiException("Токен недействителен — нужно войти заново.", HttpStatusCode.Unauthorized);
            _token = fresh;
            Log.Info("Токен обновлён");
            return await SendWithRetryAsync(request, fresh, ct).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, string token, CancellationToken ct)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(_scheme, token);
            // повторяем только то, что безопасно повторить: GET ничего не меняет на сервере
            var attempts = request.Method == HttpMethod.Get ? GetAttempts : 1;

            for (int attempt = 1; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(AttemptTimeout);
                try
                {
                    var resp = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    // 429 — «слишком часто» (Spotify): ждём, сколько попросили, но не дольше 10 с
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < attempts)
                    {
                        var wait = resp.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                        if (wait > TimeSpan.FromSeconds(10)) return resp;
                        Log.Warn($"{request.Method} {request.RequestUri?.AbsolutePath}: HTTP 429, жду {wait.TotalSeconds:0.#} с");
                        resp.Dispose();
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                        continue;
                    }
                    if ((int)resp.StatusCode >= 500 && attempt < attempts)
                    {
                        Log.Warn($"{request.Method} {request.RequestUri?.AbsolutePath}: HTTP {(int)resp.StatusCode}, повтор {attempt}");
                        resp.Dispose();
                        await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                        continue;
                    }
                    return resp;
                }
                catch (HttpRequestException ex) when (attempt < attempts)
                {
                    Log.Warn($"{request.Method} {request.RequestUri?.AbsolutePath}: нет связи, повтор {attempt}", ex);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < attempts)
                {
                    Log.Warn($"{request.Method} {request.RequestUri?.AbsolutePath}: таймаут, повтор {attempt}");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new YaApiException($"Сервер {_serverName} не отвечает");
                }
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
            }
        }

        private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(400 * attempt * attempt);
    }
}
