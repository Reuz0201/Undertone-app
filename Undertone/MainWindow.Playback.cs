using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Undertone.Audio;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Воспроизведение, очередь, Моя волна, лайки.</summary>
    public partial class MainWindow
    {
        private readonly AudioEngine _player = new();
        private string _queueLabel = "";
        private string _waveKey = "";
        private readonly PlayQueue _queue = new();
        private readonly DispatcherTimer _progressTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };

        private bool _hasMedia;
        private bool _isPlaying;
        private bool _seeking;
        private bool _updatingProgress;
        private int _playGen;
        private CancellationTokenSource? _playCts;
        private string? _currentFile;
        private CacheDownload? _currentDownload;
        private int _failStreak;
        private bool _shownBuffering;

        /// <summary>Сколько байт ждём перед стартом потокового воспроизведения (~5 с при 320 кбит/с).</summary>
        private const int StreamStartBytes = 192 * 1024;

        /// <summary>С какого места включить трек: продолжение после перезапуска или после отключения наушников.</summary>
        private (string TrackId, TimeSpan Position)? _resumeAt;

        private string? _waveSession;
        private string? _waveBatch;
        private Task? _waveFetch;

        private void InitPlayer()
        {
            _player.MediaOpened += (_, _) =>
            {
                var d = _player.NaturalDuration ?? _queue.Current?.Duration ?? TimeSpan.Zero;
                ProgressSlider.Maximum = Math.Max(1, d.TotalSeconds);
                DurText.Text = Format.Time(d);
            };
            _player.MediaEnded += async (_, _) =>
            {
                if (RoomDefersAutoNext()) return; // в комнате следующий включает ведущий
                await NextAsync(auto: true);
            };
            _player.MediaFailed += async (_, ex) => await OnPlaybackFailedAsync(ex);
            _player.AdvancedToNext += (_, path) => OnAdvancedToNext(path);
            _player.Equalizer.SetGains(_settings.EqGains);
            _player.Equalizer.Enabled = _settings.EqEnabled;
            _player.OutputRecovered += (_, name) => ShowToast($"Звук переключён: {name}");
            _player.DeviceId = _settings.OutputDeviceId;

            // визуализаторы
            foreach (var viz in new[] { BigViz, MiniViz })
            {
                viz.Engine = _player;
                viz.IsActive = () => _isPlaying;
                viz.Mode = (VisualizerMode)Math.Clamp(_settings.VisualizerMode, 0, 3);
                viz.ModeCycled += (sender, _) =>
                {
                    var mode = ((Visualizer)sender!).Mode;
                    BigViz.Mode = mode;
                    MiniViz.Mode = mode;
                    _settings.VisualizerMode = (int)mode;
                    PersistSettings();
                };
            }

            _progressTimer.Tick += (_, _) => UpdateProgress();

            // при IsMoveToPointEnabled слайдер сам помечает клик обработанным — подписываемся с handledEventsToo
            ProgressSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((_, _) => _seeking = true), true);
            ProgressSlider.AddHandler(PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler((_, _) => CommitSeek()), true);
            ProgressSlider.LostMouseCapture += (_, _) => { if (_seeking) CommitSeek(); };
        }

        // ────────── запуск треков ──────────

        private async Task PlayFromListAsync(IList<TrackItem> items, int index, QueueSource source, string? label = null)
        {
            if (index < 0 || items.Count == 0) return;
            ReportSkip();
            _waveSession = null;
            _waveKey = "";
            _queueLabel = label ?? source switch
            {
                QueueSource.Liked => "ИЗ «МНЕ НРАВИТСЯ»",
                QueueSource.Search => "ИЗ ПОИСКА",
                _ => "СЕЙЧАС ИГРАЕТ",
            };
            _queue.Set(items.Select(i => i.Track), index, source, _settings.Shuffle);
            _queue.Repeat = _settings.Repeat;
            await PlayCurrentAsync();
        }

        private async Task JumpAheadAsync(int steps)
        {
            if (steps < 1) return;
            ReportSkip();
            if (_queue.SkipAhead(steps) != null) await PlayCurrentAsync();
        }

        private async Task PlayCurrentAsync()
        {
            var track = _queue.Current;
            var api = _api;
            if (track == null || api == null) return;

            _playCts?.Cancel();
            var cts = _playCts = new CancellationTokenSource();
            var gen = ++_playGen;

            FlushPlayReport();
            _player.Stop();
            _hasMedia = false;
            _isPlaying = false;
            // файл прошлого трека больше не наш: если загрузка этого упадёт, удалять нечего
            _currentFile = null;
            _currentDownload = null;
            _preparedNextId = null;
            ShowTrack(track);
            SetLoading(true);

            try
            {
                if (!track.Available) throw new YaApiException("трек недоступен в вашем регионе");

                // 1) уже в кэше (в нужном качестве)
                var path = FindCachedFile(track);
                StreamingMp3Reader? stream = null;
                CacheDownload? download = null;

                // 2) «без потерь» — качаем целиком (FLAC потоком не играем); не вышло — переходим на MP3
                if (path == null && EffectiveQuality == AudioQuality.Lossless && !_noLossless.Contains(track.Id))
                    path = await DownloadLosslessAsync(api, track, cts.Token);
                if (gen != _playGen) return;

                // 3) MP3 — потоком, пока докачивается
                if (path == null)
                {
                    var entry = _cache.Open(track.Id, Mp3Resolver(api, track), Mp3Ext);
                    path = entry.CompletedPath;
                    download = entry.Download;
                    if (path == null && download != null)
                    {
                        // ждём только начало файла — дальше играем, пока он докачивается
                        await download.WaitForAsync(StreamStartBytes, cts.Token);
                        if (gen != _playGen) return;

                        if (!download.IsCompleted && _settings.StreamPlayback)
                        {
                            try
                            {
                                var d = download;
                                stream = await Task.Run(() => new StreamingMp3Reader(d, track.Duration), cts.Token);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                Log.Warn($"Потоковое воспроизведение {track.Id} не завелось, жду полную загрузку", ex);
                            }
                        }
                        if (stream == null) path = await download.Completion.WaitAsync(cts.Token);
                    }
                }
                if (gen != _playGen)
                {
                    stream?.Dispose();
                    return;
                }

                if (stream != null)
                {
                    _currentDownload = download;
                    _player.Open(stream);
                }
                else
                {
                    _currentFile = path;
                    _player.Open(path!);
                }
                if (!_player.HasMedia) return; // MediaFailed уже обработан
                _player.TrackGain = GainFor(track);
                if (_resumeAt is { } resume && resume.TrackId == track.Id)
                {
                    _player.Position = resume.Position;
                    _resumeAt = null;
                }
                _player.Play();
                _hasMedia = true;
                _isPlaying = true;
                _failStreak = 0;
                _progressTimer.Start();
                UpdatePlayState();
                OnTrackStarted(track, api);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested || gen != _playGen)
            {
                // пользователь уже переключил трек
            }
            catch (Exception ex)
            {
                if (gen == _playGen) await OnPlaybackFailedAsync(ex);
            }
            finally
            {
                if (gen == _playGen) SetLoading(false);
            }
        }

        /// <summary>Трек зазвучал — обычным запуском или бесшовным переходом.</summary>
        private void OnTrackStarted(YaTrack track, YaApi api)
        {
            if (_queue.Source == QueueSource.Wave && _waveSession != null)
                _ = api.WaveFeedbackAsync(_waveSession, _waveBatch, "trackStarted", track.Id);
            BeginPlayReport(track);
            _ = PrefetchNextAsync();
            _ = EnsureWaveBufferAsync(force: false);
            SaveSession();
            RoomOnTrackStarted(track);
        }

        // ────────── переход без паузы / кроссфейд ──────────

        private string? _preparedNextId;
        private string? _prepareFailedId;

        /// <summary>У конца трека готовит следующий в движке, если он уже скачан.</summary>
        private void MaybePrepareNext()
        {
            if (_settings.CrossfadeSeconds < 0 || !_hasMedia || !_isPlaying || _inRoom) return;
            if (_player.HasPreparedNext) return;
            _preparedNextId = null;

            var duration = _player.NaturalDuration ?? TimeSpan.Zero;
            if (duration <= TimeSpan.FromSeconds(5)) return;
            var lead = TimeSpan.FromSeconds(Math.Max(0, _settings.CrossfadeSeconds) + 4);
            if (duration - _player.Position > lead) return;
            // текущий ещё качается — его настоящий конец неизвестен; волна без запаса — ждём подгрузки
            if (_player.BufferedFraction < 1) return;
            if (_queue.Source == QueueSource.Wave && _queue.Remaining == 0) return;

            var next = _queue.Repeat == RepeatMode.One ? _queue.Current : _queue.PeekNext();
            if (next == null || !next.Available || next.Id == _prepareFailedId) return;
            var path = FindCachedFile(next);
            if (path == null)
            {
                _ = PrefetchNextAsync();
                return;
            }
            if (_player.PrepareNext(path, Math.Max(0, _settings.CrossfadeSeconds), GainFor(next)))
                _preparedNextId = next.Id;
            else
                _prepareFailedId = next.Id; // этот трек переходом не играем — включится обычным способом
        }

        /// <summary>Порядок очереди поменялся — подготовленный переход больше не верен.</summary>
        private void InvalidatePreparedNext()
        {
            _preparedNextId = null;
            _player.CancelNext();
        }

        private void OnAdvancedToNext(string path)
        {
            var expected = _preparedNextId;
            _preparedNextId = null;
            var api = _api;

            FlushPlayReport(atEnd: true);
            ReportFinished();
            var moved = _queue.MoveNext(auto: true);
            if (moved == null)
            {
                StopPlayback();
                return;
            }
            if (moved.Id != expected || api == null)
            {
                // очередь успели поменять — включаем то, что в ней действительно следующее
                _ = PlayCurrentAsync();
                return;
            }

            // всё, что могло качаться для прошлого трека, больше не нужно
            _playCts?.Cancel();
            _playGen++;
            _currentFile = path;
            _currentDownload = null;
            _failStreak = 0;
            _hasMedia = true;
            _isPlaying = true;
            ShowTrack(moved);
            UpdatePlayState();
            OnTrackStarted(moved, api);
        }

        private async Task OnPlaybackFailedAsync(Exception? ex)
        {
            var track = _queue.Current;
            var gen = _playGen;
            _hasMedia = false;
            _isPlaying = false;
            UpdatePlayState();

            // нет устройства вывода (отключили наушники и т.п.) — файл в порядке, трек не пропускаем:
            // ждём, пока пользователь подключит звук и нажмёт ▶ (TogglePlay включит этот же трек заново)
            if (ex is AudioOutputException)
            {
                if (track != null && _player.HasMedia) _resumeAt = (track.Id, _player.Position);
                _player.Close();
                _progressTimer.Stop();
                ShowToast("Нет устройства вывода звука. Подключите наушники или колонки и нажмите ▶", 4);
                return;
            }

            // битый файл в кэше — удаляем только его и только при ошибке декодирования,
            // чтобы в следующий раз скачать заново. Сетевые ошибки файл не трогают.
            if (ex is AudioDecodeException && _currentFile != null)
            {
                var wasLossless = IsLosslessFile(_currentFile);
                _player.Close();
                try { File.Delete(_currentFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                _currentFile = null;

                // Windows не смогла раскодировать FLAC/MP4 — этот же трек сразу в MP3, без пропуска
                if (wasLossless && track != null)
                {
                    Log.Warn($"Lossless-файл {track.Id} не воспроизводится, переключаюсь на MP3", ex);
                    _noLossless.Add(track.Id);
                    MarkLosslessFailed();
                    if (gen == _playGen) await PlayCurrentAsync();
                    return;
                }
            }

            _failStreak++;
            Log.Error($"Не удалось воспроизвести {track?.Id} «{track}»", ex);
            ShowToast($"Не получилось включить «{track?.Title}»: {Short(ex?.Message ?? "ошибка воспроизведения")}", 3.5);

            if (_failStreak >= 4)
            {
                _failStreak = 0;
                StopPlayback();
                return;
            }
            await Task.Delay(700);
            if (gen == _playGen) await NextAsync(auto: false, report: false);
        }

        private int _prefetchGen;

        /// <summary>Качает заранее следующие треки (сколько — на странице настроек), по одному, чтобы не делить канал.</summary>
        private async Task PrefetchNextAsync()
        {
            var api = _api;
            var count = Math.Clamp(_settings.PrefetchCount, 0, 3);
            if (api == null || count == 0) return;
            var gen = ++_prefetchGen;
            foreach (var next in _queue.PeekAhead(count))
            {
                if (gen != _prefetchGen) return; // очередь сменилась — старый список уже не нужен
                if (!next.Available || FindCachedFile(next) != null) continue;
                try
                {
                    if (EffectiveQuality == AudioQuality.Lossless && !_noLossless.Contains(next.Id) &&
                        await DownloadLosslessAsync(api, next, CancellationToken.None) != null)
                        continue;
                    await _cache.GetAsync(next.Id, Mp3Resolver(api, next), CancellationToken.None, Mp3Ext);
                }
                catch (Exception) { return; /* догрузим при переключении; ошибка уже в журнале */ }
            }
        }

        // ────────── управление ──────────

        private void TogglePlay()
        {
            if (!_hasMedia)
            {
                if (_queue.Current != null) _ = PlayCurrentAsync();
                else _ = StartWaveAsync();
                return;
            }
            if (_isPlaying) _player.Pause();
            else _player.Play();
            _isPlaying = !_isPlaying;
            UpdatePlayState();
            if (!_isPlaying) SaveSession();
            RoomPublishLocal();
        }

        private async Task NextAsync(bool auto, bool report = true)
        {
            if (_queue.IsEmpty) return;
            if (report)
            {
                if (auto) ReportFinished();
                else ReportSkip();
            }

            if (_queue.Source == QueueSource.Wave && _queue.Remaining == 0 && !(auto && _queue.Repeat == RepeatMode.One))
                await EnsureWaveBufferAsync(force: true);

            if (_queue.MoveNext(auto) == null)
            {
                // список закончился
                StopPlayback();
                ProgressSlider.Value = 0;
                PosText.Text = "0:00";
                return;
            }
            await PlayCurrentAsync();
        }

        private async Task PrevAsync()
        {
            if (_queue.IsEmpty) return;
            if (_hasMedia && _player.Position.TotalSeconds > 3)
            {
                _player.Position = TimeSpan.Zero;
                UpdateProgress();
                RoomPublishLocal();
                return;
            }
            ReportSkip();
            _queue.MovePrev();
            await PlayCurrentAsync();
        }

        private void SeekBy(double seconds)
        {
            if (!_hasMedia) return;
            var target = _player.Position + TimeSpan.FromSeconds(seconds);
            var max = TimeSpan.FromSeconds(ProgressSlider.Maximum);
            _player.Position = target < TimeSpan.Zero ? TimeSpan.Zero : target > max ? max : target;
            UpdateProgress();
            PublishNowPlaying(force: true);
            RoomPublishLocal();
        }

        private void CommitSeek()
        {
            if (_seeking && _hasMedia)
            {
                _player.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
                PublishNowPlaying(force: true);
                RoomPublishLocal();
            }
            else if (_seeking && _resumeAt is { } r)
            {
                // трек ещё не загружен (продолжение сессии) — запомним, откуда начать
                _resumeAt = (r.TrackId, TimeSpan.FromSeconds(ProgressSlider.Value));
            }
            _seeking = false;
        }

        private void StopPlayback()
        {
            FlushPlayReport();
            _playCts?.Cancel();
            _playGen++;
            _player.Stop();
            _hasMedia = false;
            _isPlaying = false;
            _progressTimer.Stop();
            SetLoading(false);
            UpdatePlayState();
            RoomPublishLocal();
        }

        private void ToggleShuffle()
        {
            if (_queue.Source == QueueSource.Wave)
            {
                ShowToast("В Моей волне порядок подбирает Яндекс");
                return;
            }
            _settings.Shuffle = !_settings.Shuffle;
            _queue.SetShuffle(_settings.Shuffle);
            InvalidatePreparedNext();
            PersistSettings();
            UpdateModeButtons();
            UpdateUpNext();
            _ = PrefetchNextAsync();
            ShowToast(_settings.Shuffle ? "Перемешивание включено" : "Перемешивание выключено");
        }

        private void CycleRepeat()
        {
            _settings.Repeat = _settings.Repeat switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.Off,
            };
            _queue.Repeat = _settings.Repeat;
            InvalidatePreparedNext();
            PersistSettings();
            UpdateModeButtons();
            UpdateUpNext();
            ShowToast(_settings.Repeat switch
            {
                RepeatMode.All => "Повтор списка",
                RepeatMode.One => "Повтор трека",
                _ => "Повтор выключен",
            });
        }

        private void ToggleMute()
        {
            _player.IsMuted = !_player.IsMuted;
            UpdateVolumeGlyph();
            PersistSettings();
        }

        // ────────── Моя волна ──────────

        private Task StartWaveAsync() => StartWaveAsync(MyWaveSeeds(), "МОЯ ВОЛНА");

        private const string MyWaveSeed = "user:onyourwave";

        private bool IsMyWave => _waveKey.StartsWith(MyWaveSeed, StringComparison.Ordinal);

        /// <summary>Моя волна + настройки со страницы «Настройки» (характер, настроение, язык) — как доп. seeds.</summary>
        private string[] MyWaveSeeds()
        {
            var seeds = new List<string> { MyWaveSeed };
            if (_settings.WaveDiversity.Length > 0) seeds.Add("settingDiversity:" + _settings.WaveDiversity);
            if (_settings.WaveMood.Length > 0) seeds.Add("settingMoodEnergy:" + _settings.WaveMood);
            if (_settings.WaveLanguage.Length > 0) seeds.Add("settingLanguage:" + _settings.WaveLanguage);
            return seeds.ToArray();
        }

        /// <param name="seeds">user:onyourwave — Моя волна; artist:123 — волна по артисту</param>
        private async Task StartWaveAsync(string[] seeds, string label)
        {
            if (_api == null) return;
            var key = string.Join(",", seeds);
            if (_queue.Source == QueueSource.Wave && _waveSession != null && _queue.Current != null && _waveKey == key)
            {
                TogglePlay();
                return;
            }

            WaveCardSub.Text = "Настраиваю волну…";
            try
            {
                var batch = await _api.StartWaveAsync(seeds);
                if (batch.Tracks.Count == 0) throw new YaApiException("волна не вернула треков");

                ReportSkip();
                _waveSession = batch.SessionId;
                _waveKey = key;
                _queueLabel = label;
                _waveBatch = batch.BatchId;
                _queue.Set(batch.Tracks, 0, QueueSource.Wave);
                _queue.Repeat = _settings.Repeat == RepeatMode.One ? RepeatMode.One : RepeatMode.Off;
                _ = _api.WaveFeedbackAsync(_waveSession, _waveBatch, "radioStarted");

                GoTo(NavNow);
                await PlayCurrentAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Моя волна не запустилась", ex);
                ShowToast("Не удалось запустить Мою волну: " + Short(ex.Message), 4);
            }
            finally
            {
                UpdatePlayState();
            }
        }

        private Task EnsureWaveBufferAsync(bool force)
        {
            if (_queue.Source != QueueSource.Wave || _waveSession == null || _api == null) return Task.CompletedTask;
            if (_waveFetch is { IsCompleted: false }) return _waveFetch;
            if (!force && _queue.Remaining > 1) return Task.CompletedTask;
            return _waveFetch = FetchWaveAsync(_api, _waveSession);
        }

        private async Task FetchWaveAsync(YaApi api, string session)
        {
            try
            {
                var played = _queue.PlayedIds.TakeLast(50).ToList();
                var batch = await api.GetWaveTracksAsync(session, played);
                if (session != _waveSession) return;
                _waveBatch = batch.BatchId ?? _waveBatch;
                var known = new HashSet<string>(_queue.Upcoming(int.MaxValue).Select(t => t.Id));
                if (_queue.Current != null) known.Add(_queue.Current.Id);
                _queue.Append(batch.Tracks.Where(t => known.Add(t.Id)));
                UpdateUpNext();
                _ = PrefetchNextAsync();
            }
            catch (Exception)
            {
                // попробуем при следующем треке
            }
        }

        private void ReportSkip()
        {
            if (_queue.Source != QueueSource.Wave || _waveSession == null || _api == null || !_hasMedia) return;
            if (_queue.Current is { } t)
                _ = _api.WaveFeedbackAsync(_waveSession, _waveBatch, "skip", t.Id, _player.Position.TotalSeconds);
        }

        private void ReportFinished()
        {
            if (_queue.Source != QueueSource.Wave || _waveSession == null || _api == null) return;
            if (_queue.Current is { } t)
                _ = _api.WaveFeedbackAsync(_waveSession, _waveBatch, "trackFinished", t.Id, t.Duration.TotalSeconds);
        }

        private void ReportWaveStop() => ReportSkip();

        // ────────── лайки ──────────

        /// <summary>Треки, лайк которых сейчас отправляется: повторный клик до ответа сервера игнорируем.</summary>
        private readonly HashSet<string> _likePending = new();

        private async Task ToggleLikeAsync(YaTrack track)
        {
            if (_api == null || !_likePending.Add(track.Id)) return;
            var like = !_likedIds.Contains(track.Id);
            ApplyLike(track, like);
            try
            {
                await _api.SetLikeAsync(track, like);
                ShowToast(like ? "♥  Добавлено в «Мне нравится»" : "Удалено из «Мне нравится»");
            }
            catch (Exception ex)
            {
                Log.Warn($"Лайк {track.Id} не сохранился", ex);
                ApplyLike(track, !like);
                ShowToast("Не получилось: " + Short(ex.Message));
            }
            finally
            {
                _likePending.Remove(track.Id);
            }
        }

        private void ApplyLike(YaTrack track, bool like)
        {
            if (like)
            {
                if (_likedIds.Add(track.Id))
                {
                    _likedTracks.Insert(0, track);
                    _likedItems.Insert(0, new TrackItem(track, 1) { IsLiked = true });
                }
            }
            else
            {
                _likedIds.Remove(track.Id);
                _likedTracks.RemoveAll(t => t.Id == track.Id);
                foreach (var it in _likedItems.Where(i => i.Track.Id == track.Id).ToList())
                    _likedItems.Remove(it);
            }
            for (int i = 0; i < _likedItems.Count; i++) _likedItems[i].Index = i + 1;
            UpdateLikedHeader();
            UpdateLikedEmpty();
            RefreshLikeMarks();
            ScheduleLibrarySave();
            PublishNowPlaying();
        }

        private async Task DislikeCurrentAsync()
        {
            var track = _queue.Current;
            if (track == null || _api == null) return;
            try
            {
                await _api.DislikeAsync(track);
                if (_likedIds.Contains(track.Id)) ApplyLike(track, false);
                ShowToast("Больше не будем это рекомендовать");
            }
            catch (Exception ex)
            {
                ShowToast("Не получилось: " + Short(ex.Message));
                return;
            }
            await NextAsync(auto: false);
        }

        // ────────── UI ──────────

        private void ShowTrack(YaTrack track)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            NowState.Visibility = Visibility.Visible;

            NowTitle.Text = track.FullTitle;
            NowArtists.ItemsSource = ArtistLink.From(track.ArtistRefs);
            NowAlbum.Text = track.Album;
            NowSource.Text = string.IsNullOrEmpty(_queueLabel) ? "СЕЙЧАС ИГРАЕТ" : _queueLabel;
            BarTitle.Text = track.FullTitle;
            BarArtists.ItemsSource = ArtistLink.From(track.ArtistRefs);
            BarArtists.Visibility = Visibility.Visible;
            BarArtist.Visibility = Visibility.Collapsed;
            BarLike.IsEnabled = true;
            Title = $"{track.ArtistsText} — {track.FullTitle}";
            Taskbar.Description = Title;

            BarCoverBrush.ImageSource = Covers.Load(track.CoverUrl(200), 116);
            BigCoverBrush.ImageSource = Covers.Load(track.CoverUrl(600), 600);
            BackdropImage.Source = Covers.Load(track.CoverUrl(200), 200);

            ProgressSlider.Maximum = Math.Max(1, track.Duration.TotalSeconds);
            SetProgressValue(0);
            PosText.Text = "0:00";
            DurText.Text = Format.Time(track.Duration);

            UpdateUpNext();
            RefreshLikeMarks();
            PublishNowPlaying();
            OnLyricsTrackChanged();
        }

        private void ShowEmptyNowPlaying()
        {
            EmptyState.Visibility = Visibility.Visible;
            NowState.Visibility = Visibility.Collapsed;
            BarTitle.Text = "Ничего не играет";
            BarArtist.Text = "Включите Мою волну";
            BarArtist.Visibility = Visibility.Visible;
            BarArtists.Visibility = Visibility.Collapsed;
            BarLike.IsEnabled = false;
            BarCoverBrush.ImageSource = null;
            BackdropImage.Source = null;
            Title = "Undertone";
            _upNextItems.Clear();
            PosText.Text = DurText.Text = "0:00";
            SetProgressValue(0);
            BufferBar.Width = 0;
            PublishNowPlaying();
        }

        private void UpdateUpNext()
        {
            var current = _queue.Current?.Id;
            var upcoming = _queue.Upcoming(40);
            _upNextItems = new ObservableCollection<TrackItem>(upcoming.Select((t, i) =>
                new TrackItem(t, i + 1) { IsLiked = _likedIds.Contains(t.Id), IsPlaying = t.Id == current }));
            UpNextList.ItemsSource = _upNextItems;
            UpNextHeader.Text = _upNextItems.Count == 0
                ? (_queue.Repeat == RepeatMode.One ? "Далее · повтор трека" : "Далее · список закончится на этом треке")
                : "Далее";
        }

        private void UpdatePlayState()
        {
            var glyph = _isPlaying ? "" : "";
            PlayBtn.Content = glyph;
            WaveCardGlyph.Text = _queue.Source == QueueSource.Wave && IsMyWave && _isPlaying ? "" : "";
            WaveCardSub.Text = _queue.Source == QueueSource.Wave && _queue.Current != null && IsMyWave
                ? (_isPlaying ? "Играет" : "На паузе")
                : "Музыка под ваш вкус";

            if (Taskbar.ThumbButtonInfos.Count >= 2)
            {
                var thumb = Taskbar.ThumbButtonInfos[1];
                thumb.ImageSource = (ImageSource)Application.Current.Resources[_isPlaying ? "ThumbPauseImage" : "ThumbPlayImage"];
                thumb.Description = _isPlaying ? "Пауза" : "Играть";
            }

            if (_isPlaying) _progressTimer.Start();
            else _progressTimer.Stop();
            RefreshLikeMarks();
            PublishNowPlaying();
        }

        private void SetLoading(bool loading)
        {
            PlayBtn.Opacity = loading ? 0.6 : 1;
            if (loading) Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
            else Taskbar.ProgressState = TaskbarItemProgressState.None;
        }

        private void UpdateProgress()
        {
            if (!_hasMedia) return;
            UpdateBufferBar();
            MaybePrepareNext();
            UpdateLyricsPosition();
            var buffering = _player.IsBuffering;
            if (buffering != _shownBuffering)
            {
                _shownBuffering = buffering;
                SetLoading(buffering);
            }
            if (_seeking) return;
            var pos = _player.Position;
            SetProgressValue(pos.TotalSeconds);
            PosText.Text = Format.Time(pos);
            PublishNowPlaying(positionOnly: true);
        }

        /// <summary>Светлая полоса под ползунком — сколько трека уже скачано.</summary>
        private void UpdateBufferBar()
        {
            var fraction = _hasMedia ? _player.BufferedFraction : 0;
            BufferBar.Width = Math.Max(0, (ProgressSlider.ActualWidth - 4) * fraction);
        }

        // ────────── сессия: продолжить после перезапуска ──────────

        private void SaveSession()
        {
            if (!_settings.ResumeSession || _account == null) return;
            var (tracks, index) = _queue.Snapshot(before: 30, after: 200);
            if (index < 0) return;
            SessionStore.Save(new PlaybackSession
            {
                Uid = _account.Uid,
                Tracks = tracks,
                Index = index,
                PositionSeconds = _hasMedia ? _player.Position.TotalSeconds : _resumeAt?.Position.TotalSeconds ?? 0,
                // волну продолжаем как обычный список: сессия волны на сервере к тому времени истечёт
                Source = _queue.Source == QueueSource.Wave ? QueueSource.Collection : _queue.Source,
                Label = _queueLabel,
            });
        }

        /// <summary>Показывает трек, на котором остановились, на паузе — ▶ продолжит с того же места.</summary>
        private void RestoreSession()
        {
            if (!_settings.ResumeSession || _account == null || _queue.Current != null) return;
            var s = SessionStore.Load(_account.Uid);
            if (s == null) return;

            _waveSession = null;
            _waveKey = "";
            _queueLabel = s.Label;
            _queue.Set(s.Tracks, s.Index, s.Source, shuffle: false);
            _queue.Repeat = _settings.Repeat;
            if (_settings.Shuffle) _queue.SetShuffle(true);
            var track = _queue.Current;
            if (track == null) return;

            var pos = TimeSpan.FromSeconds(Math.Max(0, s.PositionSeconds));
            // почти доиграли — начнём трек сначала
            if (track.Duration > TimeSpan.Zero && pos > track.Duration - TimeSpan.FromSeconds(10)) pos = TimeSpan.Zero;
            _resumeAt = (track.Id, pos);
            ShowTrack(track);
            SetProgressValue(pos.TotalSeconds);
            PosText.Text = Format.Time(pos);
            UpdatePlayState();
            _ = PrefetchCurrentAsync(track);
        }

        /// <summary>Заранее качает трек, на котором остановились, чтобы ▶ сработал мгновенно.</summary>
        private async Task PrefetchCurrentAsync(YaTrack track)
        {
            var api = _api;
            if (api == null || !track.Available || FindCachedFile(track) != null) return;
            try
            {
                if (EffectiveQuality == AudioQuality.Lossless && await DownloadLosslessAsync(api, track, CancellationToken.None) != null)
                    return;
                await _cache.GetAsync(track.Id, Mp3Resolver(api, track), CancellationToken.None, Mp3Ext);
            }
            catch (Exception) { /* скачаем при нажатии ▶ */ }
        }

        private void SetProgressValue(double seconds)
        {
            _updatingProgress = true;
            ProgressSlider.Value = Math.Min(seconds, ProgressSlider.Maximum);
            _updatingProgress = false;
        }

        private DispatcherTimer? _saveTimer;

        /// <summary>Сохраняет громкость/режимы сразу (с задержкой 0.5 с, чтобы не писать файл на каждый пиксель слайдера).</summary>
        private void PersistSettings()
        {
            _settings.Volume = VolumeSlider.Value;
            _settings.Muted = _player.IsMuted;
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _saveTimer.Tick += (_, _) =>
                {
                    _saveTimer.Stop();
                    _settings.Save();
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void UpdateModeButtons()
        {
            ShuffleBtn.Foreground = _settings.Shuffle ? AccentBrushRes : DimBrush;
            RepeatBtn.Foreground = _settings.Repeat != RepeatMode.Off ? AccentBrushRes : DimBrush;
            RepeatBtn.Content = _settings.Repeat == RepeatMode.One ? "" : "";
        }

        private void UpdateVolumeGlyph()
        {
            var v = VolumeSlider.Value;
            MuteBtn.Content = _player.IsMuted || v <= 0.001 ? ""
                : v < 0.34 ? ""
                : v < 0.67 ? ""
                : "";
        }

        // ────────── обработчики ──────────

        private async void Wave_Click(object sender, RoutedEventArgs e) => await StartWaveAsync();
        private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
        private async void Next_Click(object sender, RoutedEventArgs e) => await NextAsync(auto: false);
        private async void Prev_Click(object sender, RoutedEventArgs e) => await PrevAsync();
        private void Shuffle_Click(object sender, RoutedEventArgs e) => ToggleShuffle();
        private void Repeat_Click(object sender, RoutedEventArgs e) => CycleRepeat();
        private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

        private async void Like_Click(object sender, RoutedEventArgs e)
        {
            if (_queue.Current is { } t) await ToggleLikeAsync(t);
        }

        private async void Dislike_Click(object sender, RoutedEventArgs e) => await DislikeCurrentAsync();

        private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _player.Volume = e.NewValue;
            if (_player.IsMuted && e.NewValue > 0) _player.IsMuted = false;
            UpdateVolumeGlyph();
            PersistSettings();
        }

        private void Progress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _updatingProgress) return;
            // пока тянем ползунок — показываем, куда перемотаем
            if (_seeking) PosText.Text = Format.Time(TimeSpan.FromSeconds(e.NewValue));
        }

        private void Thumb_Prev(object? sender, EventArgs e) => _ = PrevAsync();
        private void Thumb_Play(object? sender, EventArgs e) => TogglePlay();
        private void Thumb_Next(object? sender, EventArgs e) => _ = NextAsync(auto: false);
    }
}
