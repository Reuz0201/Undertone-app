using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Undertone.Core;

namespace Undertone
{
    public partial class App : Application
    {
        private const string MutexName = @"Local\Undertone.SingleInstance";
        private const string ShowEventName = @"Local\Undertone.Show";

        private static Mutex? _instanceMutex;
        private static EventWaitHandle? _showEvent;

        /// <summary>PID старой версии, если нас запустил обновлятор (аргумент --updated &lt;pid&gt;).</summary>
        public static int? UpdatedFrom { get; private set; }

        /// <summary>Нас вернули откатом: эта версия не запустилась (аргумент --rolled-back &lt;версия&gt;).</summary>
        public static Version? RolledBackFrom { get; private set; }

        /// <summary>Запуск из автозагрузки (--tray): окно не показываем, сразу в трей.</summary>
        public static bool StartInTray { get; private set; }

        /// <summary>Перезапуск после обновления, когда играла музыка: продолжить с того же места сразу, без ▶.</summary>
        public const string ResumePlayArg = "--resume-play";
        public static bool ResumePlay { get; set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Warn("Необработанная ошибка в фоновой задаче", args.Exception);
                args.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                Log.Error("Фатальная ошибка", args.ExceptionObject as Exception);
                // новая версия падает насмерть, ещё не пройдя проверку, — возвращаем прошлую
                if (UpdateGuard.Verifying) UpdateGuard.RollBack(Updater.AppVersion, AppContext.BaseDirectory);
            };

            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--updated" && i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out var pid))
                    UpdatedFrom = pid;
                if (e.Args[i] == Autostart.TrayArg)
                    StartInTray = true;
                if (e.Args[i] == ResumePlayArg)
                    ResumePlay = true;
                if (e.Args[i] == UpdateGuard.RolledBackArg && i + 1 < e.Args.Length && Updater.TryParseVersion(e.Args[i + 1], out var rb))
                    RolledBackFrom = rb;
            }

            if (UpdatedFrom is { } oldPid)
            {
                // ждём, пока старая версия закроется: она держит мьютекс, медиаклавиши и файлы *.old
                try { Process.GetProcessById(oldPid).WaitForExit(30_000); }
                catch (ArgumentException) { /* уже вышла */ }
                catch (InvalidOperationException) { }
            }

            // один экземпляр: повторный запуск просто показывает уже открытое окно (оно может быть в трее)
            _instanceMutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    using var show = EventWaitHandle.OpenExisting(ShowEventName);
                    show.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { }
                Shutdown();
                return;
            }

            if (!EnvFile.IsRunningFromSource && UpdateGuard.OnStartup(Updater.AppVersion, AppContext.BaseDirectory))
            {
                // эта версия уже падала при запуске — сразу назад
                if (UpdateGuard.RollBack(Updater.AppVersion, AppContext.BaseDirectory)) { Shutdown(); return; }
            }

            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            if (StartInTray) window.StartHidden();
            else window.Show();
            _started = true;

            if (UpdateGuard.Verifying)
            {
                var healthy = new DispatcherTimer { Interval = UpdateGuard.HealthyAfter };
                healthy.Tick += (_, _) => { healthy.Stop(); UpdateGuard.MarkHealthy(AppContext.BaseDirectory); };
                healthy.Start();
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var listener = new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.InvokeAsync(() => (Current.MainWindow as Undertone.MainWindow)?.ShowFromTray());
            })
            { IsBackground = true, Name = "Undertone.ShowListener" };
            listener.Start();
        }

        /// <summary>Окно создано — дальше ошибки интерфейса не повод откатывать версию.</summary>
        private static bool _started;

        protected override void OnExit(ExitEventArgs e)
        {
            // закрыли сами, а окно успело открыться — версия рабочая
            if (_started) UpdateGuard.MarkHealthy(AppContext.BaseDirectory);
            try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("Необработанная ошибка в интерфейсе", e.Exception);
            // окно новой версии даже не создалось — откатываемся, а не оставляем пользователя без плеера
            if (!_started && UpdateGuard.Verifying &&
                UpdateGuard.RollBack(Updater.AppVersion, AppContext.BaseDirectory))
            {
                e.Handled = true;
                Current.Shutdown();
                return;
            }
            e.Handled = true;
            var answer = MessageBox.Show(e.Exception.Message + "\n\nСообщить об ошибке? Соберу журналы и открою страницу на GitHub.",
                "Undertone — ошибка", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
                BugReport.Send(Updater.EmbeddedRepo, error: e.Exception.GetType().Name + ": " + e.Exception.Message);
        }
    }
}
