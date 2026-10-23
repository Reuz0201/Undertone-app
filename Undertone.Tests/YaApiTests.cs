using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Undertone.Core;

namespace Undertone.Tests;

public class YaApiTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Sign_MatchesKnownValue()
    {
        // MD5("XGRlBW9FXlekgbPrRHuSiA" + path без первого "/" + s) — формат подписи ссылок Яндекса
        var expected = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("XGRlBW9FXlekgbPrRHuSiA" + "get-mp3/abc" + "salt"))).ToLowerInvariant();
        Assert.Equal(expected, YaApi.Sign("/get-mp3/abc", "salt"));
    }

    [Fact]
    public void ParseTrack_ReadsAlbumArtistsAndLyricsFlags()
    {
        var t = YaApi.ParseTrack(Json("""
        {
          "id": 123, "title": "Песня", "version": "Remix", "durationMs": 201000, "available": true,
          "artists": [ { "id": 1, "name": "А" }, { "id": 2, "name": "Б" } ],
          "albums": [ { "id": 77, "title": "Альбом", "coverUri": "avatars.yandex.net/x/%%" } ],
          "lyricsInfo": { "hasAvailableSyncLyrics": true, "hasAvailableTextLyrics": true }
        }
        """));
        Assert.Equal("123", t.Id);
        Assert.Equal("77", t.AlbumId);
        Assert.Equal("Песня (Remix)", t.FullTitle);
        Assert.Equal("А, Б", t.ArtistsText);
        Assert.Equal("123:77", t.LikeId);
        Assert.Equal("https://avatars.yandex.net/x/200x200", t.CoverUrl(200));
        Assert.True(t.HasSyncLyrics);
        Assert.True(t.HasLyrics);
    }

    [Fact]
    public void ParseTrack_UnavailableFlag() =>
        Assert.False(YaApi.ParseTrack(Json("""{ "id": "1", "available": false }""")).Available);

    [Fact]
    public void ParsePlaylist_ReadsOwnerFromNestedObject()
    {
        var p = YaApi.ParsePlaylist(Json("""
        { "kind": 1003, "title": "Мой", "trackCount": 2, "revision": 5,
          "owner": { "uid": 42, "login": "vasya", "name": "Вася" } }
        """));
        Assert.Equal("1003", p.Kind);
        Assert.Equal(42, p.OwnerUid);
        Assert.Equal("Вася", p.OwnerName);
        Assert.Equal(5, p.Revision);
    }

    [Fact]
    public async Task GetPlaylist_MixedResponse_KeepsOrder()
    {
        // регрессия P0: часть треков пришла целиком, часть — только id; раньше «только id» терялись
        var handler = new FakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/playlists/5")) return """
                { "result": { "kind": 5, "title": "P", "revision": 1, "tracks": [
                    { "id": "1", "track": { "id": "1", "title": "один" } },
                    { "id": "2", "albumId": "20" },
                    { "id": "3", "track": { "id": "3", "title": "три" } }
                ] } }
                """;
            if (path == "/tracks") return """{ "result": [ { "id": "2", "title": "два" } ] }""";
            return """{ "result": {} }""";
        });
        using var api = new YaApi(handler);
        var (_, tracks) = await api.GetPlaylistAsync(5L, "5");
        Assert.Equal(new[] { "1", "2", "3" }, tracks.Select(t => t.Id));
        Assert.Equal("два", tracks[1].Title);
    }

    [Fact]
    public async Task RemoveTrack_FindsPositionById()
    {
        // на экране трек «3» второй, а на сервере — третий: удалять надо позицию 2, а не 1
        string? diff = null;
        var handler = new FakeHandler(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get) return """
                { "result": { "kind": 5, "revision": 9, "tracks": [ { "id": "1" }, { "id": "9" }, { "id": "3" } ] } }
                """;
            var form = req.Content!.ReadAsStringAsync().Result;
            diff = Uri.UnescapeDataString(form.Split('&').First(p => p.StartsWith("diff=")).Substring(5).Replace('+', ' '));
            return """{ "result": { "kind": 5, "revision": 10 } }""";
        });
        using var api = new YaApi(handler);
        var updated = await api.RemoveTrackFromPlaylistAsync("5", "3", hintIndex: 1);
        Assert.Equal(10, updated.Revision);
        Assert.Contains("\"from\":2", diff);
        Assert.Contains("\"to\":3", diff);
    }

    [Fact]
    public async Task RemoveTrack_MissingTrack_Throws()
    {
        var handler = new FakeHandler(_ => """{ "result": { "kind": 5, "revision": 1, "tracks": [ { "id": "1" } ] } }""");
        using var api = new YaApi(handler);
        await Assert.ThrowsAsync<YaApiException>(() => api.RemoveTrackFromPlaylistAsync("5", "3", 0));
    }

    [Fact]
    public async Task GetTracks_KeepsRequestedOrder()
    {
        var handler = new FakeHandler(_ => """{ "result": [ { "id": "b" }, { "id": "a" } ] }""");
        using var api = new YaApi(handler);
        var tracks = await api.GetTracksAsync(new[] { "a:1", "b:2", "zzz" });
        Assert.Equal(new[] { "a", "b" }, tracks.Select(t => t.Id));
    }
}
