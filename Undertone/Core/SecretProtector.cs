using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Undertone.Core
{
    /// <summary>
    /// Шифрование токенов через Windows DPAPI (CryptProtectData, область — текущий пользователь).
    /// Расшифровать их может только этот же пользователь Windows на этом же компьютере:
    /// скопированный .env на другом ПК бесполезен.
    /// В файле зашифрованное значение хранится как <c>dpapi:&lt;base64&gt;</c>.
    /// </summary>
    public static class SecretProtector
    {
        public const string Prefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Undertone.Tokens.v1");

        public static bool IsProtected(string? value) =>
            value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

        public static string Protect(string plain)
        {
            if (plain.Length == 0) return "";
            var data = Encoding.UTF8.GetBytes(plain);
            return Prefix + Convert.ToBase64String(Transform(data, protect: true));
        }

        /// <summary>Возвращает исходную строку. Незашифрованное значение (старый формат) возвращается как есть.</summary>
        public static string? Unprotect(string? stored)
        {
            if (stored == null || !IsProtected(stored)) return stored;
            try
            {
                var data = Convert.FromBase64String(stored.Substring(Prefix.Length));
                return Encoding.UTF8.GetString(Transform(data, protect: false));
            }
            catch (Exception ex) when (ex is FormatException or Win32Exception)
            {
                // файл с другого компьютера / другого пользователя — токен не прочитать, нужен новый вход
                Log.Warn("Не удалось расшифровать токен", ex);
                return null;
            }
        }

        private static byte[] Transform(byte[] input, bool protect)
        {
            var inBlob = new DataBlob();
            var entropyBlob = new DataBlob();
            var outBlob = new DataBlob();
            var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            try
            {
                inBlob.cbData = input.Length;
                inBlob.pbData = inHandle.AddrOfPinnedObject();
                entropyBlob.cbData = Entropy.Length;
                entropyBlob.pbData = entropyHandle.AddrOfPinnedObject();

                var ok = protect
                    ? CryptProtectData(ref inBlob, "Undertone", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                inHandle.Free();
                entropyHandle.Free();
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
