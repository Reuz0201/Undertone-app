using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>Что слушает участник компании. Ts — время отправки (Unix, секунды).</summary>
    public sealed class FriendPresence
    {
        public string Member { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Online { get; set; }
        public bool Playing { get; set; }
        public string TrackId { get; set; } = "";
        public string AlbumId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artists { get; set; } = "";
        public string? Cover { get; set; }
        public long Ts { get; set; }
    }

    /// <summary>
    /// Лента друзей без своего сервера: каждый Undertone публикует свой статус в публичные MQTT-брокеры
    /// (сразу в два — если один лежит, работает второй) и читает статусы остальных.
    ///
    /// Приватность: тема — хэш от кода компании (по ней нельзя узнать код), а содержимое зашифровано
    /// AES-GCM ключом из того же кода. Без кода видно только, что кто-то шлёт зашифрованные байты.
    /// Паролей и токенов здесь нет — только название трека и имя, которое участник выбрал сам.
    /// </summary>
    public sealed class FriendsFeed : IDisposable
    {
        public static readonly string[] Brokers = { "broker.hivemq.com", "broker.emqx.io" };
        private const int KeepAlive = 60;

        private readonly byte[] _key;
        private readonly string _topicBase;
        private readonly string _member;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, FriendPresence> _friends = new();
        private readonly ConcurrentDictionary<string, MqttLite> _clients = new();
        private FriendPresence _me;
        private readonly object _meSync = new();

        /// <summary>Список друзей изменился (в фоновом потоке).</summary>
        public event Action? Changed;

        /// <summary>Сколько брокеров сейчас на связи (0 — оффлайн).</summary>
        public int ConnectedBrokers => _clients.Values.Count(c => c.IsConnected);

        public FriendsFeed(string groupCode, string memberId, string name)
        {
            var code = NormalizeCode(groupCode);
            _key = DeriveKey(code);
            _topicBase = "undertone/v1/" + TopicId(code);
            _member = memberId;
            _me = new FriendPresence { Member = memberId, Name = name, Online = true };
            foreach (var host in Brokers) _ = Task.Run(() => RunBrokerAsync(host, _cts.Token));
        }

        public IReadOnlyList<FriendPresence> Friends =>
            _friends.Values.Where(f => f.Member != _member).OrderByDescending(f => f.Ts).ToList();

        /// <summary>Обновить свой статус и разослать его.</summary>
        public void Publish(FriendPresence me)
        {
            me.Member = _member;
            me.Online = true;
            me.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (_meSync) _me = me;
            var payload = Encrypt(Serialize(me));
            foreach (var client in _clients.Values.Where(c => c.IsConnected))
                _ = SafePublishAsync(client, payload);
        }

        private async Task SafePublishAsync(MqttLite client, byte[] payload)
        {
            try { await client.PublishAsync(MyTopic, payload, retain: true, _cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Друзья: {client.Host} не принял статус", ex); }
        }

        private string MyTopic => $"{_topicBase}/{_member}";

        private async Task RunBrokerAsync(string host, CancellationToken ct)
        {
            var delay = TimeSpan.FromSeconds(5);
            while (!ct.IsCancellationRequested)
            {
                var client = new MqttLite(host);
                try
                {
                    FriendPresence me;
                    lock (_meSync) me = _me;
                    var offline = new FriendPresence
                    {
                        Member = _member, Name = me.Name, Online = false, Playing = false,
                        TrackId = me.TrackId, AlbumId = me.AlbumId, Title = me.Title, Artists = me.Artists, Cover = me.Cover,
                        Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    };
                    client.MessageReceived += OnMessage;
                    await client.ConnectAsync($"undertone-{_member}-{Guid.NewGuid():N}"[..40], KeepAlive,
                        (MyTopic, Encrypt(Serialize(offline)), true), ct);
                    _clients[host] = client;
                    await client.SubscribeAsync(_topicBase + "/+", ct);
                    lock (_meSync) me = _me;
                    me.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    await client.PublishAsync(MyTopic, Encrypt(Serialize(me)), retain: true, ct);
                    Log.Info($"Друзья: подключено к {host}");
                    delay = TimeSpan.FromSeconds(5);
                    Changed?.Invoke();

                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var pinger = Task.Run(async () =>
                    {
                        while (!pingCts.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(KeepAlive / 2), pingCts.Token);
                            await client.PingAsync(pingCts.Token);
                        }
                    }, pingCts.Token);
                    try { await client.ReadLoopAsync(ct); }
                    finally { pingCts.Cancel(); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Друзья: нет связи с {host}", ex);
                }
                finally
                {
                    client.MessageReceived -= OnMessage;
                    _clients.TryRemove(host, out _);
                    client.Dispose();
                    Changed?.Invoke();
                }
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(120, delay.TotalSeconds * 2));
            }
        }

        private void OnMessage(string topic, byte[] payload)
        {
            if (!topic.StartsWith(_topicBase + "/", StringComparison.Ordinal) || payload.Length == 0) return;
            var json = Decrypt(payload);
            if (json == null) return; // чужое или битое — молча пропускаем
            FriendPresence? p;
            try { p = JsonSerializer.Deserialize<FriendPresence>(json); }
            catch (JsonException) { return; }
            if (p == null || p.Member.Length == 0 || p.Member == _member) return;
            // одно и то же приходит от двух брокеров — оставляем самое свежее
            _friends.AddOrUpdate(p.Member, p, (_, old) => p.Ts >= old.Ts ? p : old);
            Changed?.Invoke();
        }

        public void Dispose()
        {
            _cts.Cancel();
            // отписываемся «по-хорошему»: статус «не в сети» вместо «последней воли» через минуту
            FriendPresence me;
            lock (_meSync) me = _me;
            var offline = new FriendPresence
            {
                Member = _member, Name = me.Name, Online = false, TrackId = me.TrackId, AlbumId = me.AlbumId,
                Title = me.Title, Artists = me.Artists, Cover = me.Cover, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            var payload = Encrypt(Serialize(offline));
            foreach (var client in _clients.Values)
            {
                try { client.PublishAsync(MyTopic, payload, true, CancellationToken.None).Wait(1500); }
                catch (Exception) { }
                _ = client.DisconnectAsync();
            }
        }

        // ────────── код компании и шифрование (internal — для тестов) ──────────

        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // без 0/O и 1/I — не перепутать

        /// <summary>Новый код компании: 20 символов, например K7QM-2XRA-9PLD-H4WN-TC8E.</summary>
        public static string NewCode()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 20; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]);
            }
            return sb.ToString();
        }

        /// <summary>Регистр, пробелы и дефисы не важны: «k7qm 2xra…» = «K7QM-2XRA-…».</summary>
        public static string NormalizeCode(string code) =>
            new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        public static bool IsValidCode(string code) => NormalizeCode(code).Length >= 12;

        internal static byte[] DeriveKey(string normalizedCode) =>
            SHA256.HashData(Encoding.UTF8.GetBytes("undertone-friends-key|" + normalizedCode));

        internal static string TopicId(string normalizedCode) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("undertone-friends-topic|" + normalizedCode)))[..32].ToLowerInvariant();

        private static byte[] Serialize(FriendPresence p) => JsonSerializer.SerializeToUtf8Bytes(p);

        internal byte[] Encrypt(byte[] plain) => Encrypt(_key, plain);

        internal byte[]? Decrypt(byte[] data) => Decrypt(_key, data);

        internal static byte[] Encrypt(byte[] key, byte[] plain)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag);
            return nonce.Concat(cipher).Concat(tag).ToArray();
        }

        internal static byte[]? Decrypt(byte[] key, byte[] data)
        {
            if (data.Length < 12 + 16) return null;
            var nonce = data.AsSpan(0, 12);
            var cipher = data.AsSpan(12, data.Length - 28);
            var tag = data.AsSpan(data.Length - 16);
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(nonce, cipher, tag, plain);
                return plain;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }
}
