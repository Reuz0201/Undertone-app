using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>«Подборки»: умные плейлисты по правилам, считаются заново при каждом открытии.</summary>
    public partial class MainWindow
    {
        private PlayStats _stats = new();
        private SmartPlaylist? _collSmart;
        /// <summary>Что сейчас в редакторе: копия правил (null — редактор закрыт); _smartEditingNew — новая подборка.</summary>
        private SmartPlaylist? _smartEditing;
        private bool _smartEditingNew;

        private void LoadStats() => _stats = _account != null ? PlayStats.Load(_account.Uid) : new PlayStats();

        private List<SmartPlaylist> SmartRules
        {
            get
            {
                if (_settings.SmartPlaylists == null)
                {
                    _settings.SmartPlaylists = SmartPlaylist.Defaults();
                    PersistSettings();
                }
                return _settings.SmartPlaylists;
            }
        }

        private void RebuildSmart()
        {
            SmartList.ItemsSource = SmartRules.Select((r, i) => new SmartItem(r, i)).ToList();
            _ = RebuildSpotifyMixesAsync();
        }

        private List<YaTrack> EvaluateSmart(SmartPlaylist rule) =>
            SmartEngine.Evaluate(rule, _likedTracks, _local.Tracks(), _stats, DateTime.Now,
                // «случайно» меняется раз в день, чтобы подборка не скакала при каждом клике
                // (string.GetHashCode в .NET меняется от запуска к запуску — считаем свой)
                CompanyMix.TodaySeed(DateTime.Now) ^ rule.Id.Aggregate(17, (h, c) => h * 31 + c));

        private async void SmartCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: SmartItem item })
                await OpenCollectionAsync(() => LoadSmartViewAsync(item.Rule));
        }

        private void SmartCard_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: SmartItem item } el) return;
            e.Handled = true;
            OpenMenu(BuildSmartMenu(item.Rule), el, atMouse: true);
        }

        private ContextMenu BuildSmartMenu(SmartPlaylist rule)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Изменить правила", "", () => OpenSmartEditor(rule, isNew: false)));
            menu.Items.Add(Item("Удалить подборку", "", () => _ = DeleteSmartAsync(rule)));
            return menu;
        }

        private Task LoadSmartViewAsync(SmartPlaylist rule)
        {
            ++_collGen;
            ResetCollection(CollectionKind.Smart, "ПОДБОРКА", rule.Name, round: false);
            _collSmart = rule;
            _collKey = "smart:" + rule.Id;
            _collLabel = "ПОДБОРКА · " + rule.Name.ToUpperInvariant();
            CollCoverGlyph.Text = new SmartItem(rule, 0).Glyph;
            var tracks = EvaluateSmart(rule);
            var total = TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs));
            CollSub.Text = $"{rule.Describe()} · {tracks.Count} {Format.Plural(tracks.Count, "трек", "трека", "треков")}" +
                           (tracks.Count > 0 ? $" · {Format.Total(total)}" : "");
            CollCoverBrush.ImageSource = Covers.Load(tracks.FirstOrDefault()?.CoverUrl(400), 320);
            FillTracks(tracks, rule.Source == SmartSource.Played || rule.MinPlays > 0 || rule.DayPart != DayPart.Any
                ? "Пока пусто — подборка наполнится, когда послушаешь побольше в Undertone"
                : "Под эти правила ничего не подошло — попробуй ослабить их");
            return Task.CompletedTask;
        }

        // ────────── редактор ──────────

        private void SmartNew_Click(object sender, RoutedEventArgs e) => OpenSmartEditor(new SmartPlaylist(), isNew: true);

        private void OpenSmartEditor(SmartPlaylist rule, bool isNew)
        {
            // правим копию — «Отмена» ничего не меняет
            _smartEditing = new SmartPlaylist
            {
                Id = rule.Id, Name = rule.Name, Source = rule.Source, NotPlayedDays = rule.NotPlayedDays, MinPlays = rule.MinPlays,
                PlayedWithinDays = rule.PlayedWithinDays, DayPart = rule.DayPart, ArtistContains = rule.ArtistContains,
                Sort = rule.Sort, Limit = rule.Limit,
            };
            _smartEditingNew = isNew;
            var r = _smartEditing;
            SmartName.Text = isNew ? "" : r.Name;
            SmartArtist.Text = r.ArtistContains;
            Pills(SmartSourcePills, (int)r.Source, v => r.Source = (SmartSource)v,
                ("Лайки", 0), ("Прослушанное", 1), ("Мои файлы", 2), ("Всё", 3));
            Pills(SmartNotPlayedPills, r.NotPlayedDays, v => r.NotPlayedDays = v,
                ("Не важно", 0), ("7 дней", 7), ("30 дней", 30), ("90 дней", 90));
            Pills(SmartWithinPills, r.PlayedWithinDays, v => r.PlayedWithinDays = v,
                ("Не важно", 0), ("7 дней", 7), ("14 дней", 14), ("30 дней", 30));
            Pills(SmartPlaysPills, r.MinPlays, v => r.MinPlays = v,
                ("Не важно", 0), ("от 2", 2), ("от 3", 3), ("от 10", 10));
            Pills(SmartPartPills, (int)r.DayPart, v => r.DayPart = (DayPart)v,
                ("Не важно", 0), ("Ночью", 1), ("Утром", 2), ("Днём", 3), ("Вечером", 4));
            Pills(SmartSortPills, (int)r.Sort, v => r.Sort = (SmartSort)v,
                ("Случайно", 0), ("Чаще играли", 1), ("Недавно играли", 2), ("Давно не играли", 3), ("Новые лайки", 4));
            Pills(SmartLimitPills, r.Limit, v => r.Limit = v, ("25", 25), ("50", 50), ("100", 100), ("200", 200));
            SmartDeleteBtn.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
            UpdateSmartPreview();
            SmartEditor.Visibility = Visibility.Visible;
            Dispatcher.InvokeAsync(() => SmartName.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>Ряд кнопок-вариантов; выбор сразу пишет значение и обновляет предпросмотр.</summary>
        private void Pills(WrapPanel panel, int current, Action<int> set, params (string Text, int Value)[] options)
        {
            panel.Children.Clear();
            var group = panel.Name;
            foreach (var (text, value) in options)
            {
                var pill = new RadioButton
                {
                    Content = text, GroupName = group, Style = (Style)FindResource("ChoicePill"),
                    IsChecked = value == current, Tag = value,
                };
                pill.Checked += (_, _) =>
                {
                    set(value);
                    UpdateSmartPreview();
                };
                panel.Children.Add(pill);
            }
        }

        private void UpdateSmartPreview()
        {
            if (_smartEditing is not { } r) return;
            r.ArtistContains = SmartArtist.Text.Trim();
            var n = EvaluateSmart(r).Count;
            SmartPreview.Text = n == 0
                ? "Сейчас под эти правила ничего не подходит"
                : $"Сейчас подходит {n} {Format.Plural(n, "трек", "трека", "треков")}";
        }

        private void SmartArtist_TextChanged(object sender, TextChangedEventArgs e) => UpdateSmartPreview();

        private void CloseSmartEditor()
        {
            SmartEditor.Visibility = Visibility.Collapsed;
            _smartEditing = null;
        }

        private void SmartCancel_Click(object sender, RoutedEventArgs e)
        {
            SmartEditor.Visibility = Visibility.Collapsed;
            _smartEditing = null;
        }

        private async void SmartSave_Click(object sender, RoutedEventArgs e)
        {
            if (_smartEditing is not { } r) return;
            r.Name = SmartName.Text.Trim().Length > 0 ? SmartName.Text.Trim() : "Моя подборка";
            r.ArtistContains = SmartArtist.Text.Trim();
            var rules = SmartRules;
            var i = rules.FindIndex(x => x.Id == r.Id);
            if (i >= 0) rules[i] = r;
            else rules.Add(r);
            PersistSettings();
            SmartEditor.Visibility = Visibility.Collapsed;
            _smartEditing = null;
            RebuildSmart();
            // открыта эта подборка — показываем по новым правилам; новая — сразу открываем
            if (_smartEditingNew || (PageCollection.Visibility == Visibility.Visible && _collSmart?.Id == r.Id))
                await OpenCollectionAsync(() => LoadSmartViewAsync(r));
        }

        private async void SmartDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_smartEditing is not { } r) return;
            SmartEditor.Visibility = Visibility.Collapsed;
            _smartEditing = null;
            await DeleteSmartAsync(r);
        }

        private async Task DeleteSmartAsync(SmartPlaylist rule)
        {
            var ok = await ShowDialogAsync("Удалить подборку?", $"«{rule.Name}» исчезнет. Треки никуда не денутся.", null, "Удалить");
            if (ok == null) return;
            SmartRules.RemoveAll(x => x.Id == rule.Id);
            PersistSettings();
            _offline.Remove("smart:" + rule.Id);
            RebuildSmart();
            if (PageCollection.Visibility == Visibility.Visible && _collSmart?.Id == rule.Id) GoTo(NavSmart);
            ShowToast($"Подборка «{rule.Name}» удалена");
        }
    }
}
