using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Undertone.Core
{
    /// <summary>Что играло при выходе — чтобы после перезапуска предложить продолжить с того же места.</summary>
    public sealed class PlaybackSession
    {
        public long Uid { get; set; }
        public List<YaTrack> Tracks { get; set; } = new();
        public int Index { get; set; }
        public double PositionSeconds { get; set; }
        public QueueSource Source { get; set; }
        public string Label { get; set; } = "";
        public DateTime SavedAt { get; set; }
    }

    /// <summary>%LOCALAPPDATA%\Undertone\session.json</summary>
    public static class SessionStore
    {
        private static string FilePath => Path.Combine(EnvFile.DataDirectory, "session.json");

        public static PlaybackSession? Load(long uid)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var s = JsonSerializer.Deserialize<PlaybackSession>(File.ReadAllText(FilePath));
                if (s == null || s.Uid != uid || s.Tracks.Count == 0) return null;
                if (s.Index < 0 || s.Index >= s.Tracks.Count) return null;
                return s;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Warn("session.json не прочитан", ex);
                return null;
            }
        }

        public static void Save(PlaybackSession session)
        {
            try
            {
                session.SavedAt = DateTime.UtcNow;
                Directory.CreateDirectory(EnvFile.DataDirectory);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(session));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Warn("session.json не сохранён", ex);
            }
        }

        public static void Clear()
        {
            try { File.Delete(FilePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
