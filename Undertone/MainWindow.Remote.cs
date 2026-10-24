using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Undertone.Core;
using Undertone.Remote;
using Undertone.UI;

namespace Undertone
{
    /// <summary>
    /// Пульт с телефона (4.0): страница в домашней сети. Хозяин управляет музыкой, очередью и поиском,
    /// гость (вторая ссылка — для вечеринки) только ищет и предлагает треки, а хозяин одобряет их на компьютере.
    /// </summary>
    public partial class MainWindow
    {
        private RemoteServer? _remote;
        private string? _remoteHtml;
        private readonly Queue<(YaTrack Track, string Name)> _guestSuggestions = new();

        private static string NewToken() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        private string? RemoteUrl(bool guest)
        {
            if (_remote == null || RemoteServer.LanAddress() is not { } ip) return null;
            return $"http://{ip}:{_remote.Port}/?t={(guest ? _settings.RemoteGuestToken : _settings.RemoteToken)}";
        }

        /// <summary>Включает или выключает сервер по настройке.</summary>
        private void ApplyRemote()
        {
            _remote?.Dispose();
            _remote = null;
            if (_settings.RemoteEnabled && _account != null)
            {
                if (_settings.RemoteToken.Length == 0) _settings.RemoteToken = NewToken();
                if (_settings.RemoteGuestToken.Length == 0) _settings.RemoteGuestToken = NewToken();
                PersistSettings();
                try
                {
                    var server = new RemoteServer(_settings.RemoteToken, _settings.RemoteGuestToken, HandleRemoteAsync);
                    server.Start();
                    _remote = server;
                }
                catch (Exception ex)
                {
                    Log.Warn("Пульт не запустился", ex);
                    ShowToast("Пульт не запустился: " + Short(ex.Message), 4);
                }
            }
            FillRemoteSettings();
            SyncParty(full: true);
        }

        private Task<RemoteResponse> HandleRemoteAsync(RemoteRequest r) => r.Path switch
        {
            "/" or "/index.html" => Task.FromResult(RemoteResponse.Html(RemotePage())),
            "/api/state" => OnUi(() => RemoteResponse.Json(RemoteState(r.IsOwner))),
            "/api/cmd" when r.IsOwner && r.Method == "POST" => OnUiAsync(() => RemoteCommandAsync(r.Body)),
            "/api/search" => RemoteSearchAsync(r.Query.GetValueOrDefault("q", ""), r.IsOwner),
            "/api/add" when r.IsOwner && r.Method == "POST" => RemoteAddAsync(r.Body, suggest: false),
            "/api/suggest" when r.Method == "POST" => RemoteAddAsync(r.Body, suggest: true),
            "/cover" => Task.FromResult(RemoteCover(r.Query.GetValueOrDefault("id", ""))),
            _ => Task.FromResult(RemoteResponse.Text(404, "not found")),
        };

        private Task<T> OnUi<T>(Func<T> f) => Dispatcher.InvokeAsync(f).Task;
        private Task<T> OnUiAsync<T>(Func<Task<T>> f) => Dispatcher.InvokeAsync(f).Task.Unwrap();

        private string RemotePage()
        {
            if (_remoteHtml != null) return _remoteHtml;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Undertone.Remote.remote.html");
            if (stream == null) return "<h1>Undertone</h1>";
            using var reader = new StreamReader(stream);
            return _remoteHtml = reader.ReadToEnd();
        }

        /// <summary>Обложка для телефона: у Яндекса — прямая ссылка, у своего файла — через наш сервер.</summary>
        private string RemoteCoverUrl(YaTrack t, string token) =>
            LocalLibrary.IsLocal(t) ? (t.CoverUri != null ? $"/cover?id={t.Id}&t={token}" : "") : t.CoverUrl(400) ?? "";

