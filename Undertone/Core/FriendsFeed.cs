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
        /// <summary>Сейчас в комнате «Слушать вместе».</summary>
        public bool InRoom { get; set; }
    }

    /// <summary>Лайки участника для «Микса компании»: последние ~300, в формате id:albumId.</summary>
    public sealed class FriendLikes
    {
        public string Member { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Ids { get; set; } = new();
        public long Ts { get; set; }
    }

    /// <summary>Разовое событие комнаты: реакция или предложенный трек. Не хранится на брокере.</summary>
    public sealed class RoomEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>react — реакция (Emoji), suggest — предложить трек хозяину (Track).</summary>
        public string Type { get; set; } = "";
        public string By { get; set; } = "";
        public string Name { get; set; } = "";
        public string Emoji { get; set; } = "";
        public YaTrack? Track { get; set; }
    }

    /// <summary>
    /// Часы комнаты по НАШИМ часам: позиция трека и момент (мс, TickCount64), когда она была такой.
    /// Чужие часы не используем вовсе — у друзей они могут расходиться на секунды, а сверка с интернетом за VPN не всегда проходит.
    /// Повторы состояния от участников могут только подтянуть позицию вперёд: задержка в сети делает сообщение
    /// запоздавшим, поэтому самое «позднее» из них правдивее всего.
    /// </summary>
    public sealed class RoomClock
    {
        private double _pos;
        private long _tick;
        private bool _playing;

        public void Set(double position, bool playing, long nowMs)
        {
            _pos = position;
            _tick = nowMs;
            _playing = playing;
        }

        public void Refine(double position, bool playing, long nowMs)
        {
            if (!playing || !_playing) return;
            if (position > Predict(nowMs) + 0.03) Set(position, true, nowMs);
        }

        public double Predict(long nowMs) => Math.Max(0, _pos + (_playing ? Math.Max(0, nowMs - _tick) / 1000.0 : 0));
    }

    /// <summary>
    /// Общее состояние комнаты «Слушать вместе». Кто угодно из комнаты может его поменять;
    /// побеждает самое свежее изменение (Seq). Позиция считается от общего времени: Position + (сейчас − At).
    /// </summary>
    public sealed class RoomState
    {
        /// <summary>Порядок изменений: точное время в мс (TimeSync.NowMs) в момент изменения.</summary>
        public long Seq { get; set; }
        public string By { get; set; } = "";
        public string ByName { get; set; } = "";
        public bool Active { get; set; }
        public YaTrack? Track { get; set; }
        public bool Playing { get; set; }
        /// <summary>Позиция трека (секунды) в момент At.</summary>
        public double Position { get; set; }
        public long At { get; set; }
        public List<YaTrack> Queue { get; set; } = new();
        /// <summary>Кто открыл комнату — «хозяин».</summary>
        public string Host { get; set; } = "";
        public string HostName { get; set; } = "";
        /// <summary>Управляет только хозяин; остальные предлагают треки.</summary>
        public bool HostOnly { get; set; }
        /// <summary>Кто поставил трек в очередь: id трека → имя.</summary>
        public Dictionary<string, string> Added { get; set; } = new();

        /// <summary>Где трек должен быть сейчас.</summary>
        public TimeSpan ExpectedPosition(long nowMs)
        {
            var seconds = Position + (Playing ? Math.Max(0, nowMs - At) / 1000.0 : 0);
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        /// <summary>a новее b: по времени, при равенстве — по id участника (чтобы все выбрали одно и то же).</summary>
        public static bool IsNewer(RoomState a, RoomState? b) =>
            b == null || a.Seq > b.Seq || (a.Seq == b.Seq && string.CompareOrdinal(a.By, b.By) > 0);
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

        /// <summary>Пришло более свежее состояние комнаты «Слушать вместе» (в фоновом потоке).</summary>
        public event Action<RoomState>? RoomChanged;

        private RoomState? _room;
        private readonly object _roomSync = new();

        /// <summary>Последнее известное состояние комнаты (может быть старым — смотри, есть ли кто в комнате).</summary>
        public RoomState? Room
        {
            get { lock (_roomSync) return _room; }
        }

        public string MemberId => _member;

        // ────────── лайки для «Микса компании» ──────────

        private readonly ConcurrentDictionary<string, FriendLikes> _likes = new();
        private byte[]? _myLikesPayload;

        /// <summary>Лайки друзей обновились (в фоновом потоке).</summary>
        public event Action? LikesChanged;

        /// <summary>Лайки друзей (без своих), только тех, кто есть в ленте.</summary>
        public IReadOnlyList<FriendLikes> FriendsLikes => _likes.Values.Where(l => l.Member != _member).ToList();

        private string LikesTopic(string member) => $"{_topicBase}/likes-{member}";

        public void PublishLikes(string name, IEnumerable<string> likeIds)
        {
            var likes = new FriendLikes { Member = _member, Name = name, Ids = likeIds.Take(300).ToList(), Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            var payload = Encrypt(JsonSerializer.SerializeToUtf8Bytes(likes));
            _myLikesPayload = payload;
            foreach (var client in _clients.Values.Where(c => c.IsConnected))
                _ = SafePublishAsync(client, LikesTopic(_member), payload);
        }

        /// <summary>Реакция или предложение от друга (в фоновом потоке). Свои не приходят.</summary>
        public event Action<RoomEvent>? EventReceived;

        private string EventTopic => $"{_topicBase}/event";
        private readonly Queue<string> _seenEvents = new();
        private readonly HashSet<string> _seenEventSet = new();

        public void PublishEvent(RoomEvent ev)
        {
            ev.By = _member;
            lock (_seenEvents) Remember(ev.Id);
            var payload = Encrypt(JsonSerializer.SerializeToUtf8Bytes(ev));
            foreach (var client in _clients.Values.Where(c => c.IsConnected))
                _ = SafePublishEventAsync(client, payload);
        }

        private async Task SafePublishEventAsync(MqttLite client, byte[] payload)
        {
            try { await client.PublishAsync(EventTopic, payload, retain: false, _cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Друзья: {client.Host} не принял событие", ex); }
        }

        /// <returns>false — уже видели (то же событие от второго брокера).</returns>
        private bool Remember(string id)
        {
            if (!_seenEventSet.Add(id)) return false;
            _seenEvents.Enqueue(id);
            while (_seenEvents.Count > 200) _seenEventSet.Remove(_seenEvents.Dequeue());
            return true;
        }

        /// <summary>Когда последний раз приходило (или уходило) состояние комнаты — Environment.TickCount64.</summary>
        private long _roomSeenAt = long.MinValue / 2;

        /// <summary>
        /// В комнате кто-то есть: участники переотправляют состояние каждые несколько секунд,
        /// так что свежее сообщение значит «комната жива», даже если чей-то статус потерялся.
        /// </summary>
        public bool RoomAlive
        {
            get { lock (_roomSync) return _room is { Active: true } && Environment.TickCount64 - _roomSeenAt < RoomAliveMs; }
        }

        private const long RoomAliveMs = 15_000;

        // позиция комнаты по НАШИМ часам — см. RoomClock
        private readonly RoomClock _clock = new();

        /// <summary>Где сейчас должен быть трек комнаты (по нашим часам). null — комнаты нет.</summary>
        public TimeSpan? RoomPosition
        {
            get
            {
                lock (_roomSync)
                    return _room == null ? null : TimeSpan.FromSeconds(_clock.Predict(Environment.TickCount64));
            }
        }

        /// <summary>
        /// Переотправить текущее состояние комнаты как есть (тот же Seq). Сообщения идут без подтверждения
        /// и иногда теряются — так пропустивший команду получит её через несколько секунд. Дубли все игнорируют.
        /// </summary>
        public void RepublishRoom()
        {
            RoomState copy;
            lock (_roomSync)
            {
                var room = _room;
                if (room == null) return;
                var now = Environment.TickCount64;
                _roomSeenAt = now;
                // то же состояние, но с позицией «на сейчас» — так вошедший позже не отстанет на возраст сообщения
                copy = new RoomState
                {
                    Seq = room.Seq, By = room.By, ByName = room.ByName, Active = room.Active, Track = room.Track,
                    Playing = room.Playing, Position = _clock.Predict(now), At = TimeSync.NowMs, Queue = room.Queue,
                    Host = room.Host, HostName = room.HostName, HostOnly = room.HostOnly, Added = room.Added,
                };
            }
            var payload = Encrypt(JsonSerializer.SerializeToUtf8Bytes(copy));
            foreach (var client in _clients.Values.Where(c => c.IsConnected))
                _ = SafePublishAsync(client, RoomTopic, payload);
        }

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

        private Task SafePublishAsync(MqttLite client, byte[] payload) => SafePublishAsync(client, MyTopic, payload);

        private async Task SafePublishAsync(MqttLite client, string topic, byte[] payload)
        {
            try { await client.PublishAsync(topic, payload, retain: true, _cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Друзья: {client.Host} не принял статус", ex); }
        }

        private string MyTopic => $"{_topicBase}/{_member}";
        private string RoomTopic => $"{_topicBase}/room";

        /// <summary>Разослать новое состояние комнаты (оно же сразу становится текущим и у нас).</summary>
        public void PublishRoom(RoomState state)
        {
            state.By = _member;
            lock (_roomSync)
            {
                if (_room != null && !RoomState.IsNewer(state, _room)) state.Seq = _room.Seq + 1;
                _room = state;
                _roomSeenAt = Environment.TickCount64;
                _clock.Set(state.Position, state.Playing, _roomSeenAt);
            }
            var payload = Encrypt(JsonSerializer.SerializeToUtf8Bytes(state));
            foreach (var client in _clients.Values.Where(c => c.IsConnected))
                _ = SafePublishAsync(client, RoomTopic, payload);
        }

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
                        Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), InRoom = false,
                    };
                    client.MessageReceived += OnMessage;
                    await client.ConnectAsync($"undertone-{_member}-{Guid.NewGuid():N}"[..40], KeepAlive,
                        (MyTopic, Encrypt(Serialize(offline)), true), ct);
                    _clients[host] = client;
                    await client.SubscribeAsync(_topicBase + "/+", ct);
                    lock (_meSync) me = _me;
                    me.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    await client.PublishAsync(MyTopic, Encrypt(Serialize(me)), retain: true, ct);
                    if (_myLikesPayload is { } likes) await client.PublishAsync(LikesTopic(_member), likes, retain: true, ct);
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

            if (topic.StartsWith(_topicBase + "/likes-", StringComparison.Ordinal))
            {
                FriendLikes? likes;
                try { likes = JsonSerializer.Deserialize<FriendLikes>(json); }
                catch (JsonException) { return; }
                if (likes == null || likes.Member.Length == 0 || likes.Member == _member) return;
                _likes.AddOrUpdate(likes.Member, likes, (_, old) => likes.Ts >= old.Ts ? likes : old);
                LikesChanged?.Invoke();
                return;
            }

            if (topic == EventTopic)
            {
                RoomEvent? ev;
                try { ev = JsonSerializer.Deserialize<RoomEvent>(json); }
                catch (JsonException) { return; }
                if (ev == null || ev.By == _member) return;
                lock (_seenEvents) if (!Remember(ev.Id)) return;
                EventReceived?.Invoke(ev);
                return;
            }

            if (topic == RoomTopic)
            {
                RoomState? state;
                try { state = JsonSerializer.Deserialize<RoomState>(json); }
                catch (JsonException) { return; }
                if (state == null) return;
                var now = Environment.TickCount64;
                lock (_roomSync)
                {
                    if (_room != null && state.Seq == _room.Seq && state.By == _room.By)
                    {
                        // повтор того же состояния: в комнате кто-то есть, и можно уточнить позицию
                        if (state.Active) _roomSeenAt = now;
                        _clock.Refine(state.Position, state.Playing, now);
                        return;
                    }
                    if (!RoomState.IsNewer(state, _room)) return; // старое (пришло позже нового)
                    _room = state;
                    _clock.Set(state.Position, state.Playing, now);
                    if (state.Active) _roomSeenAt = now;
                }
                RoomChanged?.Invoke(state);
                return;
            }

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
