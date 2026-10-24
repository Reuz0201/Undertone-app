using Undertone.Core;

namespace Undertone.Tests;

public class LocalLibraryTests
{
    [Fact]
    public void IdIsStableAndCaseInsensitive()
    {
        var a = LocalLibrary.IdFor(@"C:\Music\Song.mp3");
        var b = LocalLibrary.IdFor(@"c:\music\SONG.mp3");
        Assert.Equal(a, b);
        Assert.StartsWith(LocalLibrary.IdPrefix, a);
        Assert.True(LocalLibrary.IsLocal(new YaTrack { Id = a }));
        Assert.False(LocalLibrary.IsLocal(new YaTrack { Id = "12345" }));
    }

    [Fact]
    public void LocalCoverUrlIsPassedThrough()
    {
        var t = new YaTrack { Id = "local-1", CoverUri = "file:///C:/covers/1.jpg" };
        Assert.Equal("file:///C:/covers/1.jpg", t.CoverUrl(300));
        var y = new YaTrack { Id = "1", CoverUri = "avatars.yandex.net/get/%%" };
        Assert.Equal("https://avatars.yandex.net/get/300x300", y.CoverUrl(300));
    }
}
