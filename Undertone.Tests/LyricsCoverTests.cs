using Undertone.Core;
using Undertone.UI;

namespace Undertone.Tests;

public class LrcLibTests
{
    [Theory]
    [InlineData("U Do (feat. Siadou)", "U Do")]
    [InlineData("One Kiss (with Dua Lipa)", "One Kiss")]
    [InlineData("Pain 1993 (with Playboi Carti)", "Pain 1993")]
    [InlineData("Song [ft. Someone]", "Song")]
    [InlineData("Yesterday - Remastered 2009", "Yesterday")]
    [InlineData("Track (Radio Edit)", "Track")]
    [InlineData("Automotivo Galático (Slowed)", "Automotivo Galático (Slowed)")]
    [InlineData("Demons Around (Sped Up)", "Demons Around (Sped Up)")]
    public void CleanTitle_DropsFeatAndRemaster_KeepsVersions(string title, string expected) =>
        Assert.Equal(expected, LrcLib.CleanTitle(title));

    [Fact]
    public void Pick_PrefersSynced_WithinDuration()
    {
        const string json = """
            [
              {"duration": 300, "syncedLyrics": "[00:01.00] wrong length", "plainLyrics": "x"},
              {"duration": 214, "syncedLyrics": null, "plainLyrics": "plain only"},
              {"duration": 216, "syncedLyrics": "[00:01.00] synced", "plainLyrics": "y"}
            ]
            """;
        var found = LrcLib.Pick(json, 215);
        Assert.NotNull(found);
        Assert.True(found!.Synced);
        Assert.Equal("[00:01.00] synced", found.Text);
    }

    [Fact]
    public void Pick_FallsBackToPlain_SkipsInstrumental()
    {
        const string json = """
            [
              {"duration": 200, "instrumental": true, "syncedLyrics": null, "plainLyrics": null},
              {"duration": 201, "syncedLyrics": "", "plainLyrics": "words"}
            ]
            """;
        var found = LrcLib.Pick(json, 200);
        Assert.NotNull(found);
        Assert.False(found!.Synced);
        Assert.Equal("words", found.Text);
    }

    [Fact]
    public void Pick_SingleObjectAndEmpty()
    {
        Assert.Equal("[00:02.00] a", LrcLib.Pick("""{"duration": 100, "syncedLyrics": "[00:02.00] a"}""", 100)!.Text);
        Assert.Null(LrcLib.Pick("[]", 100));
        Assert.Null(LrcLib.Pick(null, 100));
    }
}

public class CoverUrlTests
{
    private const string Spotify300 = "https://i.scdn.co/image/ab67616d00001e0295237fd2f347fbaf96013913";

    [Theory]
    [InlineData(64, "https://i.scdn.co/image/ab67616d0000485195237fd2f347fbaf96013913")]
    [InlineData(200, "https://i.scdn.co/image/ab67616d00001e0295237fd2f347fbaf96013913")]
    [InlineData(600, "https://i.scdn.co/image/ab67616d0000b27395237fd2f347fbaf96013913")]
    public void Spotify_PicksNearestSize(int size, string expected) =>
        Assert.Equal(expected, CoverUris.Resolve(Spotify300, size));

    [Fact]
    public void Spotify_UnknownImageIdLeftAsIs()
    {
        const string other = "https://i.scdn.co/image/ab6761610000e5eb0123456789";
        Assert.Equal(other, CoverUris.Resolve(other, 600));
    }

    [Fact]
    public void Distinct_GivesEachSizeItsOwnAddress()
    {
        Assert.Equal(Spotify300 + "?ut=116", Covers.Distinct(Spotify300, 116));
        Assert.Equal(Spotify300 + "?ut=600", Covers.Distinct(Spotify300, 600));
        Assert.Equal("https://x/y?a=1&ut=200", Covers.Distinct("https://x/y?a=1", 200));
        Assert.Equal("file:///C:/m/cover.jpg", Covers.Distinct("file:///C:/m/cover.jpg", 200));
    }
}
