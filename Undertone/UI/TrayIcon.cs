using System;
using System.Drawing;
using WinForms = System.Windows.Forms;

namespace Undertone.UI
{
    /// <summary>Значок в трее (WinForms NotifyIcon — в WPF своего нет) с тёмным меню.</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ContextMenuStrip _menu;
        private readonly WinForms.ToolStripMenuItem _track;
        private readonly WinForms.ToolStripMenuItem _play;

        public event Action? ShowRequested;
        public event Action? PlayPauseRequested;
        public event Action? NextRequested;
        public event Action? PrevRequested;
        public event Action? MiniRequested;
        public event Action? ExitRequested;

        public TrayIcon()
        {
            _menu = new WinForms.ContextMenuStrip
            {
                Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false },
                ShowImageMargin = false,
                BackColor = Color.FromArgb(24, 27, 36),
                ForeColor = Color.FromArgb(235, 238, 245),
                Font = new Font("Segoe UI", 9.5f),
                Padding = new WinForms.Padding(2, 4, 2, 4),
            };

            _track = new WinForms.ToolStripMenuItem("Ничего не играет") { Enabled = false };
            _play = Item("Играть", () => PlayPauseRequested?.Invoke());
            var open = Item("Открыть Undertone", () => ShowRequested?.Invoke());
            open.Font = new Font(_menu.Font, FontStyle.Bold);

            _menu.Items.AddRange(new WinForms.ToolStripItem[]
            {
                _track,
                new WinForms.ToolStripSeparator(),
                _play,
                Item("Следующий", () => NextRequested?.Invoke()),
                Item("Предыдущий", () => PrevRequested?.Invoke()),
                new WinForms.ToolStripSeparator(),
                open,
                Item("Мини-плеер", () => MiniRequested?.Invoke()),
                Item("Выход", () => ExitRequested?.Invoke()),
            });

            _icon = new WinForms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Undertone",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) ShowRequested?.Invoke();
            };
        }

        private static WinForms.ToolStripMenuItem Item(string text, Action onClick) =>
            new(text, null, (_, _) => onClick()) { Padding = new WinForms.Padding(4, 3, 4, 3) };

        private static Icon LoadIcon()
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/undertone.ico"));
                if (info != null)
                {
                    using var stream = info.Stream;
                    return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
                }
            }
            catch (Exception) { /* упадём на иконку exe */ }
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? SystemIcons.Application;
        }

        public void SetTrack(string? track, bool playing)
        {
            _track.Text = track ?? "Ничего не играет";
            _play.Text = playing ? "Пауза" : "Играть";
            var tip = track == null ? "Undertone" : $"Undertone — {track}";
            // у NotifyIcon лимит подсказки — 127 символов
            _icon.Text = tip.Length > 127 ? tip.Substring(0, 124) + "..." : tip;
        }

        public void ShowHint(string title, string text) =>
            _icon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.None);

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }

        private sealed class DarkMenuColors : WinForms.ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(24, 27, 36);
            private static readonly Color Hover = Color.FromArgb(38, 43, 58);
            private static readonly Color Border = Color.FromArgb(44, 49, 64);

            public override Color ToolStripDropDownBackground => Bg;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Bg;
        }
    }
}
