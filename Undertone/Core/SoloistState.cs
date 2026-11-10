using System;
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

                case "error":
                    LastError = Str(m, "message");
                    return SoloistChange.Error;

                default:
                    return SoloistChange.None; // command_result, queue_changed, options_changed, context_changed
            }
        }

        /// <summary>true — сменился трек.</summary>
        private bool SetItem(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                var hadTrack = ItemUri.Length > 0;
                ItemUri = "";
                DurationMs = 0;
                return hadTrack;
            }
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
