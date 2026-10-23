using System;
using Microsoft.Win32;

namespace Undertone.Core
{
    /// <summary>Автозапуск через HKCU\...\Run. С ключом --tray приложение стартует свёрнутым в трей.</summary>
    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Undertone";
        public const string TrayArg = "--tray";

        private static string? ExePath => Environment.ProcessPath;

        private static string Command => $"\"{ExePath}\" {TrayArg}";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(ValueName) is string s && s.Length > 0;
                }
                catch (Exception ex)
                {
                    Log.Warn("Не удалось прочитать автозапуск", ex);
                    return false;
                }
            }
        }

        public static bool Set(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (enabled)
                {
                    if (ExePath == null) return false;
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось изменить автозапуск", ex);
                return false;
            }
        }

        /// <summary>
        /// После обновления или переноса папки путь в реестре мог устареть — переписываем на текущий exe.
        /// </summary>
        public static void RefreshPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key?.GetValue(ValueName) is not string current || current.Length == 0 || ExePath == null) return;
                if (!string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось обновить путь автозапуска", ex);
            }
        }
    }
}
