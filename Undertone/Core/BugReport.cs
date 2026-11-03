using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Undertone.Core
{
    /// <summary>
    /// «Сообщить об ошибке»: архив с журналами за последние дни и сведениями о системе — на рабочий стол,
    /// плюс страница новой задачи на GitHub с уже заполненными версией и системой. Архив туда перетаскивается
    /// руками: прикрепить файл ссылкой GitHub не даёт. Токенов в журналах нет (их туда не пишут).
    /// </summary>
    public static class BugReport
    {
        public const string DefaultRepo = "Reuz0201/Undertone-app";
        private const int LogDays = 3;

        /// <summary>Строчки «о системе» — и в архив, и в текст задачи.</summary>
        public static string SystemInfo(string? service = null)
        {
            var v = Updater.AppVersion;
            var sb = new StringBuilder();
            sb.AppendLine($"Undertone: {Updater.Display(v)}{(Updater.IsBetaVersion(v) ? " (бета)" : "")}");
            sb.AppendLine($"Windows: {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-бит)");
            sb.AppendLine($".NET: {Environment.Version}");
            if (!string.IsNullOrEmpty(service)) sb.AppendLine($"Сервис: {service}");
            if (UpdateGuard.BadVersions() is { Count: > 0 } bad)
                sb.AppendLine("Откаченные версии: " + string.Join(", ", bad.Select(Updater.Display)));
            return sb.ToString().TrimEnd();
        }

        /// <summary>Собирает архив (журналы за <see cref="LogDays"/> дня + system.txt). Возвращает путь.</summary>
        public static string CreateArchive(string? service = null, string? folder = null)
        {
            folder ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Path.GetTempPath();
            var path = Path.Combine(folder, $"Undertone-ошибка-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");
            if (File.Exists(path)) File.Delete(path);

            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var info = zip.CreateEntry("system.txt");
            using (var w = new StreamWriter(info.Open(), Encoding.UTF8))
                w.Write(SystemInfo(service));

            if (Directory.Exists(Log.Directory))
            {
                var logs = new DirectoryInfo(Log.Directory).GetFiles("undertone-*.log")
                    .OrderByDescending(f => f.Name).Take(LogDays);
                foreach (var f in logs)
                {
                    // журнал открыт на запись этим же процессом — читаем с FileShare.ReadWrite
                    using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dst = zip.CreateEntry(f.Name).Open();
                    src.CopyTo(dst);
                }
            }
            return path;
        }

        /// <summary>Ссылка на новую задачу с заполненным шаблоном.</summary>
        public static string IssueUrl(string? repo, string? service = null, string? error = null)
        {
            repo = string.IsNullOrWhiteSpace(repo) || !repo.Contains('/') ? DefaultRepo : repo.Trim().Trim('/');
            var body = new StringBuilder()
                .AppendLine("**Что случилось?**")
                .AppendLine()
                .AppendLine("<!-- опиши, что делал и что пошло не так -->")
                .AppendLine()
                .AppendLine("**Журналы:** перетащи сюда архив `Undertone-ошибка-….zip` с рабочего стола.")
                .AppendLine()
                .AppendLine("```")
                .AppendLine(SystemInfo(service));
            if (!string.IsNullOrWhiteSpace(error))
                body.AppendLine("Ошибка: " + Shorten(error, 300));
            body.AppendLine("```");

            var title = string.IsNullOrWhiteSpace(error) ? "Ошибка: " : "Ошибка: " + Shorten(error, 80);
            var labels = Updater.IsBetaVersion(Updater.AppVersion) ? "&labels=bug,beta" : "&labels=bug";
            return $"https://github.com/{repo}/issues/new?title={Uri.EscapeDataString(title)}" +
                   $"&body={Uri.EscapeDataString(body.ToString())}{labels}";
        }

        /// <summary>Всё сразу: архив, проводник с ним, страница задачи. Возвращает путь архива (или null).</summary>
        public static string? Send(string? repo, string? service = null, string? error = null)
        {
            string? archive = null;
            try
            {
                archive = CreateArchive(service);
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{archive}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось собрать архив для отчёта об ошибке", ex);
            }
            try
            {
                Process.Start(new ProcessStartInfo(IssueUrl(repo, service, error)) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось открыть страницу отчёта", ex);
            }
            return archive;
        }

        internal static string Shorten(string text, int max)
        {
            var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
        }
    }
}
