using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Undertone.Audio;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// DJ-режим: каждый скачанный трек анализируется (темп, сетка, тональность, где затихает), и переход
    /// к следующему строится как у диджея — вход на сильную долю, подгон темпа, смена баса посередине.
    /// «Умный порядок» переставляет ближайшие треки очереди, чтобы соседние лучше сводились.
    /// </summary>
    public partial class MainWindow
    {
        private readonly DjStore _djStore = DjStore.Open();
        /// <summary>Анализ — по одному треку за раз, чтобы не отнимать процессор у звука.</summary>
        private readonly SemaphoreSlim _djGate = new(1, 1);
        private readonly HashSet<string> _djPending = new();
        private readonly HashSet<string> _djFailed = new();
        /// <summary>Для какого текущего трека умный порядок уже решён.</summary>
        private string? _djOrderedFor;
        /// <summary>Для какого текущего трека уже попросили докачать следующие.</summary>
        private string? _djPrefetchedFor;
        private TransitionPlan? _djPlan;
        private bool _djMixAnnounced;
        private int _djTick;
        private bool _djUpdating;
        private DispatcherTimer? _djUiTimer;

        /// <summary>Сколько следующих треков держать скачанными и проанализированными.</summary>
        private int DjLookahead => _settings.DjSmartOrder ? 4 : 1;

        private DjBlend DjBlendSetting => _settings.DjBars switch
        {
            <= 4 => DjBlend.Short,
            >= 16 => DjBlend.Long,
            _ => DjBlend.Medium,
        };

        // ────────── включение и окошко ──────────

        private void Dj_Click(object sender, RoutedEventArgs e) => ToggleDjPopup();

        private void ToggleDjPopup()
        {
            UpdateDjUi();
            DjPopup.IsOpen = !DjPopup.IsOpen;
        }

        /// <summary>Горячая клавиша J — сразу включить или выключить, без окошка.</summary>
        private void ToggleDj() => SetDjMode(!_settings.DjMode, announce: true);

        private void SetDjMode(bool on, bool announce)
        {
            if (_settings.DjMode == on) return;
            _settings.DjMode = on;
            _settings.Save();
            _djOrderedFor = null;
            _djPrefetchedFor = null;
            // переход уже подготовлен в старом режиме — пересоберём в новом
            InvalidatePreparedNext();
            UpdateDjUi();
            if (on)
            {
                DjKick();
                _ = PrefetchNextAsync();
            }
            if (!announce) return;
            if (!on) ShowToast("DJ-режим выключен");
            else if (_inRoom) ShowToast("DJ-режим включится, когда выйдешь из комнаты — там треки у всех меняются одновременно", 4);
            else if (_player.IsExternal) ShowToast("DJ-режим работает с Яндекс Музыкой, SoundCloud и своими файлами — Spotify играет сам", 4);
            else ShowToast("DJ-режим включён — дальше свожу сам");
        }

        private void DjToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_djUpdating || !IsLoaded) return;
            SetDjMode(DjToggle.IsChecked == true, announce: false);
        }

        private void DjBlend_Checked(object sender, RoutedEventArgs e)
        {
            if (_djUpdating || !IsLoaded || sender is not FrameworkElement pill) return;
            _settings.DjBars = Math.Clamp(TagValue(pill), 4, 16);
            _settings.Save();
            InvalidatePreparedNext();
        }

        private void DjOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_djUpdating || !IsLoaded) return;
            _settings.DjMatchTempo = DjTempoToggle.IsChecked == true;
            var order = DjOrderToggle.IsChecked == true;
            if (order && !_settings.DjSmartOrder) _djOrderedFor = null;
            _settings.DjSmartOrder = order;
            _settings.Save();
            InvalidatePreparedNext();
            if (order) _ = PrefetchNextAsync();
        }

        private void DjPopup_Opened(object? sender, EventArgs e)
        {
            UpdateDjUi();
            _djUiTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
                (_, _) => UpdateDjStatus(), Dispatcher);
            _djUiTimer.Start();
        }

        private void DjPopup_Closed(object? sender, EventArgs e) => _djUiTimer?.Stop();

        private void UpdateDjUi()
        {
            _djUpdating = true;
            DjToggle.IsChecked = _settings.DjMode;
            DjTempoToggle.IsChecked = _settings.DjMatchTempo;
            DjOrderToggle.IsChecked = _settings.DjSmartOrder;
            SelectPill(DjBlendPanel, (int)DjBlendSetting);
            _djUpdating = false;

            DjDot.Visibility = _settings.DjMode ? Visibility.Visible : Visibility.Collapsed;
            if (_settings.DjMode) DjBtn.SetResourceReference(ForegroundProperty, "LiveAccent");
            else DjBtn.ClearValue(ForegroundProperty);
            UpdateDjStatus();
        }

        /// <summary>Строчки в окошке: темп и тональность текущего и следующего, что DJ сейчас делает.</summary>
        private void UpdateDjStatus()
        {
            var cur = _queue.Current;
            var next = cur == null ? null : _queue.Repeat == RepeatMode.One ? cur : _queue.PeekNext();
            var curA = cur == null ? null : _djStore.Get(cur.Id);
            var nextA = next == null ? null : _djStore.Get(next.Id);
            DjNowInfo.Text = cur == null ? "—" : Describe(cur, curA);
            DjNextInfo.Text = next == null ? "—" : Describe(next, nextA);

            var progress = _player.MixProgress;
            DjMixBar.Width = progress >= 0 ? 304 * progress : 0;
            string status;
            if (!_settings.DjMode) status = "Выключен — переходы как в настройках воспроизведения.";
            else if (_inRoom) status = "На паузе: в комнате треки у всех меняются одновременно.";
            else if (_player.IsExternal) status = "Spotify играет сам — сводить его треки нельзя.";
            else if (progress >= 0 && _djPlan is { } live)
                status = live.BeatMatched
                    ? $"Свожу в такт{RateText(live.Rate)} · {(int)DjBlendSetting} тактов"
                    : "Плавный переход — темпы слишком разные для сведения в такт";
            else if (_player.HasPreparedNext && _djPlan is { } plan)
            {
                var left = plan.MixOutAt - _player.Position.TotalSeconds;
                status = (plan.BeatMatched ? "Сведение в такт" : "Плавный переход") +
                         (left > 0 ? $" через {Format.Time(TimeSpan.FromSeconds(left))}" : "");
            }
            else if (cur != null && (_djPending.Contains(cur.Id) || (next != null && _djPending.Contains(next.Id))))
                status = "Слушаю треки: ищу бит и тональность…";
            else if (next != null && FindCachedFile(next) == null) status = "Жду, пока скачается следующий трек.";
            else status = "Готов. Переход начнётся ближе к концу трека.";
            DjStatus.Text = status;
        }

        private static string Describe(YaTrack track, TrackAnalysis? a)
        {
            if (a == null) return "…";
            var parts = new List<string>();
            if (a.HasBeat) parts.Add($"{a.Bpm:0} BPM");
            if (a.KeyRoot >= 0) parts.Add($"{Camelot.Name(a.KeyRoot, a.KeyMinor)} · {a.Camelot}");
            return parts.Count == 0 ? "без бита" : string.Join(" · ", parts);
        }

        private static string RateText(double rate)
        {
            var percent = (rate - 1) * 100;
            return Math.Abs(percent) < 0.3 ? "" : $", темп {percent:+0.0;-0.0}%";
        }

        // ────────── анализ ──────────

        /// <summary>Сразу заказать анализ текущего и ближайших треков (если они уже скачаны).</summary>
        private void DjKick()
        {
            if (!_settings.DjMode || _queue.Current is not { } cur) return;
            DjRequestAnalysis(cur);
            foreach (var t in _queue.PeekAhead(DjLookahead)) DjRequestAnalysis(t);
        }

        private void DjRequestAnalysis(YaTrack track)
        {
            if (!track.Available || _djPending.Contains(track.Id) || _djFailed.Contains(track.Id)) return;
            if (_djStore.Get(track.Id) != null) return;
            var path = FindCachedFile(track);
            if (path == null) return;
            _djPending.Add(track.Id);
            _ = DjAnalyzeAsync(track, path);
        }

        private async Task DjAnalyzeAsync(YaTrack track, string path)
        {
            await _djGate.WaitAsync();
            try
            {
                var started = DateTime.UtcNow;
                var result = await Task.Factory.StartNew(() =>
                {
                    // отдельный поток пониже приоритетом: звук и интерфейс важнее
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    var mono = DjDecoder.DecodeMono(path, CancellationToken.None);
                    return mono == null ? null : DjAnalyzer.Analyze(mono, DjAnalyzer.PreferredSampleRate);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                if (result == null)
                {
                    _djFailed.Add(track.Id);
                    return;
                }
                _djStore.Put(track.Id, result);
                _ = _djStore.SaveAsync();
                Log.Info($"DJ: «{track.FullTitle}» — {(result.HasBeat ? $"{result.Bpm:0.0} BPM" : "без бита")}, " +
                         $"{Camelot.Name(result.KeyRoot, result.KeyMinor)} {result.Camelot}, " +
                         $"{(DateTime.UtcNow - started).TotalSeconds:0.0} с");
                if (DjPopup.IsOpen) UpdateDjStatus();
            }
            catch (Exception ex)
            {
                Log.Warn($"DJ: не удалось проанализировать {track.Id}", ex);
                _djFailed.Add(track.Id);
            }
            finally
            {
                _djPending.Remove(track.Id);
                _djGate.Release();
            }
        }

        // ────────── умный порядок ──────────

        /// <summary>
        /// Ставит следующим тот из ближайших треков, что лучше сводится с текущим. Решает один раз за трек:
        /// когда все кандидаты проанализированы, или принудительно (force) — когда пора готовить переход.
        /// </summary>
        private void DjSmartOrder(bool force)
        {
            if (!_settings.DjSmartOrder || _queue.Repeat == RepeatMode.One || _player.HasPreparedNext) return;
            if (_queue.Current is not { } cur || _djOrderedFor == cur.Id) return;
            var curA = _djStore.Get(cur.Id);
            if (curA == null)
            {
                if (force) _djOrderedFor = cur.Id;
                return;
            }
            var upcoming = _queue.Upcoming(DjLookahead);
            if (upcoming.Count < 2)
            {
                if (force) _djOrderedFor = cur.Id;
                return;
            }
            var analyses = upcoming.Select(t => (TrackAnalysis?)_djStore.Get(t.Id)).ToList();
            var waiting = upcoming.Any(t => _djPending.Contains(t.Id) || (t.Available && !_djFailed.Contains(t.Id) && _djStore.Get(t.Id) == null && FindCachedFile(t) != null));
            if (!force && (waiting || analyses.Any(a => a == null))) return;

            _djOrderedFor = cur.Id;
            var pick = DjPlanner.PickNext(curA, analyses);
            if (pick <= 0 || !_queue.MoveUpcoming(pick, 0)) return;
            var chosen = upcoming[pick];
            Log.Info($"DJ: следующим поставлен «{chosen.FullTitle}» (был {pick + 1}-м) — лучше сводится");
            QueueEdited();
            ShowToast($"DJ: дальше «{chosen.FullTitle}» — лучше ляжет по темпу и тональности", 3.2);
        }

        // ────────── подготовка перехода ──────────

        /// <summary>Вызывается вместо обычной подготовки перехода, пока включён DJ-режим (каждые 200 мс).</summary>
        private void DjMaybePrepare()
        {
            if (!_hasMedia || !_isPlaying || _player.IsExternal) return;

            // раз в секунду — заказать анализ того, что уже скачалось
            if (++_djTick % 5 == 0) DjKick();

            var progress = _player.MixProgress;
            if (progress >= 0 && !_djMixAnnounced && _djPlan is { BeatMatched: true } mixing && _queue.PeekNext() is { } incoming)
            {
                _djMixAnnounced = true;
                var a = _djStore.Get(incoming.Id);
                ShowToast($"DJ сводит → «{incoming.FullTitle}»" + (a is { HasBeat: true } ? $" · {a.Bpm * mixing.Rate:0} BPM" : ""), 3);
            }

            if (_player.HasPreparedNext) return;
            _preparedNextId = null;
            _djPlan = null;
            _djMixAnnounced = false;

            var duration = (_player.NaturalDuration ?? TimeSpan.Zero).TotalSeconds;
            if (duration <= 20) return; // джинглы и превью сводить нечего — обычный переход
            if (_queue.Source == QueueSource.Wave && _queue.Remaining == 0) return;
            var cur = _queue.Current;
            if (cur == null) return;
            var pos = _player.Position.TotalSeconds;
            var curA = _djStore.Get(cur.Id);

            // где примерно начнётся переход (точно — когда будет анализ следующего)
            var rough = DjPlanner.Plan(curA, duration, null, DjBlendSetting, _settings.DjMatchTempo);
            var roughStart = rough.MixOutAt - (int)DjBlendSetting * 2.5; // запас на длинный переход в такт
            if (pos >= duration * 0.25 || pos >= roughStart - 20) DjSmartOrder(force: true);
            else DjSmartOrder(force: false);

            var next = _queue.Repeat == RepeatMode.One ? cur : _queue.PeekNext();
            if (next == null || !next.Available || next.Id == _prepareFailedId) return;

            if (pos < roughStart - 45) return;
            var path = FindCachedFile(next);
            if (path == null)
            {
                if (_djPrefetchedFor != cur.Id)
                {
                    _djPrefetchedFor = cur.Id;
                    _ = PrefetchNextAsync();
                }
                return;
            }
            DjRequestAnalysis(next);
            if (pos < roughStart - 12) return;
            // текущий ещё качается — его конец неизвестен
            if (_player.BufferedFraction < 1) return;

            var nextA = _djStore.Get(next.Id);
            var plan = DjPlanner.Plan(curA, duration, nextA, DjBlendSetting, _settings.DjMatchTempo, pos);
            // анализ вот-вот будет — подождём, пока есть время
            if ((nextA == null && _djPending.Contains(next.Id) || curA == null && _djPending.Contains(cur.Id))
                && pos < plan.MixOutAt - 3) return;
            if (pos > plan.MixOutAt - 0.4) return; // проскочили — трек доиграет и сменится обычным способом

            if (_player.PrepareTransition(path, plan, GainFor(next)))
            {
                _preparedNextId = next.Id;
                _djPlan = plan;
                Log.Info($"DJ: переход в «{next.FullTitle}» с {plan.MixOutAt:0.0} с, {plan.Length:0.0} с, " +
                         $"вход с {plan.NextStartAt:0.00} с, скорость {plan.Rate:0.000}{(plan.BeatMatched ? ", в такт" : "")}");
            }
            else _prepareFailedId = next.Id;
        }
    }
}
