using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Undertone.Core;

namespace Undertone.Tests;

public class SpotifyParsingTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Ids_RoundTrip()
    {
        Assert.Equal("sp-4uLU6hMCjMI75M1A2tKUQC", SpotifyApi.ToId("spotify:track:4uLU6hMCjMI75M1A2tKUQC"));
        Assert.Equal("sp-abc", SpotifyApi.ToId("abc"));
        Assert.Equal("sp-abc", SpotifyApi.ToId("sp-abc"));
        Assert.Equal("abc", SpotifyApi.Raw("sp-abc"));
        Assert.Equal("abc", SpotifyApi.Raw("sp-abc:sp-album"));
        Assert.Equal("spotify:track:abc", SpotifyApi.ToUri("sp-abc"));
        Assert.Equal("spotify:playlist:p1", SpotifyApi.ToUri("sp-p1", "playlist"));
        Assert.True(SpotifyApi.IsSpId("sp-abc"));
        Assert.False(SpotifyApi.IsSpId("sc-1"));
    }

    [Fact]
    public void Uid_IsStableNegativeAndApartFromOthers()
    {
        var a = SpotifyApi.UidFor("31abcxyz");
        Assert.Equal(a, SpotifyApi.UidFor("31abcxyz"));
        Assert.NotEqual(a, SpotifyApi.UidFor("31abcxyZ"));
        Assert.True(a <= -1_000_000_000_000_000L);
    }

    [Fact]
    public void ParseTrack_ArtistsAlbumCoverAndLink()
    {
        var t = SpotifyApi.ParseTrack(Json("""
            {
              "id": "4uLU6hMCjMI75M1A2tKUQC", "name": "Never Gonna", "duration_ms": 213573, "type": "track",
              "artists": [ { "id": "0gxyHStUsqpMadRV0Di1Qt", "name": "Rick" }, { "id": "x2", "name": "Guest" } ],
              "album": { "id": "6N9PS4QXF1D0OWPk0Sxtb4", "name": "Whenever",
                         "images": [ { "url": "https://i.scdn.co/640", "width": 640 },
                                     { "url": "https://i.scdn.co/300", "width": 300 },
                                     { "url": "https://i.scdn.co/64", "width": 64 } ] },
              "external_urls": { "spotify": "https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC" }
            }
            """));
        Assert.Equal("sp-4uLU6hMCjMI75M1A2tKUQC", t.Id);
        Assert.Equal("sp-6N9PS4QXF1D0OWPk0Sxtb4", t.AlbumId);
        Assert.Equal(new[] { "Rick", "Guest" }, t.Artists);
        Assert.Equal("sp-0gxyHStUsqpMadRV0Di1Qt", t.ArtistRefs[0].Id);
        Assert.Equal("Whenever", t.Album);
        Assert.Equal("https://i.scdn.co/300", t.CoverUrl(200));
        Assert.True(t.Available);
        Assert.Equal("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC", t.WebUrl);
        Assert.Equal(MusicServiceKind.Spotify, MusicServices.Of(t));
        Assert.False(YaApi.IsYandexId(t.Id));
    }

    [Fact]
    public void ParseTrack_LocalOrUnplayableIsUnavailable_AlbumFromParent()
    {
        var album = Json("""{ "id": "al", "name": "Parent", "images": [ { "url": "https://i/1", "width": 300 } ] }""");
        var t = SpotifyApi.ParseTrack(Json("""{ "id": "t1", "name": "x", "is_playable": false, "artists": [] }"""), album);
        Assert.False(t.Available);
        Assert.Equal("Parent", t.Album);
        Assert.Equal("https://i/1", t.CoverUri);
        Assert.False(SpotifyApi.ParseTrack(Json("""{ "id": "t2", "name": "y", "is_local": true }""")).Available);
    }

    [Fact]
    public void UnwrapItem_TrackOrRenamedItem_SkipsEpisodes()
    {
        Assert.Equal("a", SpotifyApi.UnwrapItem(Json("""{ "track": { "id": "a", "type": "track" } }""")).GetProperty("id").GetString());
        Assert.Equal("b", SpotifyApi.UnwrapItem(Json("""{ "item": { "id": "b", "type": "track" } }""")).GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Undefined, SpotifyApi.UnwrapItem(Json("""{ "item": { "id": "e", "type": "episode" } }""")).ValueKind);
        Assert.Equal(JsonValueKind.Undefined, SpotifyApi.UnwrapItem(Json("""{ "track": null }""")).ValueKind);
    }

    [Fact]
    public void ParsePlaylist_CountFromItemsOrTracks_OwnerUid()
    {
        var p = SpotifyApi.ParsePlaylist(Json("""
            { "id": "37i9", "name": "Mix", "owner": { "id": "me1", "display_name": "Me" }, "items": { "total": 12 },
              "images": null }
            """));
        Assert.Equal("sp-37i9", p.Kind);
        Assert.Equal(12, p.TrackCount);
        Assert.Equal(SpotifyApi.UidFor("me1"), p.OwnerUid);
        Assert.Equal("Me", p.OwnerName);
        Assert.Null(p.CoverUri);
        Assert.Equal(7, SpotifyApi.ParsePlaylist(Json("""{ "id": "x", "tracks": { "total": 7 } }""")).TrackCount);
    }

    [Fact]
    public void ParsePlayerState_AndDevices()
    {
        var s = SpotifyApi.ParsePlayerState(Json("""
            { "device": { "id": "d1" }, "is_playing": true, "progress_ms": 1500, "item": { "id": "t9", "duration_ms": 200000 } }
            """))!;
        Assert.Equal("d1", s.DeviceId);
        Assert.Equal("sp-t9", s.TrackId);
        Assert.True(s.IsPlaying);
        Assert.Equal(1500, s.ProgressMs);
        Assert.Equal(200000, s.DurationMs);
        Assert.Null(SpotifyApi.ParsePlayerState(default));

        var d = SpotifyApi.ParseDevice(Json("""{ "id": "x", "name": "PC", "type": "Computer", "is_active": true, "is_restricted": false }"""));
        Assert.Equal(new SpotifyDevice("x", "PC", "Computer", true, false), d);
    }

    [Fact]
    public void Errors_AreFriendly()
    {
        Assert.Equal("Invalid access token", SpotifyApi.ErrorMessage(Json("""{ "error": { "status": 401, "message": "Invalid access token" } }""")));
        Assert.Contains("Premium", SpotifyApi.Friendly("Player command failed: Premium required", HttpStatusCode.Forbidden));
        Assert.Contains("User Management", SpotifyApi.Friendly("User not registered in the Developer Dashboard", HttpStatusCode.Forbidden));
        Assert.Equal("HTTP 500", SpotifyApi.Friendly(null, HttpStatusCode.InternalServerError));
    }

    [Fact]
    public void Services_ThreeWay()
    {
        Assert.Equal(3, MusicServices.All.Length);
        foreach (var k in MusicServices.All) Assert.Equal(k, MusicServices.Parse(k.Id()));
        Assert.Equal(MusicServiceKind.Yandex, MusicServices.Parse(null));
        Assert.Equal(new[] { MusicServiceKind.Yandex, MusicServiceKind.SoundCloud }, MusicServiceKind.Spotify.Others().ToArray());
        Assert.Equal("Spotify", MusicServiceKind.Spotify.Title());
    }

    [Fact]
    public void Config_ClientIdOnly_DefaultRedirect()
    {
        var env = new Dictionary<string, string> { ["spotify_client_id"] = " abc " };
        var cfg = SpotifyConfig.Resolve(new Func<string, string?>[] { k => env.GetValueOrDefault(k) })!;
        Assert.Equal("abc", cfg.ClientId);
        Assert.Equal(SpotifyConfig.DefaultRedirectUri, cfg.RedirectUri);
        Assert.Null(SpotifyConfig.Resolve(new Func<string, string?>[] { _ => null }));
    }
}

