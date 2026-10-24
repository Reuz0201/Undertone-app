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
}
