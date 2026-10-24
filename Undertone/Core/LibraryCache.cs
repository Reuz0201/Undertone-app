using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Локальная копия «Мне нравится»: при запуске список показывается сразу из файла,
    /// а с сервера догружаются только новые треки. %LOCALAPPDATA%\Undertone\library-&lt;uid&gt;.json
    /// </summary>
    public static class LibraryCache
    {
        private sealed class Snapshot
        {
            /// <summary>2 — в треках появились флаги текста песни; старые копии перечитываются с сервера.</summary>
            public int Version { get; set; } = CurrentVersion;
            public int Revision { get; set; }
            public List<YaTrack> Liked { get; set; } = new();
        }

        private const int CurrentVersion = 2;

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

        private static string PathFor(long uid) => Path.Combine(EnvFile.DataDirectory, $"library-{uid}.json");

        public static (int Revision, List<YaTrack> Liked)? Load(long uid)
        {
            try
            {
                var path = PathFor(uid);
                if (!File.Exists(path)) return null;
                var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Options);
                if (snap == null || snap.Version != CurrentVersion) return null;
                return (snap.Revision, snap.Liked.Where(t => t.Id.Length > 0).ToList());
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Warn("Локальная копия «Мне нравится» не прочитана", ex);
                return null;
            }
        }

        public static Task SaveAsync(long uid, int revision, IReadOnlyList<YaTrack> liked)
        {
            // снимок списка берём в вызывающем потоке, пишем в фоне
            var snap = new Snapshot { Revision = revision, Liked = liked.ToList() };
            return Task.Run(() =>
            {
                try
                {
                    var path = PathFor(uid);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(snap, Options));
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    Log.Warn("Локальная копия «Мне нравится» не сохранена", ex);
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

        /// <summary>
        /// Собирает свежий список: порядок — как в ids с сервера, данные — из кэша, недостающие — догружаются.
        /// </summary>
        public static async Task<List<YaTrack>> MergeAsync(IMusicService api, IReadOnlyList<string> ids,
            IReadOnlyList<YaTrack> cached, CancellationToken ct = default)
        {
            var known = new Dictionary<string, YaTrack>();
            foreach (var t in cached) known[t.Id] = t;

            var missing = ids.Where(full => !known.ContainsKey(full.Split(':')[0])).ToList();
            if (missing.Count > 0)
                foreach (var t in await api.GetTracksAsync(missing, ct))
                    known[t.Id] = t;

            var result = new List<YaTrack>(ids.Count);
            foreach (var full in ids)
                if (known.TryGetValue(full.Split(':')[0], out var t))
                    result.Add(t);
            return result;
        }
    }
}