        private string RemoteState(bool owner)
        {
            var token = owner ? _settings.RemoteToken : _settings.RemoteGuestToken;
            var t = _queue.Current;
            var position = _hasMedia ? _player.Position : _resumeAt?.Position ?? TimeSpan.Zero;
            var duration = _player.NaturalDuration ?? t?.Duration ?? TimeSpan.Zero;
            var added = _inRoom && _friendsFeed?.Room is { } room ? room.Added : null;
            var queue = _queue.Upcoming(25).Select((q, i) => new JsonObject
            {
                ["i"] = i,
                ["title"] = q.FullTitle,
                ["artists"] = q.ArtistsText,
                ["cover"] = RemoteCoverUrl(q, token),
                ["added"] = added != null && added.TryGetValue(q.Id, out var who) ? "добавил " + Format.PublicName(who) : "",
            }).ToArray<JsonNode?>();
            var accent = _accentShown;
            var json = new JsonObject
            {
                ["owner"] = owner,
                ["title"] = t?.FullTitle ?? "",
                ["artists"] = t?.ArtistsText ?? "",
                ["cover"] = t != null ? RemoteCoverUrl(t, token) : "",
                ["source"] = string.IsNullOrEmpty(_queueLabel) ? "СЕЙЧАС ИГРАЕТ" : _queueLabel,
                ["playing"] = _isPlaying,
                ["position"] = position.TotalSeconds,
                ["duration"] = duration.TotalSeconds,
                ["liked"] = t != null && _likedIds.Contains(t.Id),
                ["volume"] = VolumeSlider.Value,
                ["accent"] = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}",
                ["room"] = _inRoom ? "Слушаем вместе: " + PartyRoomNames() : "",
                ["queue"] = new JsonArray(queue),
            };
            return json.ToJsonString();
        }

