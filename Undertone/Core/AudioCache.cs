using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Кэш аудио в %LOCALAPPDATA%\Undertone\cache (раньше был в %TEMP%, и его стирала очистка диска).
    /// Параллельные запросы одного трека (воспроизведение + предзагрузка) сливаются в одну закачку.
    /// Пока файл качается, его можно читать — см. <see cref="CacheDownload"/> и потоковое воспроизведение.
    /// </summary>
    public sealed class AudioCache
    {
        /// <summary>Обрыв, если сервер молчит дольше этого (а не ограничение на весь файл).</summary>
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);
        private const int Attempts = 3;

        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        private readonly ConcurrentDictionary<string, CacheDownload> _inflight = new();
        private int _downloadsSinceTrim;

        public string Directory { get; }

        /// <summary>Лимит размера; меняется на странице настроек. Скачанное для офлайна в него не входит.</summary>
        public long MaxBytes { get; set; }

        /// <summary>Трек скачан для офлайна — его файлы не удаляются ни по лимиту, ни очисткой кэша.</summary>
        public Func<string, bool>? IsPinned { get; set; }

        /// <summary>id трека из имени файла: 123.mp3, 123.lq.mp3, 123.flac → 123.</summary>
        internal static string TrackIdOf(string fileName)
        {
            var dot = fileName.IndexOf('.');
            return dot > 0 ? fileName[..dot] : fileName;
        }

        private bool Pinned(FileInfo f) => IsPinned?.Invoke(TrackIdOf(f.Name)) == true;

        /// <summary>Сколько занимает скачанное для офлайна.</summary>
        public (long Bytes, int Files) GetPinnedUsage()
        {
            try
            {
                long total = 0;
                int count = 0;
                foreach (var f in new DirectoryInfo(Directory).EnumerateFiles())
                {
                    if (f.Name.EndsWith(".part", StringComparison.Ordinal) || !Pinned(f)) continue;
                    total += f.Length;
                    count++;
                }
                return (total, count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (0, 0);
            }
        }

        public static string DefaultDirectory => Path.Combine(EnvFile.DataDirectory, "cache");
        private static string LegacyDirectory => Path.Combine(Path.GetTempPath(), "Undertone");

        public AudioCache(long maxBytes, string? directory = null)
        {
            MaxBytes = maxBytes;
            Directory = directory ?? DefaultDirectory;
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string PathFor(string trackId, string ext = "mp3") => Path.Combine(Directory, $"{trackId}.{ext}");

        public bool Has(string trackId, string ext = "mp3") => File.Exists(PathFor(trackId, ext));

        public bool IsDownloading(string trackId, string ext = "mp3") => _inflight.ContainsKey(Key(trackId, ext));

        /// <summary>Первый готовый файл трека из перечисленных форматов (например, flac или m4a для lossless).</summary>
        public string? FindCached(string trackId, params string[] exts)
        {
            foreach (var ext in exts)
            {
                var path = PathFor(trackId, ext);
                if (File.Exists(path))
                {
                    Touch(path);
                    return path;
                }
            }
            return null;
        }

        private static string Key(string trackId, string ext) => $"{trackId}.{ext}";

        /// <summary>Готовый файл: ждёт, пока трек докачается целиком.</summary>
        /// <param name="resolveUrl">Получает свежую подписанную ссылку (она живёт недолго).</param>
        public Task<string> GetAsync(string trackId, Func<Task<string>> resolveUrl, CancellationToken ct = default, string ext = "mp3") =>
            GetAsync(trackId, async () => new CacheSource(await resolveUrl().ConfigureAwait(false), null), ext, ct);

        /// <summary>Готовый файл; источник может быть зашифрован (Key) — тогда он расшифруется после скачивания.</summary>
        public async Task<string> GetAsync(string trackId, Func<Task<CacheSource>> resolve, string ext, CancellationToken ct = default)
        {
            var path = PathFor(trackId, ext);
            if (File.Exists(path))
            {
                Touch(path);
                return path;
            }
            var download = Start(trackId, ext, resolve);
            return ct.CanBeCanceled ? await download.Completion.WaitAsync(ct) : await download.Completion;
        }

        /// <summary>
        /// Для потокового воспроизведения: если файл уже есть — путь к нему, иначе — идущая закачка,
        /// из которой можно читать, не дожидаясь конца.
        /// </summary>
        public CacheEntry Open(string trackId, Func<Task<string>> resolveUrl, string ext = "mp3")
        {
            var path = PathFor(trackId, ext);
            if (File.Exists(path))
            {
                Touch(path);
                return new CacheEntry(path, null);
            }
            return new CacheEntry(null, Start(trackId, ext, async () => new CacheSource(await resolveUrl().ConfigureAwait(false), null)));
        }

        /// <summary>То же, но источник может быть HLS — списком сегментов (SoundCloud).</summary>
        public CacheEntry Open(string trackId, Func<Task<CacheSource>> resolve, string ext = "mp3")
        {
            var path = PathFor(trackId, ext);
            if (File.Exists(path))
            {
                Touch(path);
                return new CacheEntry(path, null);
            }
            return new CacheEntry(null, Start(trackId, ext, resolve));
        }

        private CacheDownload Start(string trackId, string ext, Func<Task<CacheSource>> resolveUrl)
        {
            var key = Key(trackId, ext);
            while (true)
            {
                if (_inflight.TryGetValue(key, out var existing)) return existing;
                var download = new CacheDownload(trackId, PathFor(trackId, ext));
                if (!_inflight.TryAdd(key, download)) continue;
                _ = RunAsync(download, key, resolveUrl);
                return download;
            }
        }

        private async Task RunAsync(CacheDownload d, string key, Func<Task<CacheSource>> resolveUrl)
        {
            string? aesKey = null;
            // уходим с вызывающего потока: разбор заголовков и запись — в фоне
            await Task.Yield();
            Exception? last = null;
            // сколько сегментов HLS уже записано: при повторе докачиваем со следующего
            var segmentsDone = new SegmentProgress();
            try
            {
                await using var dst = new FileStream(d.PartPath, FileMode.Create, FileAccess.Write,
                    FileShare.Read | FileShare.Delete, 1 << 16, useAsync: true);

                for (int attempt = 1; attempt <= Attempts; attempt++)
                {
                    try
                    {
                        // ссылка подписана и живёт недолго — на каждую попытку берём свежую
                        var source = await resolveUrl().ConfigureAwait(false);
                        // ключ привязан к ссылке: при докачке со свежей ссылкой он тот же, но берём последний
                        aesKey = source.Key;
                        if (source.Segments is { Count: > 0 } segments)
                            await CopySegmentsAsync(segments, d, dst, segmentsDone).ConfigureAwait(false);
                        else
                            await CopyAsync(source.Url, d, dst).ConfigureAwait(false);
                        last = null;
                        break;
                    }
                    catch (Exception ex) when ((ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException)
                                               && attempt < Attempts)
                    {
                        last = ex;
                        Log.Warn($"Скачивание {d.TrackId}: попытка {attempt} не удалась, повторяю", ex);
                        await Task.Delay(TimeSpan.FromSeconds(attempt), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                if (last != null) throw last;

                await dst.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TryDeleteFile(d.PartPath);
                _inflight.TryRemove(key, out _);
                if (ex is not YaApiException) Log.Error($"Скачивание {d.TrackId} не удалось", ex);
                d.Fail(ex);
                return;
            }

            try
            {
                if (aesKey != null)
                {
                    // зашифрованный файл (lossless): расшифровываем во временный и подменяем
                    var tmp = d.FinalPath + ".dec";
                    await Task.Run(() => TrackCrypto.DecryptFile(d.PartPath, tmp, aesKey)).ConfigureAwait(false);
                    File.Move(tmp, d.FinalPath, overwrite: true);
                    TryDeleteFile(d.PartPath);
                }
                else
                {
                    // .part открыт у потокового читателя с FileShare.Delete — переименование это допускает
                    File.Move(d.PartPath, d.FinalPath, overwrite: true);
                }
                _inflight.TryRemove(key, out _);
                d.Complete();
            }
            catch (Exception ex)
            {
                _inflight.TryRemove(key, out _);
                Log.Error($"Не удалось сохранить {d.TrackId} в кэш", ex);
                d.Fail(ex);
                return;
            }

            if (Interlocked.Increment(ref _downloadsSinceTrim) >= 5)
            {
                Interlocked.Exchange(ref _downloadsSinceTrim, 0);
                _ = Task.Run(Trim);
            }
        }

        /// <summary>Качает с текущего места (докачка через Range после обрыва).</summary>
        private static async Task CopyAsync(string url, CacheDownload d, FileStream dst)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var from = d.Written;
            if (from > 0) req.Headers.Range = new RangeHeaderValue(from, null);

            using var idle = new CancellationTokenSource(IdleTimeout);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            if (from > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                // сервер не умеет докачку — начинаем заново, если никто ещё не читал из файла
                if (d.HasReaders) throw new IOException("Сервер не поддерживает докачку");
                dst.SetLength(0);
                dst.Position = 0;
                d.ResetWritten();
                from = 0;
            }

            var length = resp.Content.Headers.ContentLength;
            if (length is > 0) d.SetTotal(from + length.Value);

            await using var src = await resp.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            var buffer = new byte[1 << 16];
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                int read;
                try
                {
                    read = await src.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested)
                {
                    throw new TimeoutException($"Сервер не присылал данные {IdleTimeout.TotalSeconds:0} с");
                }
                if (read == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                // FlushAsync — чтобы читатель .part видел данные сразу, а не через 64 КБ буфера
                await dst.FlushAsync().ConfigureAwait(false);
                d.AddWritten(read);
            }

            if (d.TotalBytes > 0 && d.Written < d.TotalBytes)
                throw new IOException($"Связь оборвалась: получено {d.Written} из {d.TotalBytes} байт");
        }

        private sealed class SegmentProgress
        {
            public int Done;
        }

        /// <summary>
        /// HLS: сегменты по порядку, каждый целиком в память и только потом в файл — так при обрыве в .part
        /// не остаётся половины сегмента, а читатель потока видит только целые куски. ID3-теги в начале
        /// сегментов вырезаются: посреди склеенного MP3 они мешают декодеру.
        /// </summary>
        private static async Task CopySegmentsAsync(IReadOnlyList<string> segments, CacheDownload d, FileStream dst,
            SegmentProgress progress)
        {
            for (var i = progress.Done; i < segments.Count; i++)
            {
                using var idle = new CancellationTokenSource(IdleTimeout);
                byte[] data;
                try
                {
                    using var resp = await Http.GetAsync(segments[i], idle.Token).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    data = await resp.Content.ReadAsByteArrayAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested)
                {
                    throw new TimeoutException($"Сегмент {i + 1} из {segments.Count} не загрузился за {IdleTimeout.TotalSeconds:0} с");
                }

                var (offset, count) = StripId3(data);
                await dst.WriteAsync(data.AsMemory(offset, count)).ConfigureAwait(false);
                await dst.FlushAsync().ConfigureAwait(false);
                d.AddWritten(count);
                progress.Done = i + 1;
            }
        }

        /// <summary>Границы звука в сегменте без ведущих ID3v2-тегов (размер тега — syncsafe, по 7 бит в байте).</summary>
        internal static (int Offset, int Count) StripId3(byte[] data)
        {
            var offset = 0;
            while (data.Length - offset >= 10 && data[offset] == 'I' && data[offset + 1] == 'D' && data[offset + 2] == '3')
            {
                var size = (data[offset + 6] & 0x7F) << 21 | (data[offset + 7] & 0x7F) << 14 |
                           (data[offset + 8] & 0x7F) << 7 | (data[offset + 9] & 0x7F);
                var footer = (data[offset + 5] & 0x10) != 0 ? 10 : 0;
                var next = offset + 10 + size + footer;
                if (next > data.Length) break;
                offset = next;
            }
            return (offset, data.Length - offset);
        }

        // ────────── размер, очистка ──────────

        /// <summary>Сколько места занимает кэш (байты, число файлов).</summary>
        public (long Bytes, int Files) GetUsage()
        {
            try
            {
                long total = 0;
                int count = 0;
                foreach (var f in new DirectoryInfo(Directory).EnumerateFiles())
                {
                    if (Pinned(f)) continue; // скачанное для офлайна считаем отдельно
                    total += f.Length;
                    count++;
                }
                return (total, count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (0, 0);
            }
        }

        /// <summary>Удаляет файлы офлайн-треков (после «Удалить скачанное»), кроме тех, что сейчас качаются.</summary>
        public long DeleteTracks(IReadOnlyCollection<string> trackIds)
        {
            long freed = 0;
            try
            {
                var ids = trackIds.ToHashSet();
                var busy = _inflight.Values.Select(d => d.PartPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in new DirectoryInfo(Directory).GetFiles())
                {
                    if (busy.Contains(f.FullName) || !ids.Contains(TrackIdOf(f.Name))) continue;
                    var size = f.Length;
                    if (TryDelete(f)) freed += size;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return freed;
        }

        /// <summary>Удаляет весь кэш, кроме файлов, которые сейчас качаются или играют. Возвращает освобождённые байты.</summary>
        public long Clear()
        {
            long freed = 0;
            try
            {
                var busy = _inflight.Values.Select(d => d.PartPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in new DirectoryInfo(Directory).GetFiles())
                {
                    if (busy.Contains(f.FullName) || Pinned(f)) continue;
                    var size = f.Length;
                    if (TryDelete(f)) freed += size;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Очистка кэша прервана", ex);
            }
            Log.Info($"Кэш очищен, освобождено {freed / (1024 * 1024)} МБ");
            return freed;
        }

        /// <summary>Удаляет самые давно игравшие файлы, пока кэш больше лимита. Брошенные .part — тоже.</summary>
        public void Trim()
        {
            try
            {
                var busy = _inflight.Values.Select(d => d.PartPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var files = new DirectoryInfo(Directory).GetFiles()
                    .OrderByDescending(f => f.LastAccessTimeUtc).ToList();
                long total = 0;
                foreach (var f in files)
                {
                    if (f.Name.EndsWith(".part", StringComparison.Ordinal) || f.Name.EndsWith(".dec", StringComparison.Ordinal))
                    {
                        if (!busy.Contains(f.FullName)) TryDelete(f);
                        continue;
                    }
                    if (Pinned(f)) continue; // скачанное для офлайна не трогаем и в лимит не считаем
                    total += f.Length;
                    if (total > MaxBytes) TryDelete(f);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Переносит кэш из старой папки %TEMP%\Undertone (версии до 2.1).</summary>
        public void MigrateLegacy()
        {
            try
            {
                var old = LegacyDirectory;
                if (!System.IO.Directory.Exists(old)) return;
                foreach (var f in new DirectoryInfo(old).GetFiles("*.mp3"))
                {
                    var target = Path.Combine(Directory, f.Name);
                    try
                    {
                        if (!File.Exists(target)) f.MoveTo(target);
                        else f.Delete();
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                System.IO.Directory.Delete(old, recursive: true);
                Log.Info("Кэш перенесён из %TEMP% в " + Directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Touch(string path)
        {
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool TryDelete(FileInfo f)
        {
            try { f.Delete(); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Откуда качать: ссылка и, для зашифрованных файлов, ключ AES (hex).
    /// Segments — HLS (SoundCloud): сегменты качаются по порядку и склеиваются в один файл, Url тогда — сам плейлист.
    /// </summary>
    public readonly record struct CacheSource(string Url, string? Key, IReadOnlyList<string>? Segments = null);

    /// <summary>Результат <see cref="AudioCache.Open"/>: готовый файл или идущая закачка.</summary>
    public readonly record struct CacheEntry(string? CompletedPath, CacheDownload? Download);

    /// <summary>Закачка одного трека. Её .part-файл можно читать, пока она идёт.</summary>
    public sealed class CacheDownload
    {
        private readonly TaskCompletionSource<string> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private long _written;
        private long _total = -1;
        private int _readers;
        private TaskCompletionSource _progress = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CacheDownload(string trackId, string finalPath)
        {
            TrackId = trackId;
            FinalPath = finalPath;
            PartPath = finalPath + ".part";
        }

        public string TrackId { get; }
        public string FinalPath { get; }
        public string PartPath { get; }

        /// <summary>Сколько байт уже записано в .part.</summary>
        public long Written => Interlocked.Read(ref _written);

        /// <summary>Полный размер файла, если сервер его сообщил; иначе -1.</summary>
        public long TotalBytes => Interlocked.Read(ref _total);

        public Task<string> Completion => _done.Task;
        public bool IsCompleted => _done.Task.IsCompleted;
        public bool IsFaulted => _done.Task.IsFaulted;

        /// <summary>Доля скачанного 0..1 (или -1, если размер неизвестен).</summary>
        public double Fraction
        {
            get
            {
                if (_done.Task.IsCompletedSuccessfully) return 1;
                var total = TotalBytes;
                return total > 0 ? Math.Clamp((double)Written / total, 0, 1) : -1;
            }
        }

        internal bool HasReaders => Volatile.Read(ref _readers) > 0;
        internal void AddReader() => Interlocked.Increment(ref _readers);
        internal void RemoveReader() => Interlocked.Decrement(ref _readers);

        internal void SetTotal(long total) => Interlocked.Exchange(ref _total, total);

        internal void ResetWritten()
        {
            Interlocked.Exchange(ref _written, 0);
            Signal();
        }

        internal void AddWritten(int count)
        {
            Interlocked.Add(ref _written, count);
            Signal();
        }

        internal void Complete()
        {
            _done.TrySetResult(FinalPath);
            Signal();
        }

        internal void Fail(Exception ex)
        {
            _done.TrySetException(ex);
            Signal();
        }

        /// <summary>Ждёт, пока в файле наберётся хотя бы bytes байт или закачка закончится.</summary>
        public async Task WaitForAsync(long bytes, CancellationToken ct)
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (Written >= bytes || _done.Task.IsCompleted) return;
                    wait = _progress.Task;
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>Синхронная версия для потока декодера.</summary>
        public bool WaitFor(long bytes, int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (Written >= bytes || _done.Task.IsCompleted) return true;
                    wait = _progress.Task;
                }
                var left = deadline - Environment.TickCount64;
                if (left <= 0) return false;
                wait.Wait((int)Math.Min(left, int.MaxValue));
            }
        }

        private void Signal()
        {
            TaskCompletionSource old;
            lock (_sync)
            {
                old = _progress;
                _progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            old.TrySetResult();
        }
    }
}
