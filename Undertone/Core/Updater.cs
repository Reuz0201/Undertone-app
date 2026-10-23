using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    public sealed record UpdateInfo(
        Version Version, string Tag, string ZipUrl, string? Sha256Url, string Notes, string PageUrl, long Size,
        string? SignatureUrl = null);

    public sealed class UpdateException : Exception
    {
        public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Автообновление из GitHub Releases.
    /// Релиз содержит Undertone-win-x64.zip (+ .sha256, + .sig). Файлы приложения подменяются через
    /// переименование: запущенный exe нельзя перезаписать, но можно переименовать в *.old.
    ///
    /// SHA-256 защищает только от битого файла. От подмены защищает подпись .sig (ECDSA P-256):
    /// приватный ключ лежит в секретах GitHub, публичный вшивается в exe при сборке.
    /// Если в сборке есть публичный ключ, неподписанное или чужое обновление не установится.
    /// </summary>
    public sealed class Updater
    {
        public const string AssetName = "Undertone-win-x64.zip";
        public const string OldSuffix = ".old";

        private static readonly HttpClient Http = CreateHttp();

        public string Repo { get; }
        public Version CurrentVersion { get; }
        public string AppDirectory { get; }

        public Updater(string repo, Version currentVersion, string appDirectory)
        {
            Repo = repo.Trim().Trim('/');
            CurrentVersion = Normalize(currentVersion);
            AppDirectory = appDirectory;
        }

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Undertone-Updater", "1.0"));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return http;
        }

        // ────────── что за версия и откуда обновляться ──────────

        /// <summary>Версия текущей сборки (из &lt;Version&gt; / -p:Version= в CI).</summary>
        public static Version AppVersion
        {
            get
            {
                var asm = Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (info != null && TryParseVersion(info, out var v)) return v;
                return Normalize(asm.GetName().Version ?? new Version(0, 0, 0));
            }
        }

        /// <summary>owner/repo, который CI вшивает в сборку (AssemblyMetadata "UpdateRepo").</summary>
        public static string? EmbeddedRepo => Metadata("UpdateRepo");

        /// <summary>Публичный ключ подписи обновлений (base64 SubjectPublicKeyInfo), вшитый CI. null — подпись не проверяется.</summary>
        public static string? EmbeddedPublicKey => Metadata("UpdatePublicKey");

        private static string? Metadata(string key)
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly;
            var value = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>Публичный ключ для проверки. Для тестов можно подменить.</summary>
        internal string? PublicKey { get; init; } = EmbeddedPublicKey;

        // ────────── проверка ──────────

        public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
        {
            string json;
            try
            {
                using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // релизов ещё нет
                if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
                    throw new UpdateException("GitHub временно ограничил запросы, проверю позже");
                resp.EnsureSuccessStatusCode();
                json = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException("Нет связи с GitHub", ex);
            }

            var info = ParseRelease(json);
            return info != null && info.Version > CurrentVersion ? info : null;
        }

        internal static UpdateInfo? ParseRelease(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;

            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var version)) return null;

            string? zip = null, sha = null, sig = null;
            long size = 0;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        zip = url;
                        if (a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number) size = s.GetInt64();
                    }
                    else if (name.Equals(AssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        sha = url;
                    }
                    else if (name.Equals(AssetName + ".sig", StringComparison.OrdinalIgnoreCase))
                    {
                        sig = url;
                    }
                }
            }
            if (zip == null) return null;

            var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            return new UpdateInfo(version, tag, zip, sha, notes.Trim(), page, size, sig);
        }

        internal static bool TryParseVersion(string text, out Version version)
        {
            var s = text.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s.Substring(0, cut);
            if (Version.TryParse(s, out var v)) { version = Normalize(v); return true; }
            if (int.TryParse(s, out var major)) { version = new Version(major, 0, 0); return true; }
            version = new Version(0, 0, 0);
            return false;
        }

        /// <summary>1.2 == 1.2.0 == 1.2.0.0 — иначе Version сравнивает -1 и 0 по-разному.</summary>
        private static Version Normalize(Version v) =>
            new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

        // ────────── скачивание ──────────

        /// <summary>Скачивает, проверяет SHA-256 и распаковывает во временную папку. Возвращает её путь.</summary>
        public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var work = Path.Combine(Path.GetTempPath(), "Undertone-update", update.Version.ToString());
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, AssetName);

            try
            {
                using (var resp = await Http.GetAsync(update.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? update.Size;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(zipPath);
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        if (total > 0) progress?.Report((double)done / total);
                    }
                }

                if (update.Sha256Url != null)
                {
                    var expected = ParseSha256(await Http.GetStringAsync(update.Sha256Url, ct));
                    var actual = await ComputeSha256Async(zipPath, ct);
                    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        throw new UpdateException("Контрольная сумма не совпала — файл повреждён или подменён");
                }

                if (PublicKey != null)
                {
                    if (update.SignatureUrl == null)
                        throw new UpdateException("Обновление не подписано — установка отменена");
                    var signature = await Http.GetStringAsync(update.SignatureUrl, ct);
                    if (!VerifySignature(zipPath, signature, PublicKey))
                        throw new UpdateException("Подпись обновления не совпала — файл подменён, установка отменена");
                }

                var staging = Path.Combine(work, "files");
                ZipFile.ExtractToDirectory(zipPath, staging);
                var exe = Directory.GetFiles(staging, "Undertone.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe == null) throw new UpdateException("В архиве нет Undertone.exe");
                // если в архиве есть вложенная папка — берём её
                return Path.GetDirectoryName(exe)!;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                throw new UpdateException("Не удалось скачать обновление: " + ex.Message, ex);
            }
            finally
            {
                // архив больше не нужен (распакованное лежит в files); при ошибке — убираем всё
                try { File.Delete(zipPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Проверка подписи ECDSA P-256 / SHA-256 (формат IEEE P1363, base64).</summary>
        internal static bool VerifySignature(string filePath, string signatureBase64, string publicKeyBase64)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
                var signature = Convert.FromBase64String(signatureBase64.Trim());
                using var fs = File.OpenRead(filePath);
                return ecdsa.VerifyData(fs, signature, HashAlgorithmName.SHA256);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                Log.Warn("Подпись обновления не читается", ex);
                return false;
            }
        }

        internal static string ParseSha256(string text)
        {
            // форматы: "abc123", "abc123  file.zip", "SHA256: abc123"
            foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n', ':', '*' }, StringSplitOptions.RemoveEmptyEntries))
                if (token.Length == 64 && token.All(Uri.IsHexDigit))
                    return token.ToLowerInvariant();
            throw new UpdateException("Неверный формат файла .sha256");
        }

        internal static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
        {
            await using var fs = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(fs, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        // ────────── установка ──────────

        /// <summary>
        /// Подменяет файлы приложения файлами из staging. Существующие переименовываются в *.old
        /// (их удалит следующий запуск). При любой ошибке всё откатывается.
        /// .env и прочие файлы, которых нет в архиве, не трогаются.
        /// </summary>
        public void Apply(string stagingDir)
        {
            var moved = new List<(string Target, string? Backup)>();
            try
            {
                foreach (var src in Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(stagingDir, src);
                    if (IsProtected(rel)) continue;

                    var target = Path.Combine(AppDirectory, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    string? backup = null;
                    if (File.Exists(target))
                    {
                        backup = target + OldSuffix;
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(target, backup);
                    }
                    moved.Add((target, backup));
                    File.Copy(src, target, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                Rollback(moved);
                if (ex is UnauthorizedAccessException)
                    throw new UpdateException($"Нет прав на запись в {AppDirectory}. Перенесите приложение в свою папку или запустите от администратора.", ex);
                throw new UpdateException("Не удалось установить обновление: " + ex.Message, ex);
            }
        }

        private static void Rollback(List<(string Target, string? Backup)> moved)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                var (target, backup) = moved[i];
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (backup != null && File.Exists(backup)) File.Move(backup, target);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Пользовательские файлы, которые обновление не должно перезаписывать.</summary>
        internal static bool IsProtected(string relativePath)
        {
            var name = Path.GetFileName(relativePath);
            return name.Equals(EnvFile.FileName, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Удаляет *.old после успешного обновления (вызывать, когда старый процесс уже вышел).</summary>
        public static void CleanupOld(string appDirectory)
        {
            try
            {
                foreach (var f in Directory.GetFiles(appDirectory, "*" + OldSuffix, SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                var temp = Path.Combine(Path.GetTempPath(), "Undertone-update");
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
