using System;
using System.IO;
using System.Security.Cryptography;
using Undertone.Core;

namespace Undertone.Tests;

public class UpdaterTests
{
    [Theory]
    [InlineData("v2.1.0", 2, 1, 0)]
    [InlineData("2.1", 2, 1, 0)]
    [InlineData("v3", 3, 0, 0)]
    [InlineData("v2.2.0-beta+abc", 2, 2, 0)]
    public void TryParseVersion(string text, int major, int minor, int build)
    {
        Assert.True(Updater.TryParseVersion(text, out var v));
        Assert.Equal(new Version(major, minor, build, 0), v);
    }

    [Fact]
    public void TryParseVersion_ReadsBetaTag()
    {
        Assert.True(Updater.TryParseVersion("v4.3.0.57-beta.pr12", out var v));
        Assert.Equal(new Version(4, 3, 0, 57), v);
        Assert.True(Updater.IsBetaVersion(v));
        Assert.Equal("4.3.0 бета 57", Updater.Display(v));
        Assert.Equal("4.3.0", Updater.Display(new Version(4, 3, 0, 0)));
    }

    [Fact]
    public void TryParseVersion_RejectsGarbage() => Assert.False(Updater.TryParseVersion("latest", out _));

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef  Undertone-win-x64.zip")]
    [InlineData("SHA256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void ParseSha256_AcceptsCommonFormats(string text) =>
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", Updater.ParseSha256(text));

    [Fact]
    public void ParseSha256_RejectsBadFile() => Assert.Throws<UpdateException>(() => Updater.ParseSha256("nope"));

    [Fact]
    public void ParseRelease_FindsAssetsAndSkipsDrafts()
    {
        const string json = """
        {
          "tag_name": "v2.2.0", "draft": false, "prerelease": false, "body": " Что нового ", "html_url": "https://x",
          "assets": [
            { "name": "Undertone-win-x64.zip", "browser_download_url": "https://zip", "size": 123 },
            { "name": "Undertone-win-x64.zip.sha256", "browser_download_url": "https://sha" },
            { "name": "Undertone-win-x64.zip.sig", "browser_download_url": "https://sig" }
          ]
        }
        """;
        var info = Updater.ParseRelease(json)!;
        Assert.Equal(new Version(2, 2, 0, 0), info.Version);
        Assert.Equal("https://zip", info.ZipUrl);
        Assert.Equal("https://sha", info.Sha256Url);
        Assert.Equal("https://sig", info.SignatureUrl);
        Assert.Equal(123, info.Size);
        Assert.Equal("Что нового", info.Notes);

        Assert.Null(Updater.ParseRelease(json.Replace("\"draft\": false", "\"draft\": true")));
    }

    [Fact]
    public void Signature_AcceptsOwnAndRejectsTampered()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });
            var signature = Convert.ToBase64String(key.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256));
            Assert.True(Updater.VerifySignature(file, signature, publicKey));

            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 6 });
            Assert.False(Updater.VerifySignature(file, signature, publicKey));
            Assert.False(Updater.VerifySignature(file, "не base64", publicKey));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Apply_ReplacesFilesAndKeepsEnv()
    {
        var root = Directory.CreateTempSubdirectory("undertone-test-");
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "app")).FullName;
            var staging = Directory.CreateDirectory(Path.Combine(root.FullName, "staging")).FullName;
            File.WriteAllText(Path.Combine(app, "Undertone.exe"), "old");
            File.WriteAllText(Path.Combine(app, ".env"), "secret");
            File.WriteAllText(Path.Combine(staging, "Undertone.exe"), "new");
            File.WriteAllText(Path.Combine(staging, ".env"), "should not overwrite");

            new Updater("o/r", new Version(1, 0, 0), app).Apply(staging);

            Assert.Equal("new", File.ReadAllText(Path.Combine(app, "Undertone.exe")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(app, "Undertone.exe.old")));
            Assert.Equal("secret", File.ReadAllText(Path.Combine(app, ".env")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void PlainReleaseNotes_StripsMarkdownAndInstallTail()
    {
        var md = "## Что нового\r\n\r\n- **SoundCloud**: вход и `HLS`\r\n- [Ссылка](https://x.y) работает\r\n\r\n\r\n\r\n**Установка:** скачай zip";
        var plain = Undertone.MainWindow.PlainReleaseNotes(md);
        Assert.Equal("Что нового\n\n• SoundCloud: вход и HLS\n• Ссылка работает", plain);
        Assert.Equal("", Undertone.MainWindow.PlainReleaseNotes("  "));
    }

    private static UpdateInfo Rel(string version, bool beta = false) =>
        new(Version.Parse(version), "v" + version, "https://zip", null, "", "", 0, IsBeta: beta);

    [Fact]
    public void Choose_StableIgnoresBeta()
    {
        var list = new[] { Rel("4.3.0"), Rel("4.3.0.57", beta: true) };
        Assert.Null(Updater.Choose(list, new Version(4, 3, 0, 0), includeBeta: false));
        Assert.Equal(new Version(4, 3, 0, 57), Updater.Choose(list, new Version(4, 3, 0, 0), includeBeta: true)!.Version);
    }

    [Fact]
    public void Choose_BetaMovesToNextRelease()
    {
        var list = new[] { Rel("4.3.1"), Rel("4.3.0.57", beta: true) };
        Assert.Equal(new Version(4, 3, 1, 0), Updater.Choose(list, new Version(4, 3, 0, 57), includeBeta: true)!.Version);
    }

    [Fact]
    public void Choose_TurningBetaOffReturnsToStable()
    {
        var list = new[] { Rel("4.3.0") };
        Assert.Equal(new Version(4, 3, 0, 0), Updater.Choose(list, new Version(4, 3, 0, 57), includeBeta: false)!.Version);
        // на стабильной — ничего не делаем
        Assert.Null(Updater.Choose(list, new Version(4, 3, 0, 0), includeBeta: false));
    }

    [Fact]
    public void ParseReleases_KeepsPrereleasesAsBeta()
    {
        const string json = """
        [
          { "tag_name": "v4.3.0.57-beta.pr12", "name": "Бета #12", "prerelease": true, "draft": false,
            "assets": [ { "name": "Undertone-win-x64.zip", "browser_download_url": "https://b", "size": 1 } ] },
          { "tag_name": "v4.3.0", "prerelease": false, "draft": false,
            "assets": [ { "name": "Undertone-win-x64.zip", "browser_download_url": "https://s", "size": 1 } ] },
          { "tag_name": "v4.4.0", "prerelease": false, "draft": true,
            "assets": [ { "name": "Undertone-win-x64.zip", "browser_download_url": "https://d", "size": 1 } ] }
        ]
        """;
        var list = Updater.ParseReleases(json);
        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsBeta);
        Assert.Equal("Бета #12", list[0].Title);
        Assert.False(list[1].IsBeta);
        // одиночный релиз (/releases/latest) pre-release по-прежнему не принимает
        Assert.Null(Updater.ParseRelease("""
            { "tag_name": "v4.3.0.57-beta.pr12", "prerelease": true,
              "assets": [ { "name": "Undertone-win-x64.zip", "browser_download_url": "https://b" } ] }
            """));
    }

    [Fact]
    public void Choose_SkipsRolledBackVersions()
    {
        var list = new[] { Rel("4.4.0"), Rel("4.3.1") };
        var skip = new System.Collections.Generic.HashSet<Version> { new(4, 4, 0, 0) };
        Assert.Equal(new Version(4, 3, 1, 0), Updater.Choose(list, new Version(4, 3, 0, 0), false, skip)!.Version);
    }

    [Fact]
    public void RestoreOld_PutsPreviousFilesBack()
    {
        var dir = Directory.CreateTempSubdirectory("ut-rollback").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Undertone.exe"), "new");
            File.WriteAllText(Path.Combine(dir, "Undertone.exe.old"), "old");
            File.WriteAllText(Path.Combine(dir, "added.dll"), "new only");
            Assert.True(UpdateGuard.RestoreOld(dir));
            Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "Undertone.exe")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(dir, "Undertone.exe.bad")));
            Assert.False(File.Exists(Path.Combine(dir, "Undertone.exe.old")));
            Assert.True(Updater.IsProtected("Undertone.exe.bad"));
            Assert.False(UpdateGuard.RestoreOld(dir)); // больше нечего возвращать
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BugReport_IssueUrl()
    {
        var url = BugReport.IssueUrl("owner/repo", "Spotify", "NullReferenceException: boom");
        Assert.StartsWith("https://github.com/owner/repo/issues/new?title=", url);
        Assert.Contains(Uri.EscapeDataString("Сервис: Spotify"), url);
        Assert.StartsWith("https://github.com/Reuz0201/Undertone-app/", BugReport.IssueUrl(null));
        Assert.Equal("ab…", BugReport.Shorten("ab\ncd", 3));
    }
}
