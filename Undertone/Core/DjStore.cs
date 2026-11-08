using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Готовые анализы треков для DJ-режима — %LOCALAPPDATA%\Undertone\dj-analysis.json.
    /// Анализ трека не меняется, а считать его — пара секунд процессора, поэтому храним насовсем
    /// (не больше <see cref="MaxEntries"/>, самые старые вытесняются).
    /// </summary>
    public sealed class DjStore
    {
        public const int MaxEntries = 5000;

        private readonly object _lock = new();
        private readonly string? _path;
        private Dictionary<string, Entry> _items = new();
        private bool _dirty;

        public sealed class Entry
        {
            public TrackAnalysis Analysis { get; set; } = new();
            public DateTime Used { get; set; }
        }

        /// <summary>path = null — только в памяти (тесты).</summary>
        public DjStore(string? path)
        {
            _path = path;
            if (path == null) return;
            try
            {
                if (File.Exists(path))
                    _items = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn("Анализы треков для DJ-режима не прочитаны — посчитаются заново", ex);
            }
        }

        public static DjStore Open() => new(Path.Combine(EnvFile.DataDirectory, "dj-analysis.json"));

        public int Count
        {
            get { lock (_lock) return _items.Count; }
        }

        public TrackAnalysis? Get(string trackId)
        {
            lock (_lock)
            {
                if (!_items.TryGetValue(trackId, out var e) || e.Analysis.Version != TrackAnalysis.CurrentVersion) return null;
                e.Used = DateTime.UtcNow;
                return e.Analysis;
            }
        }

        public void Put(string trackId, TrackAnalysis analysis)
        {
            lock (_lock)
            {
                _items[trackId] = new Entry { Analysis = analysis, Used = DateTime.UtcNow };
                if (_items.Count > MaxEntries)
                {
                    var oldest = new List<KeyValuePair<string, Entry>>(_items);
                    oldest.Sort((a, b) => a.Value.Used.CompareTo(b.Value.Used));
                    for (int i = 0; i < _items.Count - MaxEntries; i++) _items.Remove(oldest[i].Key);
                }
                _dirty = true;
            }
        }

        public async Task SaveAsync()
        {
            if (_path == null) return;
            string json;
            lock (_lock)
            {
                if (!_dirty) return;
                json = JsonSerializer.Serialize(_items);
                _dirty = false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                await File.WriteAllTextAsync(_path + ".tmp", json);
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Анализы треков для DJ-режима не сохранены", ex);
            }
        }
    }
}
