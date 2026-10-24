using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Минимальный клиент MQTT 3.1.1 поверх TLS: только то, что нужно ленте друзей —
    /// подключиться (с «последней волей»), подписаться, публиковать QoS 0 с retain, держать связь пингами.
    /// Свой, а не библиотека: пара сотен строк без зависимостей и полностью под контролем.
    /// </summary>
    public sealed class MqttLite : IDisposable
    {
        private const byte Connect = 0x10, ConnAck = 0x20, Publish = 0x30, Subscribe = 0x82, SubAck = 0x90;
        private const byte PingReq = 0xC0, PingResp = 0xD0, Disconnect = 0xE0;

        private readonly string _host;
        private readonly int _port;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private TcpClient? _tcp;
        private Stream? _stream;
        private ushort _packetId;

        public string Host => _host;
        public bool IsConnected => _stream != null;

        /// <summary>Пришло сообщение: тема и содержимое. Вызывается в фоновом потоке.</summary>
        public event Action<string, byte[]>? MessageReceived;

        public MqttLite(string host, int port = 8883)
        {
            _host = host;
            _port = port;
        }

        /// <summary>Подключается и ждёт CONNACK. Will — что брокер опубликует сам, если связь оборвётся.</summary>
        public async Task ConnectAsync(string clientId, int keepAliveSeconds,
            (string Topic, byte[] Payload, bool Retain)? will, CancellationToken ct)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await tcp.ConnectAsync(_host, _port, connectTimeout.Token);
                }
                var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _host }, ct);

                await WritePacketAsync(ssl, Connect, EncodeConnect(clientId, keepAliveSeconds, will), ct);
                var (type, body) = await ReadPacketAsync(ssl, ct);
                if ((type & 0xF0) != ConnAck || body.Length < 2) throw new IOException("MQTT: брокер не ответил на подключение");
                if (body[1] != 0) throw new IOException($"MQTT: брокер отказал в подключении (код {body[1]})");

                _tcp = tcp;
                _stream = ssl;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        public Task SubscribeAsync(string topicFilter, CancellationToken ct)
        {
            var id = NextId();
            var body = new List<byte> { (byte)(id >> 8), (byte)id };
            AddString(body, topicFilter);
            body.Add(0); // QoS 0
            return WriteAsync(Subscribe, body.ToArray(), ct);
        }

        public Task PublishAsync(string topic, byte[] payload, bool retain, CancellationToken ct) =>
            WriteAsync((byte)(Publish | (retain ? 1 : 0)), EncodePublish(topic, payload), ct);

        public Task PingAsync(CancellationToken ct) => WriteAsync(PingReq, Array.Empty<byte>(), ct);

        /// <summary>Читает входящие пакеты, пока связь жива. Заканчивается исключением при обрыве.</summary>
        public async Task ReadLoopAsync(CancellationToken ct)
        {
            var stream = _stream ?? throw new InvalidOperationException("MQTT: нет подключения");
            while (!ct.IsCancellationRequested)
            {
                var (type, body) = await ReadPacketAsync(stream, ct);
                if ((type & 0xF0) == Publish)
                {
                    if (TryDecodePublish(type, body, out var topic, out var payload))
                        MessageReceived?.Invoke(topic, payload);
                }
                // SUBACK, PINGRESP и прочее нам не важны
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                if (_stream != null) await WriteAsync(Disconnect, Array.Empty<byte>(), CancellationToken.None);
            }
            catch (Exception) { }
            Dispose();
        }

        public void Dispose()
        {
            try { _stream?.Dispose(); } catch (Exception) { }
            try { _tcp?.Dispose(); } catch (Exception) { }
            _stream = null;
            _tcp = null;
        }

        private ushort NextId()
        {
            _packetId++;
            if (_packetId == 0) _packetId = 1;
            return _packetId;
        }

        private async Task WriteAsync(byte header, byte[] body, CancellationToken ct)
        {
            var stream = _stream ?? throw new IOException("MQTT: нет подключения");
            await _writeLock.WaitAsync(ct);
            try
            {
                await WritePacketAsync(stream, header, body, ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // ────────── кодирование (internal — для тестов) ──────────

        internal static byte[] EncodeConnect(string clientId, int keepAlive, (string Topic, byte[] Payload, bool Retain)? will)
        {
            var b = new List<byte>();
            AddString(b, "MQTT");
            b.Add(4); // MQTT 3.1.1
            byte flags = 0x02; // clean session
            if (will is { } w)
            {
                flags |= 0x04;
                if (w.Retain) flags |= 0x20;
            }
            b.Add(flags);
            b.Add((byte)(keepAlive >> 8));
            b.Add((byte)keepAlive);
            AddString(b, clientId);
            if (will is { } w2)
            {
                AddString(b, w2.Topic);
                b.Add((byte)(w2.Payload.Length >> 8));
                b.Add((byte)w2.Payload.Length);
                b.AddRange(w2.Payload);
            }
            return b.ToArray();
        }

        internal static byte[] EncodePublish(string topic, byte[] payload)
        {
            var b = new List<byte>();
            AddString(b, topic);
            b.AddRange(payload); // QoS 0 — без номера пакета
            return b.ToArray();
        }

        internal static bool TryDecodePublish(byte header, byte[] body, out string topic, out byte[] payload)
        {
            topic = "";
            payload = Array.Empty<byte>();
            if (body.Length < 2) return false;
            var len = body[0] << 8 | body[1];
            if (2 + len > body.Length) return false;
            topic = Encoding.UTF8.GetString(body, 2, len);
            var offset = 2 + len;
            var qos = (header >> 1) & 0x03;
            if (qos > 0) offset += 2; // номер пакета
            if (offset > body.Length) return false;
            payload = body.AsSpan(offset).ToArray();
            return true;
        }

        internal static byte[] EncodeRemainingLength(int length)
        {
            var b = new List<byte>();
            do
            {
                var digit = (byte)(length % 128);
                length /= 128;
                if (length > 0) digit |= 0x80;
                b.Add(digit);
            } while (length > 0);
            return b.ToArray();
        }

        private static void AddString(List<byte> b, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            b.Add((byte)(bytes.Length >> 8));
            b.Add((byte)bytes.Length);
            b.AddRange(bytes);
        }

        private static async Task WritePacketAsync(Stream stream, byte header, byte[] body, CancellationToken ct)
        {
            var len = EncodeRemainingLength(body.Length);
            var packet = new byte[1 + len.Length + body.Length];
            packet[0] = header;
            len.CopyTo(packet, 1);
            body.CopyTo(packet, 1 + len.Length);
            await stream.WriteAsync(packet, ct);
            await stream.FlushAsync(ct);
        }

        private static async Task<(byte Type, byte[] Body)> ReadPacketAsync(Stream stream, CancellationToken ct)
        {
            var one = new byte[1];
            await stream.ReadExactlyAsync(one, ct);
            var type = one[0];
            int length = 0, multiplier = 1;
            for (int i = 0; ; i++)
            {
                if (i >= 4) throw new IOException("MQTT: неверная длина пакета");
                await stream.ReadExactlyAsync(one, ct);
                length += (one[0] & 0x7F) * multiplier;
                if ((one[0] & 0x80) == 0) break;
                multiplier *= 128;
            }
            if (length > 1 << 20) throw new IOException("MQTT: слишком большой пакет");
            var body = new byte[length];
            if (length > 0) await stream.ReadExactlyAsync(body, ct);
            return (type, body);
        }
    }
}
