using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Undertone.Core
{
    /// <summary>Что скачано для офлайна: коллекция (лайки, альбом, плейлист) → её треки. Эти файлы кэш не удаляет.</summary>
    public sealed class OfflineStore
    {
        public Dictionary<string, OfflineCollection> Collections { get; set; } = new();

        [JsonIgnore] private string _path = "";
        [JsonIgnore] private HashSet<string> _pinned = new();

        public bool IsPinned(string trackId)
        {
            lock (_pinned) return _pinned.Contains(trackId);
        }

        public int PinnedCount
        {
            get { lock (_pinned) return _pinned.Count; }
        }

        public bool Has(string key) => Collections.ContainsKey(key);

        public void Set(string key, string title, IEnumerable<string> trackIds)
        {
            Collections[key] = new OfflineCollection { Title = title, TrackIds = trackIds.Distinct().ToList(), Added = DateTime.Now };
            Rebuild();
            Save();
        }

        public void Remove(string key)
        {
            if (Collections.Remove(key)) { Rebuild(); Save(); }
        }

        public void Clear()
        {
            Collections.Clear();
            Rebuild();
            Save();
        }

        private void Rebuild()
        {
            var all = Collections.Values.SelectMany(c => c.TrackIds).ToHashSet();
            lock (_pinned) _pinned = all;
        }

        private static string FileFor(long uid) => Path.Combine(EnvFile.DataDirectory, $"offline-{uid}.json");

        public static OfflineStore Load(long uid)
        {
            var path = FileFor(uid);
            OfflineStore store;
            try
            {
                store = File.Exists(path) ? JsonSerializer.Deserialize<OfflineStore>(File.ReadAllText(path)) ?? new() : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn("Список офлайн-треков не прочитан", ex);
                store = new();
            }
            store._path = path;
            store.Rebuild();
            return store;
        }

        public void Save()
        {
            if (_path.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Список офлайн-треков не сохранён", ex);
            }
        }
    }

    public sealed class OfflineCollection
    {
        public string Title { get; set; } = "";
        public List<string> TrackIds { get; set; } = new();
        public DateTime Added { get; set; }
    }
}
