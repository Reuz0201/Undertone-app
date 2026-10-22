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

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

            for (int i = 0; i < e.Args.Length - 1; i++)
                if (e.Args[i] == "--updated" && int.TryParse(e.Args[i + 1], out var pid))
                    UpdatedFrom = pid;

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
            window.Show();

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
            MessageBox.Show(e.Exception.Message, "Undertone — ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
