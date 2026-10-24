using System.Collections.Generic;
using System.Linq;
using Undertone.Core;

namespace Undertone.Tests;

public class CompanyMixTests
{
    private static List<string> Ids(string prefix, int n) => Enumerable.Range(1, n).Select(i => $"{prefix}{i}:9").ToList();

    [Fact]
    public void SharedTracksComeFirstAndFriendsContribute()
    {
        var mine = Ids("m", 40);
        mine.Add("shared1:9");
        var friends = new Dictionary<string, List<string>>
        {
            ["a"] = Ids("a", 40).Append("shared1:9").ToList(),
            ["b"] = Ids("b", 40),
        };
        var mix = CompanyMix.Build(mine, friends, 30, seed: 1);
        Assert.Equal(30, mix.Ids.Count);
        Assert.Contains("shared1:9", mix.Ids);
        Assert.Equal(1, mix.Shared);
        Assert.Contains(mix.Ids, id => id.StartsWith("a"));
        Assert.Contains(mix.Ids, id => id.StartsWith("b"));
        Assert.Equal(mix.Ids.Count, mix.Ids.Distinct().Count());
    }

    [Fact]
    public void SameDaySameMix()
    {
        var mine = Ids("m", 20);
        var friends = new Dictionary<string, List<string>> { ["a"] = Ids("a", 20) };
        var x = CompanyMix.Build(mine, friends, 15, 2026297);
        var y = CompanyMix.Build(mine, friends, 15, 2026297);
        Assert.Equal(x.Ids, y.Ids);
    }

    [Fact]
    public void SmallLibrariesDoNotCrash()
    {
        var mix = CompanyMix.Build(new List<string>(), new Dictionary<string, List<string>> { ["a"] = Ids("a", 3) }, 50, 1);
        Assert.Equal(3, mix.Ids.Count);
    }
}
