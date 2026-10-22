using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Кэш mp3 в %TEMP%\Undertone. Параллельные запросы одного трека
    /// (воспроизведение + предзагрузка) сливаются в одну закачку.
    /// </summary>
    public sealed class AudioCache
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
        private readonly ConcurrentDictionary<string, Task<string>> _inflight = new();
        private readonly long _maxBytes;

        public string Directory { get; }

        public AudioCache(long maxBytes = 1L << 30)
        {
            _maxBytes = maxBytes;
            Directory = Path.Combine(Path.GetTempPath(), "Undertone");
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string PathFor(string trackId) => Path.Combine(Directory, $"{trackId}.mp3");

        public bool Has(string trackId) => File.Exists(PathFor(trackId));

        /// <param name="resolveUrl">Получает свежую подписанную ссылку (она живёт недолго).</param>
        public Task<string> GetAsync(string trackId, Func<Task<string>> resolveUrl, CancellationToken ct = default)
        {
            var path = PathFor(trackId);
            if (File.Exists(path))
            {
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
                return Task.FromResult(path);
            }

            var task = _inflight.GetOrAdd(trackId, _ => DownloadAsync(trackId, path, resolveUrl));
            return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
        }

        private async Task<string> DownloadAsync(string trackId, string path, Func<Task<string>> resolveUrl)
        {
            // уходим с вызывающего потока, чтобы задача гарантированно попала в _inflight до finally
            await Task.Yield();
            var part = path + ".part";
            try
            {
                var url = await resolveUrl().ConfigureAwait(false);
                using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }
                File.Move(part, path, overwrite: true);
                return path;
            }
            catch
            {
                try { File.Delete(part); } catch (IOException) { }
                throw;
            }
            finally
            {
                _inflight.TryRemove(trackId, out _);
            }
        }

        /// <summary>Удаляет старые файлы, пока кэш больше лимита.</summary>
        public void Trim()
        {
            try
            {
                var files = new DirectoryInfo(Directory).GetFiles()
                    .OrderByDescending(f => f.LastAccessTimeUtc).ToList();
                long total = 0;
                foreach (var f in files)
                {
                    if (f.Name.EndsWith(".part", StringComparison.Ordinal)) { TryDelete(f); continue; }
                    total += f.Length;
                    if (total > _maxBytes) TryDelete(f);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void TryDelete(FileInfo f)
        {
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
