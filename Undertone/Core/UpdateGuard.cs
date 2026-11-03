using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Undertone.Core
{
    /// <summary>
    /// Откат сломанного обновления.
    ///
    /// Сразу после установки новая версия «на проверке»: старые файлы (*.old) лежат рядом и не удаляются.
    /// Проверка пройдена, когда версия проработала <see cref="HealthyAfter"/> (или нормально закрылась).
    /// Если она падает при запуске — или трижды подряд не доживает до этого — файлы *.old возвращаются на место,
    /// запускается прошлая версия, а сломанная попадает в список, который обновлятор больше не предлагает.
    /// </summary>
    public static class UpdateGuard
    {
        public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(20);
        /// <summary>Столько запусков подряд без «проверка пройдена» — и откат.</summary>
        public const int MaxLaunches = 3;
        public const string RolledBackArg = "--rolled-back";
        public const string BadSuffix = ".bad";

        private sealed record State(string To, string From, int Launches);

        private static string StatePath => Path.Combine(EnvFile.DataDirectory, "update-pending.json");
        private static string BadPath => Path.Combine(EnvFile.DataDirectory, "update-bad.txt");

        /// <summary>Эта версия ещё на проверке (после обновления не проработала положенное).</summary>
        public static bool Verifying { get; private set; }

        /// <summary>Вызывает обновлятор сразу после того, как разложил файлы новой версии.</summary>
        public static void MarkInstalled(Version from, Version to)
        {
            try
            {
                Directory.CreateDirectory(EnvFile.DataDirectory);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(new State(to.ToString(), from.ToString(), 0)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Не удалось записать состояние обновления", ex);
            }
        }

        /// <summary>
        /// При старте, до окна. true — версия сломана и нужно откатиться (<see cref="RollBack"/>).
        /// Иначе, если обновления не было, можно чистить *.old как раньше.
        /// </summary>
        public static bool OnStartup(Version current, string appDirectory)
        {
            var state = Read();
            if (state == null || !Updater.TryParseVersion(state.To, out var to) || to != current)
            {
                // проверять нечего (или запущена другая версия — например, уже откатились)
                Delete(StatePath);
                Updater.CleanupOld(appDirectory);
                return false;
            }

            var launches = state.Launches + 1;
            if (IsBad(current) || launches > MaxLaunches)
            {
                Log.Warn($"Версия {Updater.Display(current)} не прошла проверку ({launches - 1} запуска без успеха) — откат");
                return HasBackup(appDirectory);
            }
            Write(state with { Launches = launches });
            Verifying = true;
            return false;
        }

        /// <summary>Версия работает: убираем *.old и забываем про проверку.</summary>
        public static void MarkHealthy(string appDirectory)
        {
            if (!Verifying) return;
            Verifying = false;
            Delete(StatePath);
            Updater.CleanupOld(appDirectory);
            Log.Info($"Обновление до {Updater.Display(Updater.AppVersion)} прошло проверку");
        }

        private static bool HasBackup(string appDirectory)
        {
            try { return Directory.EnumerateFiles(appDirectory, "*" + Updater.OldSuffix, SearchOption.AllDirectories).Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        /// <summary>
        /// Возвращает *.old на место, запоминает версию как сломанную и запускает прошлую.
        /// true — прошлая версия запущена, этому процессу пора выйти.
        /// </summary>
        public static bool RollBack(Version bad, string appDirectory)
        {
            AddBad(bad);
            Delete(StatePath);
            Verifying = false;
            if (!RestoreOld(appDirectory)) return false;
            try
            {
                var exe = Path.Combine(appDirectory, "Undertone.exe");
                var start = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = appDirectory,
                    // --updated: прошлая версия подождёт, пока этот процесс выйдет и отпустит мьютекс
                    ArgumentList = { "--updated", Environment.ProcessId.ToString(), RolledBackArg, bad.ToString() },
                };
                Process.Start(start);
                Log.Info($"Откат с {Updater.Display(bad)}: прошлая версия запущена");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Откат: не удалось запустить прошлую версию", ex);
                return false;
            }
        }

        /// <summary>
        /// X.old → X; файл новой версии (запущенный exe тоже можно переименовать) уходит в X.bad —
        /// его удалит следующий запуск. Возвращает, было ли что возвращать.
        /// </summary>
        internal static bool RestoreOld(string appDirectory)
        {
            var any = false;
            foreach (var old in Directory.GetFiles(appDirectory, "*" + Updater.OldSuffix, SearchOption.AllDirectories))
            {
                var target = old[..^Updater.OldSuffix.Length];
                try
                {
                    if (File.Exists(target))
                    {
                        var bad = target + BadSuffix;
                        if (File.Exists(bad)) File.Delete(bad);
                        File.Move(target, bad);
                    }
                    File.Move(old, target);
                    any = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Откат: не удалось вернуть {Path.GetFileName(target)}", ex);
                }
            }
            return any;
        }

        // ────────── сломанные версии ──────────

        public static bool IsBad(Version v) => BadVersions().Contains(v);

        public static HashSet<Version> BadVersions()
        {
            var set = new HashSet<Version>();
            try
            {
                if (!File.Exists(BadPath)) return set;
                foreach (var line in File.ReadAllLines(BadPath))
                    if (Updater.TryParseVersion(line, out var v)) set.Add(v);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return set;
        }

        private static void AddBad(Version v)
        {
            try
            {
                Directory.CreateDirectory(EnvFile.DataDirectory);
                // храним последние 20 — список не растёт бесконечно
                var lines = BadVersions().Select(x => x.ToString()).Append(v.ToString()).Distinct().TakeLast(20);
                File.WriteAllLines(BadPath, lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Не удалось запомнить сломанную версию", ex);
            }
        }

        // ────────── файл состояния ──────────

        private static State? Read()
        {
            try
            {
                return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        private static void Write(State state)
        {
            try { File.WriteAllText(StatePath, JsonSerializer.Serialize(state)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
