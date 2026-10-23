using System;
using System.IO;
using Undertone.Core;

namespace Undertone.Tests;

public class EnvFileTests
{
    [Theory]
    [InlineData("access_token: abc", "access_token", "abc", ':')]
    [InlineData("access_token=abc", "access_token", "abc", '=')]
    [InlineData("export TOKEN=\"x y\"", "TOKEN", "x y", '=')]
    [InlineData("  key :  'v:1'  ", "key", "v:1", ':')]
    [InlineData("url=https://a.b/c", "url", "https://a.b/c", '=')]
    public void ParseLine_UnderstandsBothFormats(string line, string key, string value, char sep)
    {
        var parsed = EnvFile.ParseLine(line);
        Assert.NotNull(parsed);
        Assert.Equal(key, parsed!.Value.Key);
        Assert.Equal(value, parsed.Value.Value);
        Assert.Equal(sep, parsed.Value.Sep);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# comment")]
    [InlineData("no separator")]
    [InlineData("=value")]
    public void ParseLine_IgnoresJunk(string line) => Assert.Null(EnvFile.ParseLine(line));

    [Fact]
    public void Set_KeepsCommentsOrderAndSeparators()
    {
        var dir = Directory.CreateTempSubdirectory("undertone-test-");
        try
        {
            var path = Path.Combine(dir.FullName, ".env");
            File.WriteAllText(path, "# мои токены\naccess_token=old\nother: 1\n");
            var env = EnvFile.Open(path);
            env.Set(new System.Collections.Generic.Dictionary<string, string?> { ["access_token"] = "new", ["added"] = "x" });

            var lines = File.ReadAllLines(path);
            Assert.Equal("# мои токены", lines[0]);
            Assert.Equal("access_token=new", lines[1]);
            Assert.Equal("other: 1", lines[2]);
            Assert.Equal("added: x", lines[3]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacy_CopiesTokensFromExeFolder()
    {
        var dir = Directory.CreateTempSubdirectory("undertone-test-");
        try
        {
            var exeDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "app")).FullName;
            File.WriteAllText(Path.Combine(exeDir, ".env"), "access_token: legacy\n");
            var target = Path.Combine(dir.FullName, "data", ".env");

            EnvFile.MigrateLegacy(target, exeDir);

            Assert.Equal("legacy", EnvFile.Open(target).Get("access_token"));
            Assert.False(File.Exists(Path.Combine(exeDir, ".env"))); // рядом с exe токены не оставляем
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
