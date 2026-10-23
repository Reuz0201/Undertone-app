using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Undertone.Core
{
    /// <summary>
    /// Простой журнал в %LOCALAPPDATA%\Undertone\logs: по файлу на день, хранится 7 последних.
    /// Пишет ошибки API, воспроизведения и обновлений — по ним можно разобрать баг у друга.
    /// Потокобезопасен; сам никогда не бросает исключений.
    /// </summary>
    public static class Log
    {
        private const int KeepDays = 7;
        private const long MaxFileBytes = 5L << 20;
        private static readonly object Sync = new();
        private static bool _cleaned;

        public static string Directory => Path.Combine(EnvFile.DataDirectory, "logs");

        public static void Info(string message) => Write("INFO ", message, null);
        public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
        public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception? ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                  .Append(' ').Append(level).Append(' ').Append(message);
                if (ex != null) sb.Append(" | ").Append(Describe(ex));
                sb.Append(Environment.NewLine);

                lock (Sync)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    if (!_cleaned)
                    {
                        _cleaned = true;
                        Cleanup();
                    }
                    var path = Path.Combine(Directory, $"undertone-{DateTime.Now:yyyy-MM-dd}.log");
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxFileBytes) return; // защита от лавины одинаковых ошибок
                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // журнал не должен ронять приложение
            }
        }

        /// <summary>Исключение целиком, с цепочкой InnerException, но без мусора в одну строку.</summary>
        private static string Describe(Exception ex)
        {
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" → ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            if (ex.StackTrace is { Length: > 0 } stack)
                sb.Append(Environment.NewLine).Append(stack);
            return sb.ToString();
        }

        private static void Cleanup()
        {
            try
            {
                var old = new DirectoryInfo(Directory).GetFiles("undertone-*.log")
                    .OrderByDescending(f => f.Name)
                    .Skip(KeepDays);
                foreach (var f in old)
                {
                    try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
