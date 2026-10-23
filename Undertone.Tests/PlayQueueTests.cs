using System.Linq;
using Undertone.Core;

namespace Undertone.Tests;

public class PlayQueueTests
{
    [Fact]
    public void Set_StartsAtRequestedTrack()
    {
        var q = new PlayQueue();
        q.Set(TestData.Tracks(5), 2, QueueSource.Liked, shuffle: false);
        Assert.Equal("3", q.Current!.Id);
        Assert.Equal(2, q.Remaining);
    }

    [Fact]
    public void MoveNext_StopsAtEnd_WhenRepeatOff()
    {
        var q = new PlayQueue();
        q.Set(TestData.Tracks(2), 1, QueueSource.Liked, shuffle: false);
        Assert.Null(q.MoveNext(auto: true));
    }

    [Fact]
    public void RepeatOne_RepeatsOnlyWhenTrackEndedByItself()
    {
        var q = new PlayQueue { Repeat = RepeatMode.One };
        q.Set(TestData.Tracks(3), 0, QueueSource.Liked, shuffle: false);
        Assert.Equal("1", q.MoveNext(auto: true)!.Id);
        Assert.Equal("2", q.MoveNext(auto: false)!.Id);
    }

    [Fact]
    public void RepeatAll_WrapsToStart()
    {
        var q = new PlayQueue();
        q.Set(TestData.Tracks(3), 2, QueueSource.Liked, shuffle: false);
        q.Repeat = RepeatMode.All;
        Assert.Equal("1", q.PeekNext()!.Id);
        Assert.Equal("1", q.MoveNext(auto: true)!.Id);
    }

    [Fact]
    public void RepeatAllWithShuffle_PeekMatchesNextRound()
    {
        // раньше PeekNext возвращал null на стыке кругов — и предзагрузка не работала
        var q = new PlayQueue();
        q.Set(TestData.Tracks(10), 0, QueueSource.Liked, shuffle: true);
        q.Repeat = RepeatMode.All;
        for (int i = 0; i < 9; i++) q.MoveNext(auto: false);
        var peeked = q.PeekNext();
        Assert.NotNull(peeked);
        Assert.Same(peeked, q.MoveNext(auto: true));
    }

    [Fact]
    public void Shuffle_KeepsCurrentTrackFirst()
    {
        var q = new PlayQueue();
        q.Set(TestData.Tracks(20), 7, QueueSource.Liked, shuffle: true);
        Assert.Equal("8", q.Current!.Id);
        Assert.Equal(19, q.Upcoming(100).Count);
    }

    [Fact]
    public void Wave_NeverShufflesOrRepeatsList()
    {
        var q = new PlayQueue { Repeat = RepeatMode.All };
        q.Set(TestData.Tracks(2), 1, QueueSource.Wave, shuffle: true);
        Assert.False(q.Shuffle);
        Assert.Null(q.MoveNext(auto: true));
    }

    [Fact]
    public void InsertNext_PlaysRightAfterCurrent()
    {
        var q = new PlayQueue();
        q.Set(TestData.Tracks(3), 0, QueueSource.Liked, shuffle: false);
        q.InsertNext(TestData.Track("x"));
        Assert.Equal("x", q.MoveNext(auto: false)!.Id);
        Assert.Equal("2", q.MoveNext(auto: false)!.Id);
    }

    [Fact]
    public void SkipAhead_JumpsAndRejectsOutOfRange()
    {
        var q = new PlayQueue();
        q.Set(TestData.Tracks(5), 0, QueueSource.Liked, shuffle: false);
        Assert.Null(q.SkipAhead(10));
        Assert.Equal("4", q.SkipAhead(3)!.Id);
    }

    [Fact]
    public void PeekAhead_ReturnsSeveralAndWraps()
    {
        var q = new PlayQueue();
        q.Set(TestData.Tracks(4), 2, QueueSource.Liked, shuffle: false);
        q.Repeat = RepeatMode.All;
        Assert.Equal(new[] { "4", "1", "2" }, q.PeekAhead(3).Select(t => t.Id));
    }

    [Fact]
    public void MoveAndRemoveUpcoming()
    {
        var q = new PlayQueue();
        q.Set(TestData.Tracks(5), 0, QueueSource.Liked, shuffle: false);
        Assert.True(q.MoveUpcoming(3, 0));          // 5 встаёт сразу после текущего
        Assert.Equal(new[] { "5", "2", "3", "4" }, q.Upcoming(10).Select(t => t.Id));
        Assert.True(q.RemoveUpcomingAt(1));         // убираем 2
        Assert.Equal(new[] { "5", "3", "4" }, q.Upcoming(10).Select(t => t.Id));
        q.ClearUpcoming();
        Assert.Empty(q.Upcoming(10));
        Assert.Equal("1", q.Current!.Id);
    }

    [Fact]
    public void Snapshot_CentersOnCurrent()
    {
        var q = new PlayQueue();
        q.Set(TestData.Tracks(10), 5, QueueSource.Liked, shuffle: false);
        var (tracks, index) = q.Snapshot(before: 2, after: 3);
        Assert.Equal(new[] { "4", "5", "6", "7", "8", "9" }, tracks.Select(t => t.Id));
        Assert.Equal(2, index);
    }
}
