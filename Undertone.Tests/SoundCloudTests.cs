using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Undertone.Core;

namespace Undertone.Tests;

public class SoundCloudParsingTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ParseTrack_IdsArtworkAndUploader()
    {
        var t = ScApi.ParseTrack(Json("""
            {
              "urn": "soundcloud:tracks:308946187", "title": "Night Drive", "duration": 215000,
              "access": "playable", "streamable": true, "permalink_url": "https://soundcloud.com/neon_waves/night-drive",
              "artwork_url": "https://i1.sndcdn.com/artworks-000-abc-large.jpg",
              "user": { "urn": "soundcloud:users:948745750", "username": "neon_waves" }
            }
            """));

        Assert.Equal("sc-308946187", t.Id);
        Assert.True(ScApi.IsScId(t.Id));
        Assert.Equal("Night Drive", t.Title);
        Assert.Equal(new[] { "neon_waves" }, t.Artists);
        Assert.Equal(new YaArtistRef("sc-948745750", "neon_waves"), Assert.Single(t.ArtistRefs));
        Assert.Equal(TimeSpan.FromSeconds(215), t.Duration);
        Assert.True(t.Available);
        Assert.False(t.IsPreview);
        Assert.Equal("", t.AlbumId);
        Assert.Equal("sc-308946187", t.LikeId);
        Assert.Equal("https://i1.sndcdn.com/artworks-000-abc-t300x300.jpg", t.CoverUrl(200));
        Assert.Equal("https://i1.sndcdn.com/artworks-000-abc-large.jpg", t.CoverUrl(100));
        Assert.Equal("https://i1.sndcdn.com/artworks-000-abc-t500x500.jpg", t.CoverUrl(600));
        Assert.Equal("https://soundcloud.com/neon_waves/night-drive", t.WebUrl);
    }

    [Fact]
    public void WebUrl_YandexLocalAndSoundCloudWithoutLink()
    {
        Assert.Equal("https://music.yandex.ru/album/a5/track/5", TestData.Track("5").WebUrl);
        Assert.Null(TestData.Track(LocalLibrary.IdPrefix + "x").WebUrl);
        Assert.Null(TestData.Track("sc-5").WebUrl);
    }

    [Fact]
    public void ParseTrack_OldNumericIds_MetadataArtistWins_AvatarAsCover()
    {
        var t = ScApi.ParseTrack(Json("""
            { "id": 42, "title": "x", "metadata_artist": "Real Artist",
              "user": { "id": 7, "username": "label", "avatar_url": "https://i1.sndcdn.com/avatars-1-large.jpg" } }
            """));
        Assert.Equal("sc-42", t.Id);
        Assert.Equal(new[] { "Real Artist" }, t.Artists);
        Assert.Equal("sc-7", t.ArtistRefs[0].Id);
        Assert.Equal("https://i1.sndcdn.com/avatars-1-t67x67.jpg", t.CoverUrl(60));
    }

    [Theory]
    [InlineData("playable", true, true, false)]
    [InlineData("preview", true, true, true)]
    [InlineData("blocked", true, false, false)]
    [InlineData("playable", false, false, false)]
    public void ParseTrack_Access(string access, bool streamable, bool available, bool preview)
    {
        var t = ScApi.ParseTrack(Json(
            $$"""{ "urn": "soundcloud:tracks:1", "title": "x", "access": "{{access}}", "streamable": {{(streamable ? "true" : "false")}} }"""));
        Assert.Equal(available, t.Available);
        Assert.Equal(preview, t.IsPreview);
    }

    [Fact]
    public void ParsePlaylist_IdOwnerAndCoverFallback()
    {
        var p = ScApi.ParsePlaylist(Json("""
            { "urn": "soundcloud:playlists:5", "title": "Mix", "track_count": 12, "duration": 3600000, "artwork_url": null,
              "user": { "urn": "soundcloud:users:9", "username": "me" },
              "tracks": [ { "artwork_url": null }, { "artwork_url": "https://i1.sndcdn.com/artworks-2-large.png" } ] }
            """));
        Assert.Equal("sc-5", p.Kind);
        Assert.Equal(-9, p.OwnerUid);
        Assert.Equal("me", p.OwnerName);
        Assert.Equal(12, p.TrackCount);
        Assert.Equal("https://i1.sndcdn.com/artworks-2-t300x300.png", p.CoverUrl(300));
    }

    [Theory]
    [InlineData("soundcloud:tracks:123", "sc-123")]
    [InlineData("123", "sc-123")]
    [InlineData("sc-123", "sc-123")]
    [InlineData("", "")]
    public void ToId(string input, string expected) => Assert.Equal(expected, ScApi.ToId(input));

    [Theory]
    [InlineData("sc-5", "playlists", "soundcloud:playlists:5")]
    [InlineData("soundcloud:users:7", "users", "soundcloud:users:7")]
    [InlineData("8", "tracks", "soundcloud:tracks:8")]
    public void ToUrn(string id, string kind, string expected) => Assert.Equal(expected, ScApi.ToUrn(id, kind));

    [Fact]
    public void TrackUrn_AcceptsLikeIdAndUrn()
    {
        Assert.Equal("soundcloud:tracks:3", ScApi.TrackUrn("sc-3"));
        Assert.Equal("soundcloud:tracks:3", ScApi.TrackUrn("sc-3:"));
        Assert.Equal("soundcloud:tracks:3", ScApi.TrackUrn("soundcloud:tracks:3"));
    }

    [Theory]
    [InlineData("https://i1.sndcdn.com/artworks-000-abc-large.jpg", "https://i1.sndcdn.com/artworks-000-abc-%%.jpg")]
    [InlineData("https://a1.sndcdn.com/images/default_avatar_large.png", "https://a1.sndcdn.com/images/default_avatar_large.png")]
    [InlineData("", null)]
    public void ArtworkTemplate(string url, string? expected) => Assert.Equal(expected, ScApi.ArtworkTemplate(url));

    [Fact]
    public void CoverUris_YandexAndLocalUnchanged()
    {
        Assert.Equal("https://avatars.yandex.net/x/200x200", CoverUris.Resolve("avatars.yandex.net/x/%%", 200));
        Assert.Equal("file:///C:/music/cover.jpg", CoverUris.Resolve("file:///C:/music/cover.jpg", 200));
        Assert.Null(CoverUris.Resolve(null, 200));
    }

    [Fact]
    public void ParseCollection_FollowsNextHref()
    {
        var (items, next) = ScApi.ParseCollection(Json("""
            { "collection": [ { "urn": "a" }, { "urn": "b" } ], "next_href": "https://api.soundcloud.com/me/likes/tracks?cursor=x" }
            """));
        Assert.Equal(2, items.Count);
        Assert.Equal("https://api.soundcloud.com/me/likes/tracks?cursor=x", next);
        Assert.Null(ScApi.ParseCollection(Json("""{ "collection": [], "next_href": null }""")).Next);
        Assert.Single(ScApi.ParseCollection(Json("""[ { "urn": "a" } ]""")).Items);
    }

    [Fact]
    public void PickStreamUrl_PrefersHlsMp3_PreviewForPreviewTracks()
    {
        var streams = Json("""
            { "hls_aac_160_url": "https://x/aac", "hls_mp3_128_url": "https://x/mp3", "preview_mp3_128_url": "https://x/preview" }
            """);
        Assert.Equal("https://x/mp3", ScApi.PickStreamUrl(streams, preview: false));
        Assert.Equal("https://x/preview", ScApi.PickStreamUrl(streams, preview: true));
        Assert.Null(ScApi.PickStreamUrl(Json("""{ "hls_aac_160_url": "https://x/aac" }"""), preview: false));
    }

    [Fact]
    public void ErrorMessage_ReadsSoundCloudShapes()
    {
        Assert.Equal("Not found", ScApi.ErrorMessage(Json("""{ "code": 404, "message": "Not found" }""")));
        Assert.Equal("bad urn", ScApi.ErrorMessage(Json("""{ "errors": [ { "error_message": "bad urn" } ] }""")));
        Assert.Null(ScApi.ErrorMessage(Json("""{ }""")));
    }

    [Fact]
    public void NearestIndex_PicksClosestOccurrence()
    {
        var ids = new[] { "sc-1", "sc-2", "sc-1", "sc-3" };
        Assert.Equal(2, ScApi.NearestIndex(ids, "sc-1", 3));
        Assert.Equal(0, ScApi.NearestIndex(ids, "sc-1", 0));
        Assert.Equal(-1, ScApi.NearestIndex(ids, "sc-9", 0));
    }

    [Fact]
    public void ServiceOfTrack()
    {
        Assert.Equal(MusicServiceKind.SoundCloud, MusicServices.Of(TestData.Track("sc-1")));
        Assert.Equal(MusicServiceKind.Yandex, MusicServices.Of(TestData.Track("123")));
        Assert.Null(MusicServices.Of(TestData.Track(LocalLibrary.IdPrefix + "abc")));
        Assert.False(YaApi.IsYandexId("sc-1"));
        Assert.True(YaApi.IsYandexId("123"));
    }
}

