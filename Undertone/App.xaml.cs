using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Undertone.Core;

namespace Undertone
{
    public partial class App : Application
    {
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
                // ждём, пока старая версия закроется: она держит медиаклавиши и файлы *.old
                try { Process.GetProcessById(oldPid).WaitForExit(10_000); }
                catch (ArgumentException) { /* уже вышла */ }
                catch (InvalidOperationException) { }
            }

            if (!EnvFile.IsRunningFromSource)
                Updater.CleanupOld(AppContext.BaseDirectory);

            base.OnStartup(e);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.Message, "Undertone — ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
