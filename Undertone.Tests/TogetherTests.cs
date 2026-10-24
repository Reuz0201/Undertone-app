using System.Linq;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Undertone.Core;

namespace Undertone.Tests;

public class TogetherTests
{
    [Fact]
    public void ExpectedPosition_AdvancesOnlyWhilePlaying()
    {
        var playing = new RoomState { Playing = true, Position = 10, At = 1_000_000 };
        Assert.Equal(15, playing.ExpectedPosition(1_005_000).TotalSeconds, 3);
        var paused = new RoomState { Playing = false, Position = 10, At = 1_000_000 };
        Assert.Equal(10, paused.ExpectedPosition(1_005_000).TotalSeconds, 3);
        // часы чуть отстают — назад не уходим
        Assert.Equal(10, playing.ExpectedPosition(999_000).TotalSeconds, 3);
    }

    [Fact]
    public void IsNewer_UsesSeqThenMember()
    {
        var a = new RoomState { Seq = 5, By = "a" };
        var b = new RoomState { Seq = 5, By = "b" };
        var c = new RoomState { Seq = 6, By = "a" };
        Assert.True(RoomState.IsNewer(a, null));
        Assert.True(RoomState.IsNewer(b, a));
        Assert.False(RoomState.IsNewer(a, b));
        Assert.True(RoomState.IsNewer(c, b));
        Assert.False(RoomState.IsNewer(a, a));
    }

    [Fact]
    public void RoomState_RoundTripsThroughEncryption()
    {
        var key = FriendsFeed.DeriveKey("AAAABBBBCCCCDDDD");
        var state = new RoomState
        {
            Seq = 42, By = "m1", ByName = "Федуня", Active = true, Playing = true, Position = 12.5, At = 1000,
            Track = new YaTrack { Id = "1", AlbumId = "2", Title = "Песня", Artists = new[] { "Артист" }, DurationMs = 180000 },
            Queue = new List<YaTrack> { new() { Id = "3", Title = "Дальше" } },
        };
        var data = FriendsFeed.Encrypt(key, JsonSerializer.SerializeToUtf8Bytes(state));
        var back = JsonSerializer.Deserialize<RoomState>(FriendsFeed.Decrypt(key, data)!)!;
        Assert.Equal("Федуня", back.ByName);
        Assert.Equal("Песня", back.Track!.Title);
        Assert.Equal(TimeSpan.FromMinutes(3), back.Track.Duration);
        Assert.Equal("3", Assert.Single(back.Queue).Id);
        Assert.Equal(12.5, back.Position);
    }

    [Fact]
    public void NtpStamp_ConvertsToUnixMs()
    {
        // 2208988800 c + половина секунды → 500 мс после 1970-01-01
        var stamp = new byte[] { 0x83, 0xAA, 0x7E, 0x80, 0x80, 0, 0, 0 };
        Assert.Equal(500, TimeSync.NtpToUnixMs(stamp));
    }

    [Fact]
    public void Queue_ReplaceUpcomingKeepsCurrent()
    {
        var q = new PlayQueue();
        q.Set(new[] { T("a"), T("b"), T("c") }, 0, QueueSource.Collection, shuffle: false);
        q.ReplaceUpcoming(new[] { T("x"), T("y") });
        Assert.Equal("a", q.Current!.Id);
        Assert.Equal(new[] { "x", "y" }, q.Upcoming(10).Select(t => t.Id));
        Assert.Equal("x", q.MoveNext(auto: false)!.Id);
    }

    private static YaTrack T(string id) => new() { Id = id, Title = id };
}
