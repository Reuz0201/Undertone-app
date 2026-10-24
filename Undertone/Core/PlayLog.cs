using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Undertone.Core
{
    /// <summary>Одно прослушивание: когда, какой трек и сколько секунд реально играло.</summary>
    public readonly record struct PlayEvent(DateTime At, string TrackId, int Seconds);

    /// <summary>
    /// Журнал прослушиваний для «Итогов»: %LOCALAPPDATA%\Undertone\plays-&lt;uid&gt;.log, строка на прослушивание
    /// («2026-10-24T20:31:05	12345	187»). Только дописывается — дёшево даже на тысячи треков в год.
    /// Названия и обложки берутся из статистики (PlayStats), здесь только id.
    /// </summary>
    public static class PlayLog
    {
        /// <summary>Меньше — это не прослушивание, а пролистывание.</summary>
        public const int MinSeconds = 30;

        private static readonly object Sync = new();

        private static string PathFor(long uid) => Path.Combine(EnvFile.DataDirectory, $"plays-{uid}.log");

        public static string Format(PlayEvent e) =>
            $"{e.At.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\t{e.TrackId}\t{e.Seconds}";

        public static bool TryParse(string line, out PlayEvent e)
        {
            e = default;
            var parts = line.Split('\t');
            if (parts.Length < 3 || parts[1].Length == 0) return false;
            if (!DateTime.TryParseExact(parts[0], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                return false;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
                return false;
            e = new PlayEvent(at, parts[1], seconds);
            return true;
        }

        public static void Append(long uid, PlayEvent e)
        {
            try
            {
                lock (Sync)
                {
                    var path = PathFor(uid);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, Format(e) + "\n");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Прослушивание не записано в журнал итогов", ex);
            }
        }

        public static List<PlayEvent> Load(long uid)
        {
            var list = new List<PlayEvent>();
            try
            {
                var path = PathFor(uid);
                if (!File.Exists(path)) return list;
                lock (Sync)
                    foreach (var line in File.ReadLines(path))
                        if (TryParse(line, out var e)) list.Add(e);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Журнал итогов не прочитан", ex);
            }
            return list;
        }

        public static void Delete(long? uid)
        {
            if (uid == null) return;
            try { File.Delete(PathFor(uid.Value)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