        private async Task<RemoteResponse> RemoteCommandAsync(string body)
        {
            using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}");
            var a = doc.RootElement.TryGetProperty("a", out var av) ? av.GetString() ?? "" : "";
            var v = doc.RootElement.TryGetProperty("v", out var vv) && vv.ValueKind == JsonValueKind.Number ? vv.GetDouble() : 0;
            switch (a)
            {
                case "toggle": TogglePlay(); break;
                case "next": await NextAsync(auto: false); break;
                case "prev": await PrevAsync(); break;
                case "like": if (_queue.Current is { } c) await ToggleLikeAsync(c); break;
                case "volume": VolumeSlider.Value = Math.Clamp(v, 0, 1); break;
                case "seek":
                    if (_hasMedia)
                    {
                        _player.Position = TimeSpan.FromSeconds(Math.Max(0, v));
                        UpdateProgress();
                        PublishNowPlaying(force: true);
                        RoomPublishLocal();
                    }
                    break;
                case "jump": await JumpAheadAsync((int)v + 1); break;
                case "remove":
                    if (_queue.RemoveUpcomingAt((int)v)) QueueEdited();
                    break;
            }
            return RemoteResponse.Json("{\"ok\":true}");
        }

        private async Task<RemoteResponse> RemoteSearchAsync(string q, bool owner)
        {
            q = q.Trim();
            if (q.Length == 0) return RemoteResponse.Json("{\"tracks\":[]}");
            var token = owner ? _settings.RemoteToken : _settings.RemoteGuestToken;
            var tracks = new List<YaTrack>();
            // свои файлы ищем только для хозяина: гостю их не включить
            if (owner) tracks.AddRange(await OnUi(() => _local.Search(q, 3)));
            if (_api is { } api)
            {
                var r = await api.SearchAsync(q);
                tracks.AddRange(r.Tracks.Where(t => t.Available).Take(15));
            }
            var arr = new JsonArray(tracks.Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t.Id, ["albumId"] = t.AlbumId, ["title"] = t.FullTitle, ["artists"] = t.ArtistsText,
                ["cover"] = RemoteCoverUrl(t, token), ["duration"] = t.Duration.TotalSeconds,
            }).ToArray());
            return RemoteResponse.Json(new JsonObject { ["tracks"] = arr }.ToJsonString());
        }

        private async Task<RemoteResponse> RemoteAddAsync(string body, bool suggest)
        {
            using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}");
            string S(string k) => doc.RootElement.TryGetProperty(k, out var e) ? e.GetString() ?? "" : "";
            var id = S("id");
            var albumId = S("albumId");
            var mode = S("mode");
            var name = Format.PublicName(S("name")).Trim();
            if (id.Length == 0) return RemoteResponse.Text(400, "no id");

            YaTrack? track;
            if (LocalLibrary.IsLocal(new YaTrack { Id = id }))
            {
                if (suggest) return RemoteResponse.Text(400, "local");
                track = await OnUi(() => _local.Tracks().FirstOrDefault(t => t.Id == id));
            }
            else
            {
                if (_api == null) return RemoteResponse.Text(500, "no api");
                track = (await _api.GetTracksAsync(new[] { albumId.Length > 0 ? $"{id}:{albumId}" : id })).FirstOrDefault();
            }
            if (track == null) return RemoteResponse.Text(404, "track");

            await OnUiAsync(async () =>
            {
                if (suggest) EnqueueGuestSuggestion(track, name.Length > 0 ? name : "Гость");
                else if (mode == "now")
                    await PlayFromListAsync(new List<TrackItem> { new(track, 1) }, 0, QueueSource.Collection, "С ТЕЛЕФОНА");
                else if (mode == "next") PlayNext(track);
                else AddToQueueEnd(track);
                return true;
            });
            return RemoteResponse.Json("{\"ok\":true}");
        }

        private RemoteResponse RemoteCover(string id)
        {
            var track = _local.Tracks().FirstOrDefault(t => t.Id == id);
            if (track?.CoverUri is not { } uri || !uri.StartsWith("file:", StringComparison.Ordinal)) return RemoteResponse.Text(404, "no cover");
            try
            {
                return new RemoteResponse(200, "image/jpeg", File.ReadAllBytes(new Uri(uri).LocalPath));
            }
            catch (Exception) { return RemoteResponse.Text(404, "no cover"); }
        }

        // ────────── предложения гостей: карточка на компьютере ──────────

        private void EnqueueGuestSuggestion(YaTrack track, string name)
        {
            if (_guestSuggestions.Any(s => s.Track.Id == track.Id) || _queue.Current?.Id == track.Id) return;
            _guestSuggestions.Enqueue((track, name));
            ShowGuestSuggestion();
        }

        private void ShowGuestSuggestion()
        {
            if (_guestSuggestions.Count == 0)
            {
                GuestCard.Visibility = Visibility.Collapsed;
                return;
            }
            var (track, name) = _guestSuggestions.Peek();
            GuestCardCover.ImageSource = Covers.Load(track.CoverUrl(100), 100);
            GuestCardTitle.Text = track.FullTitle;
            GuestCardSub.Text = $"{track.ArtistsText} · предлагает {name}" +
                                (_guestSuggestions.Count > 1 ? $" · ещё {_guestSuggestions.Count - 1}" : "");
            GuestCard.Visibility = Visibility.Visible;
        }

        private void GuestAccept_Click(object sender, RoutedEventArgs e)
        {
            if (_guestSuggestions.Count == 0) return;
            var (track, name) = _guestSuggestions.Dequeue();
            _suggestedBy[track.Id] = name; // в комнате подпишется «добавил …»
            AddToQueueEnd(track);
            ShowGuestSuggestion();
        }

        private void GuestDecline_Click(object sender, RoutedEventArgs e)
        {
            if (_guestSuggestions.Count > 0) _guestSuggestions.Dequeue();
            ShowGuestSuggestion();
        }

        // ────────── настройки ──────────

        private void FillRemoteSettings()
        {
            RemoteToggle.IsChecked = _settings.RemoteEnabled;
            var url = RemoteUrl(guest: false);
            var guestUrl = RemoteUrl(guest: true);
            RemoteDetails.Visibility = url != null ? Visibility.Visible : Visibility.Collapsed;
            RemoteStatus.Text = !_settings.RemoteEnabled ? ""
                : url == null ? "Не вижу домашнюю сеть — подключись к Wi-Fi (VPN может мешать)"
                : "Отсканируй камерой телефона. Телефон и компьютер должны быть в одной Wi-Fi сети.";
            if (url == null) return;
            RemoteQr.Source = Qr.Make(url);
            RemoteUrlText.Text = url.Split("?t=")[0];
            RemoteGuestQr.Source = guestUrl != null ? Qr.Make(guestUrl) : null;
        }

        private void RemoteToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_fillingSettings) return;
            _settings.RemoteEnabled = RemoteToggle.IsChecked == true;
            PersistSettings();
            ApplyRemote();
            if (_settings.RemoteEnabled && _remote != null)
                ShowToast("Пульт включён. Если Windows спросит про брандмауэр — разреши «Частные сети»", 5);
        }

        private async void RemoteReset_Click(object sender, RoutedEventArgs e)
        {
            var ok = await ShowDialogAsync("Новые ссылки?", "Старые QR перестанут работать — телефоны придётся отсканировать заново.", null, "Сменить");
            if (ok == null) return;
            _settings.RemoteToken = NewToken();
            _settings.RemoteGuestToken = NewToken();
            PersistSettings();
            ApplyRemote();
            ShowToast("Ссылки обновлены");
        }
    }
}
