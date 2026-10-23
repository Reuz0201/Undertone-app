using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Undertone.Core;

namespace Undertone.UI
{
    public enum HotkeyAction { PlayPause, Next, Previous, Like, VolumeUp, VolumeDown, ShowWindow, MiniPlayer }

    /// <summary>Сочетание клавиш: модификаторы + клавиша. Текстовый вид — "Ctrl+Alt+L".</summary>
    public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
    {
        public override string ToString()
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join("+", parts);
        }

        private static string KeyName(Key key) => key switch
        {
            Key.Left => "←",
            Key.Right => "→",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Space => "Пробел",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString(),
        };

        public static bool TryParse(string? text, out Hotkey hotkey)
        {
            hotkey = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var mods = ModifierKeys.None;
            Key? key = null;
            foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                    case "alt": mods |= ModifierKeys.Alt; break;
                    case "shift": mods |= ModifierKeys.Shift; break;
                    case "win": mods |= ModifierKeys.Windows; break;
                    case "←": key = Key.Left; break;
                    case "→": key = Key.Right; break;
                    case "↑": key = Key.Up; break;
                    case "↓": key = Key.Down; break;
                    case "пробел": key = Key.Space; break;
                    default:
                        if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                        else if (Enum.TryParse<Key>(raw, true, out var k)) key = k;
                        else return false;
                        break;
                }
            }
            if (key == null || mods == ModifierKeys.None) return false;
            hotkey = new Hotkey(mods, key.Value);
            return true;
        }
    }

    /// <summary>
    /// Системные сочетания через RegisterHotKey: работают, даже когда окно свёрнуто или в трее.
    /// Если сочетание уже занято другой программой, оно просто не регистрируется (см. <see cref="Failed"/>).
    /// </summary>
    internal sealed class GlobalHotkeys : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const uint ModNoRepeat = 0x4000;
        private const int BaseId = 0x5100;

        public static readonly IReadOnlyDictionary<HotkeyAction, string> Defaults = new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.PlayPause] = "Ctrl+Alt+Space",
            [HotkeyAction.Next] = "Ctrl+Alt+Right",
            [HotkeyAction.Previous] = "Ctrl+Alt+Left",
            [HotkeyAction.Like] = "Ctrl+Alt+L",
            [HotkeyAction.VolumeUp] = "Ctrl+Alt+Up",
            [HotkeyAction.VolumeDown] = "Ctrl+Alt+Down",
            [HotkeyAction.ShowWindow] = "Ctrl+Alt+U",
            [HotkeyAction.MiniPlayer] = "Ctrl+Alt+M",
        };

        public static string Title(HotkeyAction action) => action switch
        {
            HotkeyAction.PlayPause => "Играть / пауза",
            HotkeyAction.Next => "Следующий трек",
            HotkeyAction.Previous => "Предыдущий трек",
            HotkeyAction.Like => "Мне нравится",
            HotkeyAction.VolumeUp => "Громче",
            HotkeyAction.VolumeDown => "Тише",
            HotkeyAction.ShowWindow => "Показать Undertone",
            HotkeyAction.MiniPlayer => "Мини-плеер",
            _ => action.ToString(),
        };

        private readonly HwndSource? _source;
        private readonly Action<HotkeyAction> _onAction;
        private readonly Dictionary<int, HotkeyAction> _registered = new();

        /// <summary>Действия, сочетание которых занято другой программой.</summary>
        public HashSet<HotkeyAction> Failed { get; } = new();

        public GlobalHotkeys(Window window, Action<HotkeyAction> onAction)
        {
            _onAction = onAction;
            var hwnd = new WindowInteropHelper(window).Handle;
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);
        }

        /// <summary>Сочетание для действия: из настроек или по умолчанию.</summary>
        public static Hotkey? Resolve(HotkeyAction action, IReadOnlyDictionary<string, string> overrides)
        {
            var text = overrides.TryGetValue(action.ToString(), out var custom) ? custom : Defaults[action];
            if (text == "") return null; // пользователь отключил это сочетание
            return Hotkey.TryParse(text, out var hk) ? hk : null;
        }

        public void Apply(bool enabled, IReadOnlyDictionary<string, string> overrides)
        {
            UnregisterAll();
            Failed.Clear();
            if (!enabled || _source == null) return;
            foreach (var action in Enum.GetValues<HotkeyAction>())
            {
                if (Resolve(action, overrides) is not { } hk) continue;
                var id = BaseId + (int)action;
                var vk = (uint)KeyInterop.VirtualKeyFromKey(hk.Key);
                if (Native.RegisterHotKey(_source.Handle, id, (uint)hk.Modifiers | ModNoRepeat, vk))
                    _registered[id] = action;
                else
                {
                    Failed.Add(action);
                    Log.Warn($"Сочетание {hk} для «{Title(action)}» занято другой программой");
                }
            }
        }

        private void UnregisterAll()
        {
            if (_source == null) return;
            foreach (var id in _registered.Keys.ToList()) Native.UnregisterHotKey(_source.Handle, id);
            _registered.Clear();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmHotkey) return IntPtr.Zero;
            if (_registered.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                _onAction(action);
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            UnregisterAll();
            _source?.RemoveHook(WndProc);
        }
    }
}
