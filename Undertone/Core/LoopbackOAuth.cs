using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    public sealed record PkcePair(string Verifier, string Challenge);

    /// <summary>
    /// OAuth 2.1 authorization code + PKCE для десктопа: открываем браузер, ловим редирект
    /// на http://127.0.0.1:порт/путь своим мини-сервером и забираем code.
    /// TcpListener, а не HttpListener: тому для 127.0.0.1 могут понадобиться права администратора (URL ACL).
    /// </summary>
    public static class LoopbackOAuth
    {
        private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);

        public static PkcePair CreatePkce()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            return new PkcePair(verifier, ChallengeFor(verifier));
        }

        internal static string ChallengeFor(string verifier) =>
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

        public static string BuildUrl(string baseUrl, IEnumerable<KeyValuePair<string, string>> query) =>
            baseUrl + (baseUrl.Contains('?') ? "&" : "?") +
            string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        /// <summary>Разбор «/callback?code=…&amp;state=…» из строки запроса. Ключи без учёта регистра.</summary>
        internal static (string Path, Dictionary<string, string> Query) ParseRequestTarget(string target)
        {
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var q = target.IndexOf('?');
            var path = q < 0 ? target : target.Substring(0, q);
            if (q >= 0)
            {
                foreach (var pair in target.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    var key = Unescape(eq < 0 ? pair : pair.Substring(0, eq));
                    var value = eq < 0 ? "" : Unescape(pair.Substring(eq + 1));
                    if (key.Length > 0) query[key] = value;
                }
            }
            return (path, query);
        }

        private static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        /// <summary>
        /// Слушает redirectUri, открывает authorizeUrl (через open) и ждёт редиректа с code.
        /// Проверяет state. Отмена — через ct (кнопка «Назад» на экране входа).
        /// </summary>
        public static async Task<string> WaitForCodeAsync(Uri redirectUri, string authorizeUrl, string expectedState,
            Action<string> open, CancellationToken ct)
        {
            if (!redirectUri.IsLoopback || redirectUri.Scheme != Uri.UriSchemeHttp)
                throw new AuthException($"redirect_uri должен быть вида http://127.0.0.1:порт/путь, а не {redirectUri}");

            var addresses = redirectUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
                : new[] { IPAddress.Parse(redirectUri.Host.Trim('[', ']')) };

            var listeners = new List<TcpListener>();
            try
            {
                foreach (var address in addresses)
                {
                    var listener = new TcpListener(address, redirectUri.Port);
                    try { listener.Start(); listeners.Add(listener); }
                    catch (SocketException) when (addresses.Length > 1) { /* IPv6 может быть выключен */ }
                    catch (SocketException ex)
                    {
                        throw new AuthException($"Порт {redirectUri.Port} занят другой программой ({ex.SocketErrorCode}). " +
                                                "Закройте её или поменяйте порт в redirect_uri.");
                    }
                }
                if (listeners.Count == 0)
                    throw new AuthException($"Не удалось занять порт {redirectUri.Port} — закройте программу, которая его держит.");

                open(authorizeUrl);

                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // отмена освобождает порт сразу, а не когда до finally дойдёт продолжение:
                // иначе повторный вход тут же упрётся в «порт занят»
                using var release = stop.Token.Register(() => { foreach (var l in listeners) l.Stop(); });
                var accepts = listeners.Select(l => AcceptLoopAsync(l, redirectUri.AbsolutePath, expectedState, stop.Token)).ToList();
                var done = await Task.WhenAny(accepts);
                stop.Cancel();
                return await done;
            }
            finally
            {
                foreach (var l in listeners) l.Stop();
            }
        }

        private static async Task<string> AcceptLoopAsync(TcpListener listener, string callbackPath, string expectedState,
            CancellationToken ct)
        {
            while (true)
            {
                TcpClient accepted;
                try { accepted = await listener.AcceptTcpClientAsync(ct); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException &&
                                           ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                using var client = accepted;
                using var stream = client.GetStream();

                string? target;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(RequestReadTimeout);
                    try { target = await ReadRequestTargetAsync(stream, readCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
                    catch (IOException) { continue; }
                }
                if (target == null) continue;

                var (path, query) = ParseRequestTarget(target);
                if (!string.Equals(path.TrimEnd('/'), callbackPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    // favicon.ico и прочее
                    await RespondAsync(stream, 404, "Not found", ct);
                    continue;
                }

                if (query.TryGetValue("error", out var error))
                {
                    var description = query.TryGetValue("error_description", out var d) && d.Length > 0 ? d : error;
                    await RespondAsync(stream, 200, Page("Вход не выполнен", "Можно закрыть вкладку и вернуться в Undertone."), ct);
                    throw new AuthException(error == "access_denied" ? "Доступ не разрешён" : description, error);
                }
                if (!query.TryGetValue("state", out var state) || state != expectedState)
                {
                    await RespondAsync(stream, 400, Page("Что-то пошло не так", "Ответ не совпал с запросом. Попробуйте войти ещё раз."), ct);
                    throw new AuthException("Ответ сервиса не совпал с запросом (state) — попробуйте ещё раз", "invalid_state");
                }
                if (!query.TryGetValue("code", out var code) || code.Length == 0)
                {
                    await RespondAsync(stream, 400, Page("Что-то пошло не так", "Сервис не прислал код входа."), ct);
                    throw new AuthException("Сервис не прислал код входа", "no_code");
                }

                await RespondAsync(stream, 200, Page("Готово!", "Вход выполнен — можно закрыть вкладку и вернуться в Undertone."), ct);
                return code;
            }
        }

        /// <summary>Первая строка HTTP-запроса «GET /callback?… HTTP/1.1» → «/callback?…».</summary>
        private static async Task<string?> ReadRequestTargetAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(total), ct);
                if (n == 0) break;
                total += n;
                // ждём конца заголовков, чтобы браузер получил ответ на полный запрос
                if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n")) break;
            }
            return ParseRequestLine(Encoding.ASCII.GetString(buffer, 0, total));
        }

        internal static string? ParseRequestLine(string request)
        {
            var end = request.IndexOf("\r\n", StringComparison.Ordinal);
            var line = end < 0 ? request : request.Substring(0, end);
            var parts = line.Split(' ');
            return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
        }

        private static async Task RespondAsync(Stream stream, int status, string html, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes(html);
            var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            try
            {
                await stream.WriteAsync(head, ct);
                await stream.WriteAsync(body, ct);
                await stream.FlushAsync(ct);
            }
            catch (IOException) { /* браузер уже закрыл соединение */ }
        }

        private static string Page(string title, string text) =>
            "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Undertone</title>" +
            "<style>body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;" +
            "background:#0E1016;color:#E8EAF0;font-family:'Segoe UI',sans-serif}" +
            "div{background:#181B24;border:1px solid #232735;border-radius:22px;padding:34px 38px;max-width:420px}" +
            "h1{margin:0 0 10px;font-size:24px}p{margin:0;color:#9AA0B0}</style></head>" +
            $"<body><div><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(text)}</p></div></body></html>";

        internal static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
