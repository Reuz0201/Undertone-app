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
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Воспроизведение, очередь, Моя волна, лайки.</summary>
    public partial class MainWindow
    {
        private readonly MediaPlayer _player = new();
        private readonly PlayQueue _queue = new();
        private readonly DispatcherTimer _progressTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };

        private bool _hasMedia;
        private bool _isPlaying;
        private bool _seeking;
        private bool _updatingProgress;
        private int _playGen;
        private CancellationTokenSource? _playCts;
        private string? _currentFile;
        private int _failStreak;

        private string? _waveSession;
        private string? _waveBatch;
        private Task? _waveFetch;

        private void InitPlayer()
        {
            _player.MediaOpened += (_, _) =>
            {
                var d = _player.NaturalDuration.HasTimeSpan
                    ? _player.NaturalDuration.TimeSpan
                    : _queue.Current?.Duration ?? TimeSpan.Zero;
                ProgressSlider.Maximum = Math.Max(1, d.TotalSeconds);
                DurText.Text = Format.Time(d);
            };
            _player.MediaEnded += async (_, _) => await NextAsync(auto: true);
            _player.MediaFailed += async (_, e) => await OnPlaybackFailedAsync(e.ErrorException);

            _progressTimer.Tick += (_, _) => UpdateProgress();

            // при IsMoveToPointEnabled слайдер сам помечает клик обработанным — подписываемся с handledEventsToo
            ProgressSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((_, _) => _seeking = true), true);
            ProgressSlider.AddHandler(PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler((_, _) => CommitSeek()), true);
            ProgressSlider.LostMouseCapture += (_, _) => { if (_seeking) CommitSeek(); };
        }

        // ────────── запуск треков ──────────

        private async Task PlayFromListAsync(IList<TrackItem> items, int index, QueueSource source)
        {
            if (index < 0 || items.Count == 0) return;
            ReportSkip();
            _waveSession = null;
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

            _player.Stop();
            _hasMedia = false;
            _isPlaying = false;
            ShowTrack(track);
            SetLoading(true);

            try
            {
                if (!track.Available) throw new YaApiException("трек недоступен в вашем регионе");

                var path = await _cache.GetAsync(track.Id, () => api.GetDirectUrlAsync(track.Id), cts.Token);
                if (gen != _playGen) return;

                _currentFile = path;
                _player.Open(new Uri(path));
                _player.Play();
                _hasMedia = true;
                _isPlaying = true;
                _failStreak = 0;
                _progressTimer.Start();
                UpdatePlayState();

                if (_queue.Source == QueueSource.Wave && _waveSession != null)
                    _ = api.WaveFeedbackAsync(_waveSession, _waveBatch, "trackStarted", track.Id);

                _ = PrefetchNextAsync();
                _ = EnsureWaveBufferAsync(force: false);
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

        private async Task OnPlaybackFailedAsync(Exception? ex)
        {
            var track = _queue.Current;
            var gen = _playGen;
            _hasMedia = false;
            _isPlaying = false;
            UpdatePlayState();

            // битый файл в кэше — удаляем, чтобы в следующий раз скачать заново
            if (_currentFile != null && ex is not YaApiException)
            {
                _player.Close();
                try { File.Delete(_currentFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                _currentFile = null;
            }

            _failStreak++;
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

        private async Task PrefetchNextAsync()
        {
            var next = _queue.PeekNext();
            var api = _api;
            if (next == null || api == null || !next.Available || _cache.Has(next.Id)) return;
            try { await _cache.GetAsync(next.Id, () => api.GetDirectUrlAsync(next.Id)); }
            catch (Exception) { /* догрузим при переключении */ }
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
        }

        private void CommitSeek()
        {
            if (_seeking && _hasMedia)
                _player.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
            _seeking = false;
        }

        private void StopPlayback()
        {
            _playCts?.Cancel();
            _playGen++;
            _player.Stop();
            _hasMedia = false;
            _isPlaying = false;
            _progressTimer.Stop();
            SetLoading(false);
            UpdatePlayState();
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

        private async Task StartWaveAsync()
        {
            if (_api == null) return;
            if (_queue.Source == QueueSource.Wave && _waveSession != null && _queue.Current != null)
            {
                TogglePlay();
                return;
            }

            WaveCardSub.Text = "Настраиваю волну…";
            try
            {
                var batch = await _api.StartWaveAsync(new[] { "user:onyourwave" });
                if (batch.Tracks.Count == 0) throw new YaApiException("волна не вернула треков");

                ReportSkip();
                _waveSession = batch.SessionId;
                _waveBatch = batch.BatchId;
                _queue.Set(batch.Tracks, 0, QueueSource.Wave);
                _queue.Repeat = _settings.Repeat == RepeatMode.One ? RepeatMode.One : RepeatMode.Off;
                _ = _api.WaveFeedbackAsync(_waveSession, _waveBatch, "radioStarted");

                GoTo(NavNow);
                await PlayCurrentAsync();
            }
            catch (Exception ex)
            {
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

        private async Task ToggleLikeAsync(YaTrack track)
        {
            if (_api == null) return;
            var like = !_likedIds.Contains(track.Id);
            ApplyLike(track, like);
            try
            {
                await _api.SetLikeAsync(track, like);
                ShowToast(like ? "♥  Добавлено в «Мне нравится»" : "Удалено из «Мне нравится»");
            }
            catch (Exception ex)
            {
                ApplyLike(track, !like);
                ShowToast("Не получилось: " + Short(ex.Message));
            }
        }

        private void ApplyLike(YaTrack track, bool like)
        {
            if (like)
            {
                if (_likedIds.Add(track.Id))
                {
                    _likedTracks.Insert(0, track);
                    if (Matches(track, LikedFilter.Text.Trim()))
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
            LikedEmpty.Visibility = _likedItems.Count == 0 && LikedLoading.Visibility != Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
            if (_likedItems.Count == 0) LikedEmpty.Text = "Здесь будут треки, которые вам понравились.\nНажмите ♥ у любого трека.";
            RefreshLikeMarks();
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
            NowArtist.Text = track.ArtistsText;
            NowAlbum.Text = track.Album;
            NowSource.Text = _queue.Source switch
            {
                QueueSource.Wave => "МОЯ ВОЛНА",
                QueueSource.Liked => "ИЗ «МНЕ НРАВИТСЯ»",
                QueueSource.Search => "ИЗ ПОИСКА",
                _ => "СЕЙЧАС ИГРАЕТ",
            };
            BarTitle.Text = track.FullTitle;
            BarArtist.Text = track.ArtistsText;
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
        }

        private void ShowEmptyNowPlaying()
        {
            EmptyState.Visibility = Visibility.Visible;
            NowState.Visibility = Visibility.Collapsed;
            BarTitle.Text = "Ничего не играет";
            BarArtist.Text = "Включите Мою волну";
            BarLike.IsEnabled = false;
            BarCoverBrush.ImageSource = null;
            BackdropImage.Source = null;
            Title = "Undertone";
            _upNextItems.Clear();
            PosText.Text = DurText.Text = "0:00";
            SetProgressValue(0);
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
            WaveCardGlyph.Text = _queue.Source == QueueSource.Wave && _isPlaying ? "" : "";
            WaveCardSub.Text = _queue.Source == QueueSource.Wave && _queue.Current != null
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
            UpdateTray();
        }

        private void SetLoading(bool loading)
        {
            PlayBtn.Opacity = loading ? 0.6 : 1;
            if (loading) Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
            else Taskbar.ProgressState = TaskbarItemProgressState.None;
        }

        private void UpdateProgress()
        {
            if (!_hasMedia || _seeking) return;
            var pos = _player.Position;
            SetProgressValue(pos.TotalSeconds);
            PosText.Text = Format.Time(pos);
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