public class SpotifyApiTests
{
    [Fact]
    public async Task Account_LikesAndTracks_UseCacheAndLibraryEndpoint()
    {
        var requests = new List<string>();
        using var api = new SpotifyApi(new FakeHandler(req =>
        {
            requests.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/me") return """{ "id": "me1", "display_name": "Ник", "images": [ { "url": "https://a/1", "width": 300 } ] }""";
            if (path == "/v1/me/tracks" && !req.RequestUri.Query.Contains("offset"))
                return """{ "items": [ { "track": { "id": "t1", "name": "One", "type": "track" } } ], "next": "https://api.spotify.com/v1/me/tracks?offset=1&limit=50" }""";
            if (path == "/v1/me/tracks")
                return """{ "items": [ { "track": { "id": "t2", "name": "Two", "type": "track" } } ], "next": null }""";
            if (path == "/v1/tracks/t3") return """{ "id": "t3", "name": "Three" }""";
            return "";
        }));

        var acc = await api.GetAccountAsync();
        Assert.Equal(SpotifyApi.UidFor("me1"), acc.Uid);
        Assert.Equal("Ник", acc.DisplayName);

        var (_, ids) = await api.GetLikedIdsAsync();
        Assert.Equal(new[] { "sp-t1", "sp-t2" }, ids);

        var before = requests.Count;
        var tracks = await api.GetTracksAsync(new[] { "sp-t2", "123", "sp-t3", "sp-t1" });
        Assert.Equal(new[] { "Two", "Three", "One" }, tracks.Select(t => t.Title));
        Assert.Equal(1, requests.Count - before); // только неизвестный t3

        await api.SetLikeAsync(tracks[0], true);
        Assert.Equal("PUT /v1/me/library?uris=spotify%3Atrack%3At2", requests[^1]);
        await api.SetLikeAsync(tracks[0], false);
        Assert.StartsWith("DELETE /v1/me/library", requests[^1]);
    }

    [Fact]
    public async Task Downloads_AreNotSupported()
    {
        using var api = new SpotifyApi(new FakeHandler(_ => "{}"));
        IMusicService service = api;
        Assert.False(service.SupportsDownload);
        Assert.True(((IMusicService)new ScApi(new FakeHandler(_ => "{}"))).SupportsDownload);
        await Assert.ThrowsAsync<NotSupportedException>(() => api.ResolveAudioAsync(TestData.Track("sp-1"), false));
        Assert.Empty(await api.GetSimilarTracksAsync("sp-1"));
    }

    [Fact]
    public async Task Playlist_ItemsEndpoint_AndPlayCommand()
    {
        var requests = new List<(string Line, string Body)>();
        using var api = new SpotifyApi(new FakeHandler(req =>
        {
            var body = req.Content?.ReadAsStringAsync().Result ?? "";
            requests.Add(($"{req.Method} {req.RequestUri!.AbsolutePath}", body));
            return req.RequestUri!.AbsolutePath switch
            {
                "/v1/playlists/p1" => """{ "id": "p1", "name": "Mine", "owner": { "id": "me1" }, "items": { "total": 2 } }""",
                "/v1/playlists/p1/items" => """{ "items": [ { "item": { "id": "a", "name": "A", "type": "track" } }, { "item": { "id": "e", "type": "episode" } } ] }""",
                _ => "",
            };
        }));

        var (pl, tracks) = await api.GetPlaylistAsync("sp-p1");
        Assert.Equal("Mine", pl.Title);
        Assert.Equal("sp-a", Assert.Single(tracks).Id);

        await api.AddToPlaylistAsync(pl, tracks[0]);
        var add = requests.First(r => r.Line == "POST /v1/playlists/p1/items");
        Assert.Contains("spotify:track:a", add.Body);

        await api.PlayAsync("dev1", "sp-a", 5000);
        var play = requests[^1];
        Assert.Equal("PUT /v1/me/player/play", play.Line);
        Assert.Contains("\"position_ms\":5000", play.Body);
    }
}