public class ScApiTests
{
    [Fact]
    public async Task Account_UidIsNegative_SoCachesDoNotMixWithYandex()
    {
        using var api = new ScApi(new FakeHandler(_ => """
            { "urn": "soundcloud:users:948745750", "username": "neon_waves", "full_name": "Neon",
              "avatar_url": "https://i1.sndcdn.com/avatars-1-large.jpg" }
            """));
        var acc = await api.GetAccountAsync();
        Assert.Equal(-948745750, acc.Uid);
        Assert.Equal(acc.Uid, api.Uid);
        Assert.Equal("Neon", acc.DisplayName);
        Assert.Equal("https://i1.sndcdn.com/avatars-1-t300x300.jpg", await api.GetAvatarUrlAsync());
    }

    [Fact]
    public async Task LikedIds_ThenTracks_ServedWithoutExtraRequests()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/me/likes/tracks" => """
                { "collection": [ { "urn": "soundcloud:tracks:1", "title": "один" }, { "urn": "soundcloud:tracks:2", "title": "два" } ],
                  "next_href": null }
                """,
            _ => """{ "urn": "soundcloud:tracks:3", "title": "три" }""",
        });
        using var api = new ScApi(handler);

        var (_, ids) = await api.GetLikedIdsAsync();
        Assert.Equal(new[] { "sc-1", "sc-2" }, ids);
        var calls = handler.Calls;

        // чужие id (Яндекс) пропускаются, известные — из памяти, неизвестный sc-3 — одним запросом
        var tracks = await api.GetTracksAsync(new[] { "sc-2", "777", "sc-3", "sc-1" });
        Assert.Equal(new[] { "sc-2", "sc-3", "sc-1" }, tracks.Select(t => t.Id));
        Assert.Equal(calls + 1, handler.Calls);
    }

    [Fact]
    public async Task RemoveTrack_PutsListWithoutNearestOccurrence()
    {
        string? put = null;
        var handler = new FakeHandler(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                put = req.Content!.ReadAsStringAsync().Result;
                return """{ "urn": "soundcloud:playlists:5", "title": "P", "track_count": 2 }""";
            }
            return """
                { "collection": [ { "urn": "soundcloud:tracks:1", "title": "a" }, { "urn": "soundcloud:tracks:9", "title": "b" },
                                  { "urn": "soundcloud:tracks:1", "title": "a" } ] }
                """;
        });
        using var api = new ScApi(handler);

        var updated = await api.RemoveTrackFromPlaylistAsync("sc-5", "sc-1", hintIndex: 2);
        Assert.Equal("sc-5", updated.Kind);
        using var doc = JsonDocument.Parse(put!);
        var urns = doc.RootElement.GetProperty("playlist").GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("urn").GetString()).ToArray();
        Assert.Equal(new[] { "soundcloud:tracks:1", "soundcloud:tracks:9" }, urns);
    }

    [Fact]
    public async Task ResolveAudio_HlsPlaylistBecomesSegments()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/streams")
            ? """{ "hls_mp3_128_url": "https://api.soundcloud.com/hls/1/playlist.m3u8" }"""
            : "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nseg0.mp3\n#EXTINF:10,\nseg1.mp3\n#EXT-X-ENDLIST\n");
        using var api = new ScApi(handler);

        var source = await api.ResolveAudioAsync(TestData.Track("sc-1"), lowest: false);
        Assert.Null(source.Key);
        Assert.Equal(new[] { "https://api.soundcloud.com/hls/1/seg0.mp3", "https://api.soundcloud.com/hls/1/seg1.mp3" }, source.Segments);
    }

    [Fact]
    public async Task ResolveAudio_EncryptedStream_Refused()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/streams")
            ? """{ "hls_mp3_128_url": "https://api.soundcloud.com/hls/1.m3u8" }"""
            : "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXTINF:10,\ns.mp3\n");
        using var api = new ScApi(handler);
        await Assert.ThrowsAsync<YaApiException>(() => api.ResolveAudioAsync(TestData.Track("sc-1"), lowest: false));
    }

    [Fact]
    public async Task Search_TracksUsersPlaylists()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/tracks" => """{ "collection": [ { "urn": "soundcloud:tracks:1", "title": "t" } ] }""",
            "/users" => """{ "collection": [ { "urn": "soundcloud:users:2", "username": "u", "followers_count": 1500 } ] }""",
            "/playlists" => """{ "collection": [ { "urn": "soundcloud:playlists:3", "title": "p" } ] }""",
            _ => "{}",
        });
        using var api = new ScApi(handler);
        var r = await api.SearchAsync("q");
        Assert.Equal("sc-1", Assert.Single(r.Tracks).Id);
        Assert.Equal("sc-2", Assert.Single(r.Artists).Id);
        Assert.Equal("sc-3", Assert.Single(r.Playlists).Kind);
        Assert.Empty(r.Albums);
    }

    [Fact]
    public async Task YaApi_DoesNotSendForeignIdsToYandex()
    {
        string? sent = null;
        var handler = new FakeHandler(req =>
        {
            sent = req.Content?.ReadAsStringAsync().Result;
            return """{ "result": [ { "id": "5", "title": "пять" } ] }""";
        });
        using var api = new YaApi(handler);
        var tracks = await api.GetTracksAsync(new[] { "sc-1", "5", LocalLibrary.IdPrefix + "x" });
        Assert.Equal("5", Assert.Single(tracks).Id);
        Assert.DoesNotContain("sc-1", Uri.UnescapeDataString(sent ?? ""));
    }
}

public class HlsTests
{
    private static readonly Uri Base = new("https://cf-hls-media.sndcdn.com/playlist/abc/playlist.m3u8?Policy=x");

    [Fact]
    public void MediaPlaylist_SegmentsResolvedAgainstBase()
    {
        var p = HlsPlaylist.Parse("#EXTM3U\n#EXTINF:10.0,\nseg0.mp3?s=1\n#EXTINF:5.0,\nhttps://cdn.example/seg1.mp3\n#EXT-X-ENDLIST\n", Base);
        Assert.Null(p.Variant);
        Assert.False(p.Encrypted);
        Assert.Equal(new[]
        {
            new Uri("https://cf-hls-media.sndcdn.com/playlist/abc/seg0.mp3?s=1"),
            new Uri("https://cdn.example/seg1.mp3"),
        }, p.Segments);
    }

    [Fact]
    public void MasterPlaylist_ReturnsFirstVariant()
    {
        var p = HlsPlaylist.Parse("#EXTM3U\r\n#EXT-X-STREAM-INF:BANDWIDTH=128000\r\nmedia/a.m3u8\r\n#EXT-X-STREAM-INF:BANDWIDTH=64000\r\nmedia/b.m3u8\r\n", Base);
        Assert.Equal(new Uri("https://cf-hls-media.sndcdn.com/playlist/abc/media/a.m3u8"), p.Variant);
        Assert.Empty(p.Segments);
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"k\"", true)]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k\"", true)]
    [InlineData("#EXT-X-KEY:METHOD=NONE", false)]
    public void Encryption(string keyLine, bool encrypted) =>
        Assert.Equal(encrypted, HlsPlaylist.Parse($"#EXTM3U\n{keyLine}\n#EXTINF:10,\ns.mp3\n", Base).Encrypted);

    [Fact]
    public void StripId3_SkipsTagBeforeAudio()
    {
        // ID3v2.4 с телом 5 байт (размер syncsafe), затем MPEG-кадр 0xFF 0xFB
        var data = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4, 5, 0xFF, 0xFB, 0x90 };
        Assert.Equal((15, 3), AudioCache.StripId3(data));
        Assert.Equal((0, 2), AudioCache.StripId3(new byte[] { 0xFF, 0xFB }));
        // обрезанный тег не трогаем
        var cut = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0x7F, 0x7F, 1 };
        Assert.Equal((0, cut.Length), AudioCache.StripId3(cut));
    }
}

public class LoopbackOAuthTests
{
    [Fact]
    public void Pkce_ChallengeIsS256OfVerifier()
    {
        Assert.Equal("C9k4LfzAkmtAawgBfWCQruYi38EYmkNsz2pWyhJ9mtY",
            LoopbackOAuth.ChallengeFor("undertone-test-verifier-0123456789-abcdefghijklmnop"));
        var pkce = LoopbackOAuth.CreatePkce();
        Assert.Equal(43, pkce.Verifier.Length);
        Assert.Equal(LoopbackOAuth.ChallengeFor(pkce.Verifier), pkce.Challenge);
        Assert.DoesNotContain('=', pkce.Verifier + pkce.Challenge);
    }

    [Fact]
    public void ParseRequest()
    {
        Assert.Equal("/callback?code=abc&state=s1", LoopbackOAuth.ParseRequestLine("GET /callback?code=abc&state=s1 HTTP/1.1\r\nHost: x\r\n\r\n"));
        Assert.Null(LoopbackOAuth.ParseRequestLine("POST /callback HTTP/1.1\r\n"));

        var (path, q) = LoopbackOAuth.ParseRequestTarget("/callback?code=a%2Bb&State=x+y&flag");
        Assert.Equal("/callback", path);
        Assert.Equal("a+b", q["code"]);
        Assert.Equal("x y", q["state"]);
        Assert.Equal("", q["flag"]);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>«Браузер»: сначала favicon, потом редирект с кодом.</summary>
    private static Action<string> Browser(int port, string query) => _ => Task.Run(async () =>
    {
        using var http = new HttpClient();
        try { await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico"); } catch (HttpRequestException) { }
        await http.GetAsync($"http://127.0.0.1:{port}/callback?{query}");
    });

    [Fact]
    public async Task WaitForCode_ReturnsCode()
    {
        var port = FreePort();
        var code = await LoopbackOAuth.WaitForCodeAsync(new Uri($"http://127.0.0.1:{port}/callback"), "https://auth/x", "st",
            Browser(port, "code=the-code&state=st"), new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.Equal("the-code", code);
    }

    [Fact]
    public async Task WaitForCode_StateMismatch_Throws()
    {
        var port = FreePort();
        var ex = await Assert.ThrowsAsync<AuthException>(() => LoopbackOAuth.WaitForCodeAsync(
            new Uri($"http://127.0.0.1:{port}/callback"), "https://auth/x", "expected",
            Browser(port, "code=c&state=forged"), new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token));
        Assert.Equal("invalid_state", ex.Error);
    }

    [Fact]
    public async Task WaitForCode_CancelReleasesPortImmediately()
    {
        var port = FreePort();
        using var cts = new CancellationTokenSource();
        var wait = LoopbackOAuth.WaitForCodeAsync(new Uri($"http://127.0.0.1:{port}/callback"), "https://auth/x", "st", _ => { }, cts.Token);
        cts.Cancel();
        var again = new TcpListener(IPAddress.Loopback, port);
        again.Start();
        again.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WaitForCode_RejectsNonLoopbackRedirect() =>
        await Assert.ThrowsAsync<AuthException>(() => LoopbackOAuth.WaitForCodeAsync(
            new Uri("https://example.com/callback"), "https://auth/x", "st", _ => { }, CancellationToken.None));
}

public class SoundCloudAuthTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Config_FirstNonEmptySourceWins_DefaultRedirect()
    {
        var env = new Dictionary<string, string?> { ["soundcloud_client_id"] = "", ["soundcloud_client_secret"] = "from-env" };
        var file = new Dictionary<string, string?> { ["soundcloud_client_id"] = "id-from-file", ["soundcloud_client_secret"] = "from-file" };
        var cfg = SoundCloudConfig.Resolve(new Func<string, string?>[] { k => env.GetValueOrDefault(k), k => file.GetValueOrDefault(k) });

        Assert.NotNull(cfg);
        Assert.Equal("id-from-file", cfg!.ClientId);
        Assert.Equal("from-env", cfg.ClientSecret);
        Assert.Equal(SoundCloudConfig.DefaultRedirectUri, cfg.RedirectUri);
        Assert.Null(SoundCloudConfig.Resolve(new Func<string, string?>[] { _ => null, _ => "  " }));
    }

    [Fact]
    public void TokenResponse_ParsedOrError()
    {
        Assert.Equal(new OAuthToken("2-abc", "r-new", 3599), SoundCloudAuth.ParseTokenResponse(Json(
            """{ "access_token": "2-abc", "refresh_token": "r-new", "expires_in": 3599 }""")));
        var ex = Assert.Throws<AuthException>(() => SoundCloudAuth.ParseTokenResponse(Json(
            """{ "error": "invalid_grant", "error_description": "The refresh token is invalid." }""")));
        Assert.Equal("invalid_grant", ex.Error);
    }

    [Fact]
    public async Task TokenStore_SoundCloudKeysSeparate_SingleUseRefreshSaved()
    {
        var dir = Directory.CreateTempSubdirectory("undertone-test-");
        try
        {
            var path = Path.Combine(dir.FullName, ".env");
            File.WriteAllText(path, "access_token: yandex-token-stays\n");
            var env = EnvFile.Open(path);
            var used = new List<string>();
            var sc = TokenStore.ForSoundCloud(env, (rt, _) =>
            {
                used.Add(rt);
                return Task.FromResult(new OAuthToken($"access-{used.Count}", $"refresh-{used.Count}", 3600));
            });

            Assert.Null(sc.AccessToken); // токен Яндекса не подхватывается
            sc.Save(new OAuthToken("access-0", "refresh-0", 3600));
            Assert.Equal("access-1", await sc.TryRefreshAsync("access-0", CancellationToken.None));
            Assert.Equal("access-2", await sc.TryRefreshAsync("access-1", CancellationToken.None));
            Assert.Equal(new[] { "refresh-0", "refresh-1" }, used);

            var text = File.ReadAllText(path);
            Assert.Contains("soundcloud_access_token", text);
            Assert.DoesNotContain("access-2", text); // зашифровано
            Assert.Equal("yandex-token-stays", EnvFile.Open(path).Get("access_token"));

            sc.Clear();
            Assert.Null(sc.AccessToken);
            Assert.Equal("yandex-token-stays", EnvFile.Open(path).Get("access_token"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
