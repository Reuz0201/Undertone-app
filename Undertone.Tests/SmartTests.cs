using System;
using System.Collections.Generic;
using System.Linq;
using Undertone.Core;
using Undertone.UI;

namespace Undertone.Tests;

public class SmartTests
{
    private static YaTrack T(string id, string artist = "A") => new() { Id = id, Title = id, Artists = new[] { artist } };

    [Fact]
    public void ForgottenLikes_SkipRecentlyPlayed()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0);
        var liked = new List<YaTrack> { T("1"), T("2"), T("3") };
        var stats = new PlayStats();
        stats.Count(liked[0], now.AddDays(-2));   // недавно
        stats.Count(liked[1], now.AddDays(-60));  // давно
        var rule = new SmartPlaylist { Source = SmartSource.Liked, NotPlayedDays = 30, Sort = SmartSort.Newest };
        var result = SmartEngine.Evaluate(rule, liked, new List<YaTrack>(), stats, now, 1).Select(t => t.Id).ToList();
        Assert.Equal(new[] { "2", "3" }, result);
    }

    [Fact]
    public void OnRepeat_NeedsPlaysAndSortsByCount()
    {
        var now = DateTime.Now;
        var stats = new PlayStats();
        var a = T("a"); var b = T("b"); var c = T("c");
        for (int i = 0; i < 5; i++) stats.Count(a, now.AddHours(-i));
        for (int i = 0; i < 3; i++) stats.Count(b, now.AddHours(-i));
        stats.Count(c, now);
        var rule = new SmartPlaylist { Source = SmartSource.Played, MinPlays = 3, PlayedWithinDays = 14, Sort = SmartSort.MostPlayed };
        var result = SmartEngine.Evaluate(rule, new List<YaTrack>(), new List<YaTrack>(), stats, now, 1).Select(t => t.Id).ToList();
        Assert.Equal(new[] { "a", "b" }, result);
    }

    [Fact]
    public void Night_UsesHourOfMostPlays()
    {
        var stats = new PlayStats();
        var night = T("n"); var day = T("d");
        stats.Count(night, new DateTime(2026, 1, 1, 2, 0, 0));
        stats.Count(night, new DateTime(2026, 1, 2, 3, 0, 0));
        stats.Count(day, new DateTime(2026, 1, 1, 14, 0, 0));
        var rule = new SmartPlaylist { Source = SmartSource.Played, DayPart = DayPart.Night };
        var result = SmartEngine.Evaluate(rule, new List<YaTrack>(), new List<YaTrack>(), stats, DateTime.Now, 1);
        Assert.Equal("n", Assert.Single(result).Id);
    }

    [Fact]
    public void Translator_ParsesAndAlignsLines()
    {
        var json = "[[[\"Привет\\n\",\"Hello\\n\",null,null,10],[\"мир\",\"world\",null,null,10]],null,\"en\"]";
        var r = Translator.Parse(json, 3);
        Assert.Equal("en", r.SourceLanguage);
        Assert.Equal(new[] { "Привет", "мир", "" }, r.Lines);
    }
}
