using System.Linq;
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

    [Fact]
    public void ContextChanged_SetsContextUri()
    {
        var s = new SoloistState();
        var c = Apply(s, """{"type":"context_changed","context":{"uri":"spotify:playlist:pl1","entity_type":"playlist"}}""");
        Assert.Equal(SoloistChange.Context, c);
        Assert.Equal("spotify:playlist:pl1", s.ContextUri);
    }

    [Fact]
    public void ContextTracks_CurrentPlusUpcomingFromContextOnly()
    {
        var s = new SoloistState();
        Apply(s, """
            {"type":"track_changed","item":{"uri":"spotify:track:t1","entity_type":"track",
              "decorations":{"identity":{"name":"First"},
                "creators":[{"uri":"spotify:artist:a1","decorations":{"identity":{"name":"Artist One"}}}],
                "parent":{"entity":{"uri":"spotify:album:al1","decorations":{"identity":{"name":"Album"}}}},
                "visual_identity":{"cover":[{"url":"https://i.scdn.co/image/small","size":"small"},
                                            {"url":"spotify:image:big","size":"large"}]},
                "playback":{"duration_ms":180000}}}}
            """);
        using var queue = JsonDocument.Parse("""
            {"type":"queue_changed","previous":[],"upcoming":[
              {"uid":"u2","source":"context","item":{"uri":"spotify:track:t2","entity_type":"track","decorations":{"identity":{"name":"Second"}}}},
              {"uid":"u3","source":"queue","item":{"uri":"spotify:track:q1","entity_type":"track"}},
              {"uid":"u4","source":"context","item":{"uri":"spotify:episode:e1","entity_type":"episode"}},
              {"uid":"u5","source":"autoplay","item":{"uri":"spotify:track:auto","entity_type":"track"}},
              {"uid":"u6","source":"context","item":{"uri":"spotify:track:t1","entity_type":"track"}}
            ]}
            """);

        var tracks = SoloistState.ContextTracks(s.CurrentItem, queue.RootElement);

        Assert.Equal(new[] { "sp-t1", "sp-t2" }, tracks.Select(t => t.Id));
        var first = tracks[0];
        Assert.Equal("First", first.Title);
        Assert.Equal(new[] { "Artist One" }, first.Artists);
        Assert.Equal("sp-a1", first.ArtistRefs.Single().Id);
        Assert.Equal("sp-al1", first.AlbumId);
        Assert.Equal("Album", first.Album);
        Assert.Equal(180000, first.DurationMs);
        Assert.Equal("https://i.scdn.co/image/big", first.CoverUri);
        Assert.Equal("Second", tracks[1].Title);
    }
}
