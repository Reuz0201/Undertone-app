using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Undertone.Core
{
    /// <summary>Что поменялось после события Soloist.</summary>
    [Flags]
    public enum SoloistChange
    {
        None = 0,
        Auth = 1,
        Track = 2,
        Playback = 4,
        Position = 8,
        Volume = 16,
        Error = 32,
        Context = 64,
    }

    /// <summary>
    /// Состояние Spotify Soloist по событиям его WebSocket API
    /// (auth_state, playback_state, track_changed, playback_changed, position_sync, volume_changed, device_changed, error).
    /// Отдельно от процесса и сокета — чтобы разбор событий можно было проверить тестами.
    /// </summary>
    public sealed class SoloistState
    {
        public bool LoggedIn { get; private set; }
        public bool IsActive { get; private set; }
        public string DeviceName { get; private set; } = "";
        /// <summary>idle, playing, paused или buffering.</summary>
        public string Status { get; private set; } = "idle";
        /// <summary>URI текущего трека, например spotify:track:… (пусто — ничего не играет).</summary>
        public string ItemUri { get; private set; } = "";
        public long DurationMs { get; private set; }
        public int Volume { get; private set; } = -1;
        public string? LastError { get; private set; }
        /// <summary>Что играет целиком: spotify:playlist:…, spotify:album:… (пусто — одиночный трек).</summary>
        public string ContextUri { get; private set; } = "";
        /// <summary>Текущий трек как его прислал Soloist (для списка треков контекста).</summary>
        public JsonElement CurrentItem { get; private set; }

        // якорь позиции: позиция на момент timestamp_ms и скорость (0 — на паузе)
        private long _anchorMs;
        private long _anchorAt;
        private double _speed;

        public bool IsPlaying => Status == "playing";

        /// <summary>Позиция сейчас: якорь + сколько прошло с него × скорость.</summary>
        public long PositionAt(long nowUnixMs)
        {
            var ms = _anchorMs;
            if (_anchorAt > 0 && _speed > 0) ms += (long)((nowUnixMs - _anchorAt) * _speed);
            if (DurationMs > 0) ms = Math.Min(ms, DurationMs);
            return Math.Max(0, ms);
        }

        public long PositionMs => PositionAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        /// <summary>Применяет событие; возвращает, что поменялось.</summary>
        public SoloistChange Apply(JsonElement m)
        {
            if (m.ValueKind != JsonValueKind.Object) return SoloistChange.None;
            switch (Str(m, "type"))
            {
                case "auth_state":
                    LoggedIn = Bool(m, "logged_in");
                    IsActive = Bool(m, "is_active");
                    DeviceName = Str(m, "device_name");
                    return SoloistChange.Auth;

                case "playback_state":
                {
                    var change = SoloistChange.Playback | SoloistChange.Position;
                    if (m.TryGetProperty("status", out _)) Status = Str(m, "status");
                    if (m.TryGetProperty("item", out var item) && SetItem(item)) change |= SoloistChange.Track;
                    if (m.TryGetProperty("position", out var pos)) SetPosition(pos);
                    if (m.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number)
                    {
                        Volume = v.GetInt32();
                        change |= SoloistChange.Volume;
                    }
                    if (m.TryGetProperty("is_active", out _)) IsActive = Bool(m, "is_active");
                    if (m.TryGetProperty("context", out var ctx)) ContextUri = Str(ctx, "uri");
                    // снимок приходит только залогиненным
                    LoggedIn = true;
                    return change;
                }

                case "track_changed":
                    if (m.TryGetProperty("item", out var it)) SetItem(it);
                    return SoloistChange.Track;

                case "playback_changed":
                    Status = Str(m, "status");
                    if (Status != "playing") _speed = 0;
                    return SoloistChange.Playback;

                case "position_sync":
                    if (m.TryGetProperty("position", out var p)) SetPosition(p);
                    return SoloistChange.Position;

                case "volume_changed":
                    if (m.TryGetProperty("volume", out var vol) && vol.ValueKind == JsonValueKind.Number) Volume = vol.GetInt32();
                    return SoloistChange.Volume;

                case "device_changed":
                    IsActive = Bool(m, "is_active");
                    if (Str(m, "device_name") is { Length: > 0 } name) DeviceName = name;
                    return SoloistChange.Auth;

                case "context_changed":
                    ContextUri = m.TryGetProperty("context", out var c) ? Str(c, "uri") : "";
                    return SoloistChange.Context;

                case "error":
                    LastError = Str(m, "message");
                    return SoloistChange.Error;

                default:
                    return SoloistChange.None; // command_result, queue_changed, options_changed
            }
        }

        /// <summary>true — сменился трек.</summary>
        private bool SetItem(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                CurrentItem = default;
                var hadTrack = ItemUri.Length > 0;
                ItemUri = "";
                DurationMs = 0;
                return hadTrack;
            }
            CurrentItem = item.Clone();
            var uri = Str(item, "uri");
            var changed = uri != ItemUri;
            ItemUri = uri;
            DurationMs = item.TryGetProperty("decorations", out var d) &&
                         d.ValueKind == JsonValueKind.Object &&
                         d.TryGetProperty("playback", out var pb)
                ? Long(pb, "duration_ms")
                : 0;
            return changed;
        }

        private void SetPosition(JsonElement p)
        {
            if (p.ValueKind != JsonValueKind.Object) return;
            _anchorMs = Long(p, "position_ms");
            _anchorAt = Long(p, "timestamp_ms");
            _speed = p.TryGetProperty("speed", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
        }

        // ────────── треки из очереди Soloist ──────────

        /// <summary>
        /// Треки играющего контекста (плейлиста, альбома): текущий + дальше по контексту из ответа get_queue (queue_changed).
        /// Так видны треки чужих плейлистов, которые Web API больше не отдаёт.
        /// </summary>
        public static List<YaTrack> ContextTracks(JsonElement current, JsonElement queue)
        {
            var result = new List<YaTrack>();
            var seen = new HashSet<string>();
            void Add(JsonElement entity)
            {
                var t = EntityToTrack(entity);
                if (t != null && seen.Add(t.Id)) result.Add(t);
            }
            Add(current);
            if (queue.ValueKind == JsonValueKind.Object && queue.TryGetProperty("upcoming", out var up) && up.ValueKind == JsonValueKind.Array)
                foreach (var e in up.EnumerateArray())
                {
                    // «queue» — добавленное руками, «autoplay» — похожее от Spotify: это не треки плейлиста
                    var source = Str(e, "source");
                    if (source.Length > 0 && source != "context") continue;
                    if (e.TryGetProperty("item", out var item)) Add(item);
                }
            return result;
        }

        /// <summary>Сущность Soloist (decorations.*) → трек Undertone. Не трек (эпизод, реклама) — null.</summary>
        public static YaTrack? EntityToTrack(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            var uri = Str(e, "uri");
            if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
            var type = Str(e, "entity_type");
            if (type.Length > 0 && type != "track") return null;

            var deco = e.TryGetProperty("decorations", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            var creators = deco.ValueKind == JsonValueKind.Object && deco.TryGetProperty("creators", out var cr) && cr.ValueKind == JsonValueKind.Array
                ? cr.EnumerateArray()
                    .Select(c => new YaArtistRef(
                        Str(c, "uri").StartsWith("spotify:artist:", StringComparison.Ordinal) ? SpotifyApi.ToId(Str(c, "uri")) : "",
                        Name(c)))
                    .Where(a => a.Name.Length > 0)
                    .ToArray()
                : Array.Empty<YaArtistRef>();
            var parent = deco.ValueKind == JsonValueKind.Object && deco.TryGetProperty("parent", out var p) &&
                         p.ValueKind == JsonValueKind.Object && p.TryGetProperty("entity", out var pe)
                ? pe
                : default;
            var parentUri = Str(parent, "uri");

            return new YaTrack
            {
                Id = SpotifyApi.ToId(uri),
                AlbumId = parentUri.StartsWith("spotify:album:", StringComparison.Ordinal) ? SpotifyApi.ToId(parentUri) : "",
                Title = Name(e),
                Artists = creators.Select(a => a.Name).ToArray(),
                ArtistRefs = creators.Where(a => a.Id.Length > 0).ToArray(),
                Album = Name(parent),
                DurationMs = deco.ValueKind == JsonValueKind.Object && deco.TryGetProperty("playback", out var pb) ? Long(pb, "duration_ms") : 0,
                CoverUri = Cover(e) ?? Cover(parent),
                Permalink = $"https://open.spotify.com/track/{uri["spotify:track:".Length..]}",
            };
        }

        private static string Name(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return "";
            if (e.TryGetProperty("decorations", out var d) && d.ValueKind == JsonValueKind.Object &&
                d.TryGetProperty("identity", out var id) && Str(id, "name") is { Length: > 0 } n)
                return n;
            return Str(e, "name");
        }

        /// <summary>Обложка: размер побольше; spotify:image:… → https://i.scdn.co/image/….</summary>
        private static string? Cover(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object ||
                !e.TryGetProperty("decorations", out var d) || d.ValueKind != JsonValueKind.Object ||
                !d.TryGetProperty("visual_identity", out var vi) || vi.ValueKind != JsonValueKind.Object ||
                !vi.TryGetProperty("cover", out var covers) || covers.ValueKind != JsonValueKind.Array)
                return null;
            string? best = null;
            var bestRank = -1;
            foreach (var c in covers.EnumerateArray())
            {
                var url = Str(c, "url");
                if (url.Length == 0) continue;
                var rank = Str(c, "size") switch { "large" => 3, "default" => 2, "xlarge" => 1, _ => 0 };
                if (rank > bestRank) (best, bestRank) = (url, rank);
            }
            if (best != null && best.StartsWith("spotify:image:", StringComparison.Ordinal))
                best = "https://i.scdn.co/image/" + best["spotify:image:".Length..];
            return best;
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static long Long(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt64(out var n) ? n : 0;
    }
}
