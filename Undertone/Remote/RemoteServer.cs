using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Undertone.Core;

namespace Undertone.Remote
{
    public sealed record RemoteRequest(string Method, string Path, Dictionary<string, string> Query, string Body, bool IsOwner);
    public sealed record RemoteResponse(int Status, string ContentType, byte[] Body)
    {
        public static RemoteResponse Json(string json) => new(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        public static RemoteResponse Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
        public static RemoteResponse Html(string html) => new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
    }

    /// <summary>
    /// Пульт с телефона: крошечный HTTP-сервер в домашней сети. Свой на TcpListener, потому что HttpListener
    /// в Windows без прав администратора слушает только localhost. Доступ — по секретному токену в ссылке (из QR):
    /// у хозяина полный, у гостя — только поиск и «предложить трек».
    /// </summary>
    public sealed class RemoteServer : IDisposable
    {
        public const int DefaultPort = 8686;
        private const int MaxHeader = 16 * 1024;
        private const int MaxBody = 64 * 1024;

        private readonly string _ownerToken;
        private readonly string _guestToken;
        private readonly Func<RemoteRequest, Task<RemoteResponse>> _handler;
        private readonly CancellationTokenSource _cts = new();
        private TcpListener? _listener;

        public int Port { get; private set; }

        public RemoteServer(string ownerToken, string guestToken, Func<RemoteRequest, Task<RemoteResponse>> handler)
        {
            _ownerToken = ownerToken;
            _guestToken = guestToken;
            _handler = handler;
        }

        /// <summary>Слушает первый свободный порт начиная с 8686. Windows при первом запуске спросит про брандмауэр.</summary>
        public void Start()
        {
            for (var port = DefaultPort; port < DefaultPort + 10; port++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Any, port);
                    l.Start();
                    _listener = l;
                    Port = port;
                    _ = Task.Run(() => AcceptLoopAsync(l, _cts.Token));
                    Log.Info($"Пульт: слушаю порт {port}");
                    return;
                }
                catch (SocketException) { }
            }
            throw new IOException("Не нашлось свободного порта для пульта (8686–8695)");
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (Exception) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Log.Warn("Пульт: приём соединения", ex);
                    await Task.Delay(200, CancellationToken.None);
                    continue;
                }
                _ = Task.Run(() => ServeAsync(client, ct));
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;
                    var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, ct);
                    RemoteResponse response;
                    if (request == null) response = RemoteResponse.Text(400, "bad request");
                    else response = await RouteAsync(request);
                    await WriteResponseAsync(stream, response, ct);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                catch (Exception ex)
                {
                    Log.Warn("Пульт: ошибка запроса", ex);
                }
            }
        }

        private async Task<RemoteResponse> RouteAsync((string Method, string Target, string Body) raw)
        {
            var (path, query) = SplitTarget(raw.Target);
            // токен — в ссылке ?t=…; без него ничего не отдаём, даже страницу
            var token = query.GetValueOrDefault("t", "");
            var owner = FixedEquals(token, _ownerToken);
            var guest = !owner && FixedEquals(token, _guestToken);
            if (!owner && !guest) return RemoteResponse.Text(403, "Нужна ссылка из Undertone (QR в настройках)");
            try
            {
                return await _handler(new RemoteRequest(raw.Method, path, query, raw.Body, owner));
            }
            catch (Exception ex)
            {
                Log.Warn($"Пульт: {path}", ex);
                return RemoteResponse.Text(500, ex.Message);
            }
        }

        /// <summary>Сравнение за одинаковое время — чтобы токен нельзя было подобрать по задержке ответа.</summary>
        private static bool FixedEquals(string a, string b)
        {
            if (a.Length != b.Length || b.Length == 0) return false;
            var diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        internal static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
        {
            var q = target.IndexOf('?');
            var path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target);
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (q >= 0)
                foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    var k = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
                    var v = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                    query[k] = v;
                }
            return (path, query);
        }

        private static async Task<(string Method, string Target, string Body)?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[MaxHeader];
            int read = 0, headerEnd = -1;
            while (headerEnd < 0)
            {
                if (read >= buffer.Length) return null;
                var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0) return null;
                read += n;
                headerEnd = IndexOf(buffer, read, "\r\n\r\n"u8);
            }
            var header = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            var lines = header.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var length = 0;
            foreach (var line in lines.Skip(1))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line[15..].Trim(), out length);
            if (length < 0 || length > MaxBody) return null;

            var bodyStart = headerEnd + 4;
            var body = new byte[length];
            var have = Math.Min(length, read - bodyStart);
            Array.Copy(buffer, bodyStart, body, 0, have);
            while (have < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(have), ct);
                if (n == 0) return null;
                have += n;
            }
            return (first[0].ToUpperInvariant(), first[1], Encoding.UTF8.GetString(body));
        }

        private static int IndexOf(byte[] data, int count, ReadOnlySpan<byte> pattern) =>
            data.AsSpan(0, count).IndexOf(pattern);

        private static async Task WriteResponseAsync(NetworkStream stream, RemoteResponse r, CancellationToken ct)
        {
            var reason = r.Status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
            var head = $"HTTP/1.1 {r.Status} {reason}\r\nContent-Type: {r.ContentType}\r\nContent-Length: {r.Body.Length}\r\n" +
                       "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(r.Body, ct);
            await stream.FlushAsync(ct);
        }

        /// <summary>Адрес компьютера в домашней сети (192.168…, 10…, 172.16–31…), без VPN-адаптеров.</summary>
        public static string? LanAddress()
        {
            string[] skip = { "vpn", "tap", "wintun", "wireguard", "virtual", "hyper-v", "vethernet", "wsl", "vmware", "virtualbox", "tunnel", "sota" };
            var candidates = new List<(int Rank, string Ip)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
                if (skip.Any(desc.Contains)) continue;
                var props = nic.GetIPProperties();
                // у настоящей домашней сети есть шлюз
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.Address.GetAddressBytes();
                    var isPrivate = b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
                    if (!isPrivate) continue;
                    var rank = (hasGateway ? 0 : 10) + (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1) + (b[0] == 192 ? 0 : 2);
                    candidates.Add((rank, a.Address.ToString()));
                }
            }
            return candidates.OrderBy(c => c.Rank).Select(c => c.Ip).FirstOrDefault();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener?.Stop(); } catch (Exception) { }
        }
    }
}
