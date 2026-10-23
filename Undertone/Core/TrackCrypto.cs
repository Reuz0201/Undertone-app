using System;
using System.IO;
using System.Security.Cryptography;

namespace Undertone.Core
{
    /// <summary>
    /// Расшифровка файлов get-file-info с транспортом encraw: AES-128 в режиме CTR,
    /// nonce — 12 нулевых байт, счётчик — 4 байта big-endian с нуля (как в официальных клиентах).
    /// </summary>
    public static class TrackCrypto
    {
        public static void DecryptFile(string source, string target, string hexKey)
        {
            var key = Convert.FromHexString(hexKey);
            if (key.Length is not (16 or 24 or 32)) throw new InvalidDataException("Неверная длина ключа файла");

            using var aes = Aes.Create();
            aes.Key = key;
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

            var counter = new byte[16];
            var keystream = new byte[16];
            var buffer = new byte[1 << 16];
            long block = 0;
            int read;
            // читаем полными буферами: тогда каждый кусок начинается ровно с границы блока AES
            while ((read = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
            {
                for (int i = 0; i < read; i += 16)
                {
                    SetCounter(counter, block++);
                    aes.EncryptEcb(counter, keystream, PaddingMode.None);
                    var n = Math.Min(16, read - i);
                    for (int j = 0; j < n; j++) buffer[i + j] ^= keystream[j];
                }
                output.Write(buffer, 0, read);
            }
        }

        private static void SetCounter(byte[] counter, long block)
        {
            Array.Clear(counter, 0, 12);
            counter[12] = (byte)(block >> 24);
            counter[13] = (byte)(block >> 16);
            counter[14] = (byte)(block >> 8);
            counter[15] = (byte)block;
        }
    }
}
