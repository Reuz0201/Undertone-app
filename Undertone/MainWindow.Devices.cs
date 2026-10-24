using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Undertone.Audio;

namespace Undertone
{
    /// <summary>Устройство вывода рядом с громкостью: видно, куда идёт звук, и можно переключить в один клик.</summary>
    public partial class MainWindow
    {
        private static readonly string[] HeadphoneWords =
            { "наушник", "headphone", "headset", "гарнитур", "airpods", "buds", "earphone", "wh-", "wf-" };

        /// <summary>Подпись на кнопке: имя устройства, значок — наушники или колонка.</summary>
        private void UpdateDeviceButton()
        {
            var name = _player.ActiveDeviceName;
            // «Динамики (Realtek High Definition Audio)» → «Динамики»: в скобках обычно драйвер, он только мешает
            var shortName = name;
            var paren = shortName.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 3) shortName = shortName[..paren];
            DeviceBtn.Tag = shortName;
            DeviceBtn.Content = HeadphoneWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? "headphones" : "speakers";
            DeviceBtn.ToolTip = _settings.OutputDeviceId == null
                ? $"Звук: {name} (как в системе). Нажми, чтобы выбрать"
                : $"Звук: {name}. Нажми, чтобы выбрать";
        }

        private void Device_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var current = _settings.OutputDeviceId;
            var devices = _player.GetDevices();
            var known = current == null || devices.Any(d => d.Id == current);

            menu.Items.Add(DeviceItem("Как в системе", null, current == null || !known));
            menu.Items.Add(new Separator());
            foreach (var d in devices) menu.Items.Add(DeviceItem(d.Name, d.Id, d.Id == current));
            if (devices.Count == 0)
                menu.Items.Add(new MenuItem { Header = "Устройства не найдены", IsEnabled = false });
            OpenMenu(menu, DeviceBtn);
        }

        private MenuItem DeviceItem(string name, string? id, bool selected)
        {
            var item = Item(name, selected ? "" : "", () => SelectOutputDevice(id));
            if (selected) item.FontWeight = FontWeights.SemiBold;
            return item;
        }

        /// <summary>Общий путь для меню у громкости и страницы настроек.</summary>
        private void SelectOutputDevice(string? id)
        {
            _settings.OutputDeviceId = id;
            PersistSettings();
            _player.DeviceId = id; // если играет — переключится на ходу, с той же позиции
            UpdateDeviceButton();
            if (PageSettings.Visibility == Visibility.Visible)
            {
                _fillingSettings = true;
                try { FillOutputDevices(); } finally { _fillingSettings = false; }
            }
            ShowToast("Звук: " + _player.ActiveDeviceName);
        }
    }
}
