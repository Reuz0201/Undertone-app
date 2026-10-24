using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>Сколько раз и когда играл каждый трек — для умных плейлистов. %LOCALAPPDATA%\Undertone\stats-&lt;uid&gt;.json</summary>
    public sealed class PlayStats
    {
        public Dictionary<string, TrackStat> Tracks { get; set; } = new();

        public void Count(YaTrack track, DateTime when)
        {
            if (!Tracks.TryGetValue(track.Id, out var s)) Tracks[track.Id] = s = new TrackStat { First = when };
            s.Track = track;
            s.Plays++;
            s.Last = when;
            s.HourCounts ??= new int[24];
            s.HourCounts[when.Hour]++;
        }

        public TrackStat? Get(string id) => Tracks.TryGetValue(id, out var s) ? s : null;

        private static string FileFor(long uid) => Path.Combine(EnvFile.DataDirectory, $"stats-{uid}.json");

        public static PlayStats Load(long uid)
        {
            try
            {
                var path = FileFor(uid);
                if (File.Exists(path)) return JsonSerializer.Deserialize<PlayStats>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn("Статистика прослушиваний не прочитана", ex);
            }
            return new();
        }

        public async Task SaveAsync(long uid)
        {
            try
            {
                var json = JsonSerializer.Serialize(this);
                var path = FileFor(uid);
                await File.WriteAllTextAsync(path + ".tmp", json);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Статистика прослушиваний не сохранена", ex);
            }
        }
    }

    public sealed class TrackStat
    {
        public YaTrack Track { get; set; } = new();
        public int Plays { get; set; }
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
        /// <summary>Сколько раз играл в каждый час суток (0..23).</summary>
        public int[]? HourCounts { get; set; }
    }
}
