using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Undertone.Audio;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Страница «Настройки»: кэш, поведение окна, автозапуск, журнал.</summary>
    public partial class MainWindow
    {
        private bool _fillingSettings;

        /// <summary>Заполняет страницу текущими значениями. Вызывается при каждом открытии.</summary>
        private void ShowSettings()
        {
            _fillingSettings = true;
            try
            {
                SelectPill(CacheLimitPanel, _settings.CacheLimitMb);
                SelectPill(PrefetchPanel, _settings.PrefetchCount);
                CloseToTrayToggle.IsChecked = _settings.CloseToTray;
                StreamToggle.IsChecked = _settings.StreamPlayback;
                ResumeToggle.IsChecked = _settings.ResumeSession;
                FillOutputDevices();
                SelectPill(QualityPanel, (int)_settings.Quality);
                QualityHint.Text = _account is { HasPlus: false }
                    ? "«Без потерь» доступно только с Яндекс Плюсом — сейчас будет играть MP3."
                    : "«Без потерь» — FLAC для подписчиков Плюса; трек скачивается целиком перед стартом.";
                SelectPill(CrossfadePanel, _settings.CrossfadeSeconds);
                NormalizeToggle.IsChecked = _settings.Normalize;
                EqToggle.IsChecked = _settings.EqEnabled;
                BuildEqualizer();
                SelectTagPill(WaveDiversityPanel, _settings.WaveDiversity);
                SelectTagPill(WaveMoodPanel, _settings.WaveMood);
                SelectTagPill(WaveLanguagePanel, _settings.WaveLanguage);
                WaveRestartBtn.IsEnabled = _queue.Source == QueueSource.Wave && IsMyWave;
                ReportPlaysToggle.IsChecked = _settings.ReportPlays;
                HotkeysToggle.IsChecked = _settings.GlobalHotkeys;
                BuildHotkeyRows();
                DiscordToggle.IsChecked = _settings.DiscordEnabled;
                FillFriendsSettings();
                DiscordAppIdBox.Text = _settings.DiscordAppId;
                AutostartToggle.IsChecked = Autostart.IsEnabled;
                AutostartHint.Text = EnvFile.IsRunningFromSource
                    ? "Сейчас запущена сборка из исходников: в автозапуск попадёт именно она."
                    : "Undertone тихо стартует в трее и ждёт, когда вы включите музыку.";
                CachePathText.Text = _cache.Directory;
                DataPathText.Text = EnvFile.DataDirectory;
                var v = Updater.AppVersion;
                SettingsVersionText.Text = $"Undertone {v.Major}.{v.Minor}.{v.Build} · .NET {Environment.Version.Major}";
            }
            finally
            {
                _fillingSettings = false;
            }
            _ = RefreshCacheUsageAsync();
        }

        private static void SelectPill(Panel panel, int value)
        {
            var pills = panel.Children.OfType<RadioButton>().ToList();
            // точного совпадения нет (руками поправили settings.json) — ближайший вариант
            var best = pills.OrderBy(p => Math.Abs(TagValue(p) - value)).FirstOrDefault();
            if (best != null) best.IsChecked = true;
        }

        private static void SelectTagPill(Panel panel, string value)
        {
            foreach (var pill in panel.Children.OfType<RadioButton>())
                if ((pill.Tag as string ?? "") == value) { pill.IsChecked = true; return; }
            if (panel.Children.OfType<RadioButton>().FirstOrDefault() is { } first) first.IsChecked = true;
        }

        private static int TagValue(FrameworkElement e) =>
            int.TryParse(e.Tag?.ToString(), out var n) ? n : 0;

        private async Task RefreshCacheUsageAsync()
        {
            var (bytes, files) = await Task.Run(_cache.GetUsage);
            var limit = _cache.MaxBytes;
            CacheUsageText.Text = files == 0
                ? $"Кэш пуст · лимит {FormatSize(limit)}"
                : $"Занято {FormatSize(bytes)} из {FormatSize(limit)} · {files} {Format.Plural(files, "файл", "файла", "файлов")}";
            var ratio = limit > 0 ? Math.Clamp((double)bytes / limit, 0, 1) : 0;
            CacheBarUsed.Width = new GridLength(ratio, GridUnitType.Star);
            CacheBarFree.Width = new GridLength(1 - ratio, GridUnitType.Star);
            CacheClearBtn.IsEnabled = files > 0;
        }

        internal static string FormatSize(long bytes)
        {
            const double Mb = 1024 * 1024, Gb = Mb * 1024;
            if (bytes >= Gb) return $"{bytes / Gb:0.#} ГБ";
            if (bytes >= Mb) return $"{bytes / Mb:0} МБ";
            return bytes > 0 ? "меньше 1 МБ" : "0 МБ";
        }

        // ────────── кэш ──────────

        private async void CacheClear_Click(object sender, RoutedEventArgs e)
        {
            var (bytes, _) = await Task.Run(_cache.GetUsage);
            var ok = await ShowDialogAsync("Очистить кэш?",
                $"С диска удалится {FormatSize(bytes)} музыки. Треки скачаются заново, когда вы их включите. " +
                "Лайки, плейлисты и настройки не пострадают.", null, "Очистить");
            if (ok == null) return;

            CacheClearBtn.IsEnabled = false;
            var freed = await Task.Run(_cache.Clear);
            ShowToast(freed > 0 ? $"Кэш очищен: освобождено {FormatSize(freed)}" : "Кэш уже пуст");
            await RefreshCacheUsageAsync();
        }

        private void CacheOpen_Click(object sender, RoutedEventArgs e) => OpenFolder(_cache.Directory);

        private async void CacheLimit_Checked(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings || sender is not RadioButton pill) return;
            _settings.CacheLimitMb = Math.Max(AppSettings.MinCacheMb, TagValue(pill));
            _cache.MaxBytes = _settings.CacheLimitBytes;
            PersistSettings();
            // уменьшили лимит — сразу удаляем лишнее
            await Task.Run(_cache.Trim);
            await RefreshCacheUsageAsync();
        }

        private void Prefetch_Checked(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings || sender is not RadioButton pill) return;
            _settings.PrefetchCount = Math.Clamp(TagValue(pill), 0, 3);
            PersistSettings();
        }

        // ────────── воспроизведение ──────────

        private void FillOutputDevices()
        {
            OutputDevicePanel.Children.Clear();
            var devices = _player.GetDevices();
            var current = _settings.OutputDeviceId;
            var known = current == null || devices.Any(d => d.Id == current);

            OutputDevicePanel.Children.Add(DevicePill("Как в системе", null, current == null || !known));
            foreach (var d in devices)
                OutputDevicePanel.Children.Add(DevicePill(d.Name, d.Id, d.Id == current));
            if (!known)
                OutputDevicePanel.Children.Add(new TextBlock
                {
                    Text = "Выбранное раньше устройство сейчас отключено — играю на системном.",
                    Style = (Style)FindResource("SettingsHint"),
                    Margin = new Thickness(0, 0, 0, 8),
                });
        }

        private RadioButton DevicePill(string name, string? id, bool selected)
        {
            var pill = new RadioButton
            {
                Content = name,
                Tag = id,
                GroupName = "OutputDevice",
                Style = (Style)FindResource("ChoicePill"),
                IsChecked = selected,
                ToolTip = name,
                MaxWidth = 340,
            };
            pill.Checked += OutputDevice_Checked;
            return pill;
        }

        private void OutputDevice_Checked(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings || sender is not RadioButton { Tag: var tag }) return;
            var id = tag as string;
            _settings.OutputDeviceId = id;
            PersistSettings();
            _player.DeviceId = id; // если играет — переключится на ходу, с той же позиции
            if (!_hasMedia) ShowToast(id == null ? "Звук — на системном устройстве" : "Устройство вывода выбрано");
        }

        private void StreamToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.StreamPlayback = StreamToggle.IsChecked == true;
            PersistSettings();
        }

        private void ResumeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.ResumeSession = ResumeToggle.IsChecked == true;
            PersistSettings();
            if (!_settings.ResumeSession) SessionStore.Clear();
        }

        private void Quality_Checked(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings || sender is not RadioButton pill) return;
            _settings.Quality = (AudioQuality)Math.Clamp(TagValue(pill), 0, 2);
            _losslessFailures = 0;
            _losslessWarned = false;
            PersistSettings();
            InvalidatePreparedNext();
            ShowToast(_settings.Quality == AudioQuality.Lossless && _account is { HasPlus: false }
                ? "«Без потерь» нужен Яндекс Плюс — пока будет MP3"
                : "Качество применится со следующего трека");
        }

        private void Crossfade_Checked(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings || sender is not RadioButton pill) return;
            _settings.CrossfadeSeconds = Math.Clamp(TagValue(pill), -1, 12);
            PersistSettings();
            InvalidatePreparedNext();
        }

        private void Normalize_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.Normalize = NormalizeToggle.IsChecked == true;
            PersistSettings();
            if (_queue.Current is { } t) _player.TrackGain = GainFor(t);
        }

        // ────────── эквалайзер ──────────

        private Slider[] _eqSliders = Array.Empty<Slider>();
        private TextBlock[] _eqLabels = Array.Empty<TextBlock>();
        private bool _eqFromPreset;

        private static readonly string[] EqBandNames = { "31", "62", "125", "250", "500", "1 к", "2 к", "4 к", "8 к", "16 к" };

        private void BuildEqualizer()
        {
            if (_eqSliders.Length == 0)
            {
                var bands = Equalizer.Frequencies.Length;
                _eqSliders = new Slider[bands];
                _eqLabels = new TextBlock[bands];
                var columns = new List<FrameworkElement>();
                for (int i = 0; i < bands; i++)
                {
                    var band = i;
                    var label = new TextBlock { FontSize = 11, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Center };
                    var slider = new Slider { Style = (Style)FindResource("EqSlider"), Margin = new Thickness(0, 6, 0, 6) };
                    slider.ValueChanged += (_, args) => OnEqBandChanged(band, (float)args.NewValue);
                    slider.MouseDoubleClick += (_, _) => slider.Value = 0;
                    var name = new TextBlock
                    {
                        Text = EqBandNames[i], FontSize = 11.5, Foreground = DimBrush, HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    var column = new StackPanel { Width = 46, Children = { label, slider, name } };
                    columns.Add(column);
                    _eqSliders[i] = slider;
                    _eqLabels[i] = label;
                }
                EqBands.ItemsSource = columns;

                EqPresets.Children.Clear();
                foreach (var (presetName, gains) in Equalizer.Presets)
                {
                    var pill = new RadioButton
                    {
                        Content = presetName, GroupName = "EqPreset", Style = (Style)FindResource("ChoicePill"), Tag = gains,
                    };
                    pill.Checked += EqPreset_Checked;
                    EqPresets.Children.Add(pill);
                }
            }

            var current = _settings.EqGains.Length == Equalizer.Frequencies.Length ? _settings.EqGains : new float[Equalizer.Frequencies.Length];
            for (int i = 0; i < _eqSliders.Length; i++)
            {
                _eqSliders[i].Value = current[i];
                _eqSliders[i].IsEnabled = _settings.EqEnabled;
                _eqLabels[i].Text = FormatDb(current[i]);
            }
            foreach (var pill in EqPresets.Children.OfType<RadioButton>())
            {
                pill.IsEnabled = _settings.EqEnabled;
                pill.IsChecked = pill.Tag is float[] g && g.SequenceEqual(current);
            }
        }

        private static string FormatDb(float db) => db == 0 ? "0" : db > 0 ? $"+{db:0.#}" : $"{db:0.#}";

        private void OnEqBandChanged(int band, float db)
        {
            if (_settings.EqGains.Length != Equalizer.Frequencies.Length)
                _settings.EqGains = new float[Equalizer.Frequencies.Length];
            _settings.EqGains[band] = db;
            _player.Equalizer.SetGain(band, db);
            _eqLabels[band].Text = FormatDb(db);
            if (_fillingSettings || _eqFromPreset) return;
            // ручная правка — пресет больше не выбран
            foreach (var pill in EqPresets.Children.OfType<RadioButton>()) pill.IsChecked = false;
            PersistSettings();
        }

        private void EqPreset_Checked(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings || sender is not RadioButton { Tag: float[] gains }) return;
            _eqFromPreset = true;
            try
            {
                for (int i = 0; i < _eqSliders.Length && i < gains.Length; i++) _eqSliders[i].Value = gains[i];
            }
            finally
            {
                _eqFromPreset = false;
            }
            PersistSettings();
        }

        private void Eq_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.EqEnabled = EqToggle.IsChecked == true;
            _player.Equalizer.Enabled = _settings.EqEnabled;
            foreach (var slider in _eqSliders) slider.IsEnabled = _settings.EqEnabled;
            foreach (var pill in EqPresets.Children.OfType<RadioButton>()) pill.IsEnabled = _settings.EqEnabled;
            PersistSettings();
        }

        // ────────── Моя волна ──────────

        private void WaveSetting_Checked(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings || sender is not RadioButton pill) return;
            var value = pill.Tag as string ?? "";
            if (pill.GroupName == "WaveDiversity") _settings.WaveDiversity = value;
            else if (pill.GroupName == "WaveMood") _settings.WaveMood = value;
            else if (pill.GroupName == "WaveLanguage") _settings.WaveLanguage = value;
            PersistSettings();
        }

        private async void WaveRestart_Click(object sender, RoutedEventArgs e)
        {
            await StartWaveAsync();
            WaveRestartBtn.IsEnabled = _queue.Source == QueueSource.Wave && IsMyWave;
        }

        // ────────── интеграции ──────────

        private void ReportPlays_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.ReportPlays = ReportPlaysToggle.IsChecked == true;
            PersistSettings();
        }

        private GlobalHotkeys? _hotkeys;
        private HotkeyAction? _capturingHotkey;

        private void ApplyHotkeys()
        {
            _hotkeys?.Apply(_settings.GlobalHotkeys, _settings.Hotkeys);
        }

        private void Hotkeys_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.GlobalHotkeys = HotkeysToggle.IsChecked == true;
            PersistSettings();
            ApplyHotkeys();
            BuildHotkeyRows();
        }

        private void BuildHotkeyRows()
        {
            HotkeyRows.Children.Clear();
            foreach (var action in Enum.GetValues<HotkeyAction>())
            {
                var hk = GlobalHotkeys.Resolve(action, _settings.Hotkeys);
                var capturing = _capturingHotkey == action;
                var failed = _hotkeys?.Failed.Contains(action) == true && _settings.GlobalHotkeys;

                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = GlobalHotkeys.Title(action) + (failed ? "  · занято другой программой" : ""),
                    Foreground = failed ? RedBrushRes : TextBrushRes,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var button = new Button
                {
                    Style = (Style)FindResource("PillSecondary"),
                    Content = capturing ? "Нажмите сочетание…" : hk?.ToString() ?? "Отключено",
                    Padding = new Thickness(14, 5, 14, 5),
                    FontSize = 12.5,
                    MinWidth = 150,
                    IsEnabled = _settings.GlobalHotkeys,
                };
                var a = action;
                button.Click += (_, _) =>
                {
                    _capturingHotkey = _capturingHotkey == a ? null : a;
                    BuildHotkeyRows();
                };
                Grid.SetColumn(button, 1);
                row.Children.Add(button);
                HotkeyRows.Children.Add(row);
            }

            var reset = new Button
            {
                Style = (Style)FindResource("PillSecondary"),
                Content = "Сбросить сочетания",
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
                IsEnabled = _settings.GlobalHotkeys && _settings.Hotkeys.Count > 0,
            };
            reset.Click += (_, _) =>
            {
                _settings.Hotkeys.Clear();
                _capturingHotkey = null;
                PersistSettings();
                ApplyHotkeys();
                BuildHotkeyRows();
            };
            HotkeyRows.Children.Add(reset);
        }

        /// <summary>Режим записи сочетания: перехватывает клавиши из Window_PreviewKeyDown. true — клавиша съедена.</summary>
        private bool CaptureHotkey(KeyEventArgs e)
        {
            if (_capturingHotkey is not { } action) return false;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Escape:
                    _capturingHotkey = null;
                    BuildHotkeyRows();
                    return true;
                case Key.Back:
                case Key.Delete:
                    _settings.Hotkeys[action.ToString()] = "";
                    break;
                case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftAlt: case Key.RightAlt:
                case Key.LeftShift: case Key.RightShift: case Key.LWin: case Key.RWin:
                    return true; // ждём основную клавишу
                default:
                    var mods = Keyboard.Modifiers;
                    if (mods == ModifierKeys.None || mods == ModifierKeys.Shift)
                    {
                        ShowToast("Нужен Ctrl, Alt или Win — иначе сочетание будет мешать печатать");
                        return true;
                    }
                    _settings.Hotkeys[action.ToString()] = new Hotkey(mods, key).ToString();
                    break;
            }
            _capturingHotkey = null;
            PersistSettings();
            ApplyHotkeys();
            BuildHotkeyRows();
            return true;
        }

        private void OnHotkey(HotkeyAction action)
        {
            switch (action)
            {
                case HotkeyAction.PlayPause: TogglePlay(); break;
                case HotkeyAction.Next: _ = NextAsync(auto: false); break;
                case HotkeyAction.Previous: _ = PrevAsync(); break;
                case HotkeyAction.Like:
                    if (_queue.Current is { } t) _ = ToggleLikeAsync(t);
                    break;
                case HotkeyAction.VolumeUp:
                    VolumeSlider.Value = Math.Min(1, VolumeSlider.Value + 0.05);
                    ShowToast($"Громкость {VolumeSlider.Value:P0}", 1.2);
                    break;
                case HotkeyAction.VolumeDown:
                    VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 0.05);
                    ShowToast($"Громкость {VolumeSlider.Value:P0}", 1.2);
                    break;
                case HotkeyAction.ShowWindow:
                    if (IsVisible && IsActive) HideToTray();
                    else ShowFromTray();
                    break;
                case HotkeyAction.MiniPlayer: ToggleMiniPlayer(); break;
            }
        }

        private void Discord_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.DiscordEnabled = DiscordToggle.IsChecked == true;
            SaveDiscordAppId();
            PersistSettings();
            ApplyDiscord(announce: true);
        }

        private void DiscordAppId_LostFocus(object sender, RoutedEventArgs e)
        {
            if (SaveDiscordAppId()) ApplyDiscord(announce: true);
        }

        private void DiscordAppId_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (SaveDiscordAppId()) ApplyDiscord(announce: true);
        }

        /// <summary>true — ID изменился.</summary>
        private bool SaveDiscordAppId()
        {
            var id = new string(DiscordAppIdBox.Text.Where(char.IsDigit).ToArray());
            if (id == _settings.DiscordAppId) return false;
            _settings.DiscordAppId = id;
            PersistSettings();
            return true;
        }

        private DiscordSink? _discord;

        private void ApplyDiscord(bool announce = false)
        {
            if (_discord != null)
            {
                _sinks.Remove(_discord);
                _discord.Dispose();
                _discord = null;
            }
            if (!_settings.DiscordEnabled) return;
            if (_settings.DiscordAppId.Length < 15)
            {
                if (announce) ShowToast("Вставьте Application ID из Discord Developer Portal");
                return;
            }
            _discord = new DiscordSink(_settings.DiscordAppId);
            _sinks.Add(_discord);
            PublishNowPlaying();
            if (announce) ShowToast("Статус в Discord включён — нужен запущенный Discord");
        }

        // ────────── приложение ──────────

        private void CloseToTray_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
            PersistSettings();
        }

        private void Autostart_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            var want = AutostartToggle.IsChecked == true;
            if (!Autostart.Set(want))
            {
                ShowToast("Не получилось изменить автозапуск — подробности в журнале");
                _fillingSettings = true;
                AutostartToggle.IsChecked = Autostart.IsEnabled;
                _fillingSettings = false;
                return;
            }
            ShowToast(want ? "Undertone будет запускаться вместе с Windows" : "Автозапуск выключен");
        }

        // ────────── диагностика ──────────

        private void LogsOpen_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(Log.Directory);
            OpenFolder(Log.Directory);
        }

        private void DataOpen_Click(object sender, RoutedEventArgs e) => OpenFolder(EnvFile.DataDirectory);

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось открыть папку " + path, ex);
                ShowToast("Не удалось открыть папку: " + Short(ex.Message));
            }
        }
    }
}
