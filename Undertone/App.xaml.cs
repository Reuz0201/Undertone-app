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

        /// <summary>Запуск из автозагрузки (--tray): окно не показываем, сразу в трей.</summary>
        public static bool StartInTray { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Warn("Необработанная ошибка в фоновой задаче", args.Exception);
                args.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Error("Фатальная ошибка", args.ExceptionObject as Exception);

            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--updated" && i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out var pid))
                    UpdatedFrom = pid;
                if (e.Args[i] == Autostart.TrayArg)
                    StartInTray = true;
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

            if (!EnvFile.IsRunningFromSource)
                Updater.CleanupOld(AppContext.BaseDirectory);

            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            if (StartInTray) window.StartHidden();
            else window.Show();

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var listener = new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.InvokeAsync(() => (Current.MainWindow as Undertone.MainWindow)?.ShowFromTray());
            })
            { IsBackground = true, Name = "Undertone.ShowListener" };
            listener.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("Необработанная ошибка в интерфейсе", e.Exception);
            MessageBox.Show(e.Exception.Message + "\n\nПодробности — в журнале: Настройки → Открыть папку логов.",
                "Undertone — ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
