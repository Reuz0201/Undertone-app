using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Undertone.Core
{
    /// <summary>
    /// Свои файлы: mp3/flac/m4a/wav из выбранных папок. Теги и обложки читает сама Windows (как Проводник),
    /// результат хранится в индексе, чтобы при запуске не перечитывать тысячи файлов.
    /// Трек выглядит как обычный YaTrack с id «local-…», поэтому играет в тех же очередях и плейлистах.
    /// </summary>
    public sealed class LocalLibrary
    {
        public const string IdPrefix = "local-";
        public static readonly string[] Extensions = { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".wma" };

        private Dictionary<string, Entry> _byId = new();
        private readonly object _sync = new();

        private static string IndexPath => Path.Combine(EnvFile.DataDirectory, "local-library.json");
        private static string CoverDir => Path.Combine(EnvFile.DataDirectory, "local-covers");

        public static bool IsLocal(YaTrack t) => t.Id.StartsWith(IdPrefix, StringComparison.Ordinal);

        public int Count
        {
            get { lock (_sync) return _byId.Count; }
        }

        public string? PathFor(string id)
        {
            lock (_sync) return _byId.TryGetValue(id, out var e) && File.Exists(e.Path) ? e.Path : null;
        }

        public List<YaTrack> Tracks()
        {
            lock (_sync)
                return _byId.Values
                    .OrderBy(e => e.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(e => e.Album, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(e => e.TrackNo).ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Select(ToTrack).ToList();
        }

        /// <summary>Поиск по названию, артисту, альбому — для Ctrl+K.</summary>
        public List<YaTrack> Search(string query, int max)
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            lock (_sync)
                return _byId.Values
                    .Where(e => words.All(w => $"{e.Title} {e.Artist} {e.Album}".Contains(w, StringComparison.CurrentCultureIgnoreCase)))
                    .Take(max).Select(ToTrack).ToList();
        }

        private static YaTrack ToTrack(Entry e) => new()
        {
            Id = e.Id,
            Title = e.Title,
            Artists = e.Artist.Length > 0 ? new[] { e.Artist } : Array.Empty<string>(),
            ArtistRefs = e.Artist.Length > 0 ? new[] { new YaArtistRef("", e.Artist) } : Array.Empty<YaArtistRef>(),
            Album = e.Album,
            DurationMs = e.DurationMs,
            CoverUri = e.Cover.Length > 0 && File.Exists(e.Cover) ? new Uri(e.Cover).AbsoluteUri : null,
        };

        public static string IdFor(string path)
        {
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
            return IdPrefix + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        // ────────── индекс ──────────

        public void LoadIndex()
        {
            try
            {
                if (!File.Exists(IndexPath)) return;
                var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(IndexPath)) ?? new();
                lock (_sync) _byId = list.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn("Индекс своих файлов не прочитан — пересоберу", ex);
            }
        }

        private void SaveIndex()
        {
            try
            {
                List<Entry> list;
                lock (_sync) list = _byId.Values.ToList();
                Directory.CreateDirectory(EnvFile.DataDirectory);
                var tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(list));
                File.Move(tmp, IndexPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Индекс своих файлов не сохранён", ex);
            }
        }

        /// <summary>Пересканировать папки: новые файлы читаются, неизменённые берутся из индекса, удалённые пропадают.</summary>
        public async Task<int> ScanAsync(IReadOnlyList<string> folders, IProgress<int>? progress, CancellationToken ct)
        {
            var files = await Task.Run(() => folders.Where(Directory.Exists).SelectMany(EnumerateMusic).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);
            Dictionary<string, Entry> old;
            lock (_sync) old = new Dictionary<string, Entry>(_byId);
            var fresh = new Dictionary<string, Entry>();
            Directory.CreateDirectory(CoverDir);
            int done = 0;
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var id = IdFor(path);
                var info = new FileInfo(path);
                if (old.TryGetValue(id, out var known) && known.Size == info.Length && known.Modified == info.LastWriteTimeUtc.Ticks)
                    fresh[id] = known;
                else if (await ReadAsync(path, id, info) is { } entry)
                    fresh[id] = entry;
                if (++done % 25 == 0) progress?.Report(done);
            }
            lock (_sync) _byId = fresh;
            SaveIndex();
            progress?.Report(done);
            return fresh.Count;
        }

        private static IEnumerable<string> EnumerateMusic(string folder)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
            return files.Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }

        /// <summary>Теги через Windows (то же, что показывает Проводник). Нет тегов — название из имени файла.</summary>
        private static async Task<Entry?> ReadAsync(string path, string id, FileInfo info)
        {
            var entry = new Entry
            {
                Id = id, Path = path, Size = info.Length, Modified = info.LastWriteTimeUtc.Ticks,
                Title = Path.GetFileNameWithoutExtension(path),
            };
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var music = await file.Properties.GetMusicPropertiesAsync();
                if (!string.IsNullOrWhiteSpace(music.Title)) entry.Title = music.Title.Trim();
                entry.Artist = (!string.IsNullOrWhiteSpace(music.Artist) ? music.Artist : music.AlbumArtist ?? "").Trim();
                entry.Album = (music.Album ?? "").Trim();
                entry.TrackNo = (int)music.TrackNumber;
                entry.DurationMs = (long)music.Duration.TotalMilliseconds;
                // «Артист - Название.mp3» без тегов — разбираем имя
                if (entry.Artist.Length == 0 && entry.Title.Contains(" - "))
                {
                    var parts = entry.Title.Split(" - ", 2);
                    entry.Artist = parts[0].Trim();
                    entry.Title = parts[1].Trim();
                }
                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 300, ThumbnailOptions.UseCurrentScale);
                if (thumb != null && thumb.Type == ThumbnailType.Image && thumb.Size > 0)
                {
                    var cover = Path.Combine(CoverDir, id + ".jpg");
                    await using var src = thumb.AsStreamForRead();
                    await using var dst = File.Create(cover);
                    await src.CopyToAsync(dst);
                    entry.Cover = cover;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Теги не прочитаны: {path}", ex);
            }
            return entry;
        }

        public sealed class Entry
        {
            public string Id { get; set; } = "";
            public string Path { get; set; } = "";
            public long Size { get; set; }
            public long Modified { get; set; }
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public string Album { get; set; } = "";
            public int TrackNo { get; set; }
            public long DurationMs { get; set; }
            public string Cover { get; set; } = "";
        }
    }
}
