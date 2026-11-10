using System.Text.Json;
using Undertone.Core;
using Xunit;

namespace Undertone.Tests;

public class SoloistStateTests
{
    private static SoloistChange Apply(SoloistState s, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return s.Apply(doc.RootElement);
    }

    private const string Item = """
        {"uri":"spotify:track:abc","entity_type":"track",
         "decorations":{"identity":{"name":"Song"},"playback":{"duration_ms":200000}}}
        """;

    [Fact]
    public void AuthState_SetsLoggedIn()
    {
        var s = new SoloistState();
        var c = Apply(s, """{"type":"auth_state","logged_in":true,"is_active":false,"device_name":"Undertone"}""");
        Assert.Equal(SoloistChange.Auth, c);
        Assert.True(s.LoggedIn);
        Assert.False(s.IsActive);
        Assert.Equal("Undertone", s.DeviceName);
    }

    [Fact]
    public void PlaybackState_FullSnapshot()
    {
        var s = new SoloistState();
        var c = Apply(s, $$"""
            {"type":"playback_state","status":"playing","item":{{Item}},
             "position":{"position_ms":45000,"timestamp_ms":1000000,"speed":1.0},"volume":50,"is_active":true}
            """);
        Assert.True(c.HasFlag(SoloistChange.Track));
        Assert.True(c.HasFlag(SoloistChange.Volume));
        Assert.True(s.LoggedIn);
        Assert.True(s.IsPlaying);
        Assert.Equal("spotify:track:abc", s.ItemUri);
        Assert.Equal(200000, s.DurationMs);
        Assert.Equal(50, s.Volume);
        Assert.Equal(45000, s.PositionAt(1000000));
        Assert.Equal(47000, s.PositionAt(1002000));
    }

    [Fact]
    public void Position_StopsWhenPausedAndClampsToDuration()
    {
        var s = new SoloistState();
        Apply(s, $$"""{"type":"track_changed","item":{{Item}}}""");
        Apply(s, """{"type":"position_sync","position":{"position_ms":199000,"timestamp_ms":1000,"speed":1.0}}""");
        Assert.Equal(200000, s.PositionAt(10000)); // не дальше длины трека
        Apply(s, """{"type":"position_sync","position":{"position_ms":30000,"timestamp_ms":1000,"speed":0.0}}""");
        Assert.Equal(30000, s.PositionAt(50000));
    }

    [Fact]
    public void PlaybackChanged_PauseStopsClock()
    {
        var s = new SoloistState();
        Apply(s, """{"type":"position_sync","position":{"position_ms":1000,"timestamp_ms":0,"speed":1.0}}""");
        Apply(s, """{"type":"playback_changed","status":"paused"}""");
        Assert.Equal("paused", s.Status);
        Assert.Equal(1000, s.PositionAt(5000));
    }

    [Fact]
    public void TrackChanged_AlwaysReportsTrack()
    {
        var s = new SoloistState();
        Assert.Equal(SoloistChange.Track, Apply(s, $$"""{"type":"track_changed","item":{{Item}}}"""));
        // тот же трек ещё раз (повтор) — всё равно событие смены трека
        Assert.Equal(SoloistChange.Track, Apply(s, $$"""{"type":"track_changed","item":{{Item}}}"""));
    }

    [Fact]
    public void ErrorAndUnknownEvents()
    {
        var s = new SoloistState();
        Assert.Equal(SoloistChange.Error, Apply(s, """{"type":"error","message":"not logged in"}"""));
        Assert.Equal("not logged in", s.LastError);
        Assert.Equal(SoloistChange.None, Apply(s, """{"type":"command_result","command":"play"}"""));
        Assert.Equal(SoloistChange.None, Apply(s, """[1,2]"""));
    }
}
