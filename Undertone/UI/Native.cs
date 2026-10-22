using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Undertone.UI
{
    internal static class Native
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>Тёмный заголовок окна (Win10 1809+) и цвет шапки под фон (Win11).</summary>
        public static void ApplyDarkTitleBar(Window window, Color caption)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
                int color = caption.R | (caption.G << 8) | (caption.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref color, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }

    /// <summary>Глобальные медиаклавиши (Play/Pause, Next, Prev) — работают, даже когда окно свернуто.</summary>
    internal sealed class MediaKeyHook : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const uint ModNoRepeat = 0x4000;

        private static readonly (int Id, uint Vk, Key Key)[] Keys =
        {
            (0xB301, 0xB3, Key.MediaPlayPause),
            (0xB001, 0xB0, Key.MediaNextTrack),
            (0xB101, 0xB1, Key.MediaPreviousTrack),
        };

        private readonly HwndSource? _source;
        private readonly Action<Key> _onKey;
        private readonly List<int> _registered = new();

        public MediaKeyHook(Window window, Action<Key> onKey)
        {
            _onKey = onKey;
            var hwnd = new WindowInteropHelper(window).Handle;
            _source = HwndSource.FromHwnd(hwnd);
            if (_source == null) return;
            _source.AddHook(WndProc);
            foreach (var (id, vk, _) in Keys)
                if (Native.RegisterHotKey(hwnd, id, ModNoRepeat, vk))
                    _registered.Add(id);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmHotkey) return IntPtr.Zero;
            var id = wParam.ToInt32();
            foreach (var (kid, _, key) in Keys)
            {
                if (kid != id) continue;
                _onKey(key);
                handled = true;
                break;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_source == null) return;
            foreach (var id in _registered) Native.UnregisterHotKey(_source.Handle, id);
            _registered.Clear();
            _source.RemoveHook(WndProc);
        }
    }
}
