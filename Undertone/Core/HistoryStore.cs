using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Undertone.Core
{
    public sealed class HistoryEntry
    {
        public YaTrack Track { get; set; } = new();
        public DateTime PlayedAt { get; set; }
    }

    /// <summary>Локальная история прослушиваний: последние 300 треков, %LOCALAPPDATA%\Undertone\history-&lt;uid&gt;.json</summary>
    public static class HistoryStore
    {
        public const int MaxEntries = 300;

        private static string PathFor(long uid) => Path.Combine(EnvFile.DataDirectory, $"history-{uid}.json");

        public static List<HistoryEntry> Load(long uid)
        {
            try
            {
                var path = PathFor(uid);
                if (!File.Exists(path)) return new List<HistoryEntry>();
                return (JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path)) ?? new List<HistoryEntry>())
                    .Where(e => e.Track.Id.Length > 0).Take(MaxEntries).ToList();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Warn("История не прочитана", ex);
                return new List<HistoryEntry>();
            }
        }

        public static Task SaveAsync(long uid, IReadOnlyList<HistoryEntry> entries)
        {
            var copy = entries.Take(MaxEntries).ToList();
            return Task.Run(() =>
            {
                try
                {
                    var path = PathFor(uid);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(copy));
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    Log.Warn("История не сохранена", ex);
                }
            });
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
