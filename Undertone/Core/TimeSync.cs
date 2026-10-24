using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Поправка часов этого компьютера по интернет-времени (SNTP). Нужна «Слушать вместе»:
    /// у друзей часы могут расходиться на секунды, а позиция трека считается от общего времени.
    /// Не получилось (UDP закрыт) — поправка 0, синхронизация будет чуть грубее.
    /// </summary>
    public static class TimeSync
    {
        private static readonly string[] Servers = { "time.windows.com", "pool.ntp.org", "time.google.com" };
        private static long _offsetMs;
        private static int _started;

        /// <summary>Сколько миллисекунд прибавить к локальным часам, чтобы получить точное время.</summary>
        public static long OffsetMs => Interlocked.Read(ref _offsetMs);

        /// <summary>Точное время, Unix-миллисекунды.</summary>
        public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + OffsetMs;

        /// <summary>Запускает сверку один раз за сеанс (в фоне).</summary>
        public static void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _ = Task.Run(SyncAsync);
        }

        private static async Task SyncAsync()
        {
            foreach (var server in Servers)
            {
                try
                {
                    var offset = await QueryAsync(server, TimeSpan.FromSeconds(3));
                    Interlocked.Exchange(ref _offsetMs, offset);
                    Log.Info($"Время сверено с {server}: поправка {offset} мс");
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"SNTP {server} не ответил", ex);
                }
            }
        }

        /// <summary>Классический SNTP: offset = ((t1 − t0) + (t2 − t3)) / 2.</summary>
        internal static async Task<long> QueryAsync(string host, TimeSpan timeout)
        {
            using var udp = new UdpClient();
            using var cts = new CancellationTokenSource(timeout);
            udp.Connect(host, 123);
            var request = new byte[48];
            request[0] = 0x1B; // LI = 0, версия 3, режим «клиент»

            var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var watch = Stopwatch.StartNew();
            await udp.SendAsync(request, cts.Token);
            var reply = await udp.ReceiveAsync(cts.Token);
            var t3 = t0 + watch.ElapsedMilliseconds;
            if (reply.Buffer.Length < 48) throw new InvalidOperationException("SNTP: короткий ответ");

            var t1 = NtpToUnixMs(reply.Buffer.AsSpan(32, 8));
            var t2 = NtpToUnixMs(reply.Buffer.AsSpan(40, 8));
            return ((t1 - t0) + (t2 - t3)) / 2;
        }

        /// <summary>Метка NTP (секунды с 1900 года + доля) → Unix-миллисекунды.</summary>
        internal static long NtpToUnixMs(ReadOnlySpan<byte> stamp)
        {
            ulong seconds = BinaryPrimitives.ReadUInt32BigEndian(stamp);
            ulong fraction = BinaryPrimitives.ReadUInt32BigEndian(stamp.Slice(4));
            const ulong UnixEpochInNtp = 2208988800UL;
            var ms = (seconds - UnixEpochInNtp) * 1000UL + (fraction * 1000UL >> 32);
            return (long)ms;
        }
    }
}
