using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Input;
using Undertone.Core;
using Undertone.UI;

namespace Undertone.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(1, "трек")]
    [InlineData(2, "трека")]
    [InlineData(5, "треков")]
    [InlineData(11, "треков")]
    [InlineData(21, "трек")]
    [InlineData(112, "треков")]
    [InlineData(1004, "трека")]
    public void Plural(int n, string expected) => Assert.Equal(expected, Format.Plural(n, "трек", "трека", "треков"));

    [Fact]
    public void Time() => Assert.Equal("1:02:03", Format.Time(new TimeSpan(1, 2, 3)));
}

public class LyricsParserTests
{
    [Fact]
    public void ParseLrc_SortsAndHandlesMultipleStamps()
    {
        var lines = LyricsParser.ParseLrc("[ar:Артист]\n[00:10.50]вторая\n[00:01.00][00:20]припев\n[00:15.00]\n[00:16.00]\n");
        Assert.Equal(new[] { "припев", "вторая", "♪", "припев" }, lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(10500), lines[1].Time);
    }

    [Fact]
    public void ActiveIndex_FindsLastStartedLine()
    {
        var lines = LyricsParser.ParseLrc("[00:01]a\n[00:05]b\n[00:09]c");
        Assert.Equal(-1, LyricsParser.ActiveIndex(lines, TimeSpan.FromSeconds(0.5)));
        Assert.Equal(1, LyricsParser.ActiveIndex(lines, TimeSpan.FromSeconds(6)));
        Assert.Equal(2, LyricsParser.ActiveIndex(lines, TimeSpan.FromSeconds(100)));
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+L", ModifierKeys.Control | ModifierKeys.Alt, Key.L)]
    [InlineData("Ctrl+Alt+Right", ModifierKeys.Control | ModifierKeys.Alt, Key.Right)]
    [InlineData("Ctrl+Shift+→", ModifierKeys.Control | ModifierKeys.Shift, Key.Right)]
    [InlineData("Win+5", ModifierKeys.Windows, Key.D5)]
    [InlineData("Ctrl+Alt+Пробел", ModifierKeys.Control | ModifierKeys.Alt, Key.Space)]
    public void Parse(string text, ModifierKeys mods, Key key)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(mods, hk.Modifiers);
        Assert.Equal(key, hk.Key);
        // туда и обратно
        Assert.True(Hotkey.TryParse(hk.ToString(), out var again));
        Assert.Equal(hk, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("L")]          // без модификатора
    [InlineData("Ctrl+")]      // без клавиши
    [InlineData("Ctrl+Банан")]
    public void ParseRejects(string text) => Assert.False(Hotkey.TryParse(text, out _));
}

public class TrackCryptoTests
{
    [Fact]
    public void CtrIsSymmetric_AndMatchesReference()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var data = RandomNumberGenerator.GetBytes(200_000 + 7); // не кратно 16 и больше буфера
        var dir = Directory.CreateTempSubdirectory("undertone-test-");
        try
        {
            var plain = Path.Combine(dir.FullName, "plain");
            var enc = Path.Combine(dir.FullName, "enc");
            var dec = Path.Combine(dir.FullName, "dec");
            File.WriteAllBytes(plain, data);

            TrackCrypto.DecryptFile(plain, enc, Convert.ToHexString(key)); // CTR: шифрование = расшифровка
            Assert.Equal(Reference(data, key), File.ReadAllBytes(enc));

            TrackCrypto.DecryptFile(enc, dec, Convert.ToHexString(key));
            Assert.Equal(data, File.ReadAllBytes(dec));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Эталон AES-CTR: nonce 12 нулевых байт + 32-битный big-endian счётчик с нуля.</summary>
    private static byte[] Reference(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var result = new byte[data.Length];
        var counter = new byte[16];
        var ks = new byte[16];
        for (int block = 0; block * 16 < data.Length; block++)
        {
            counter[12] = (byte)(block >> 24); counter[13] = (byte)(block >> 16);
            counter[14] = (byte)(block >> 8); counter[15] = (byte)block;
            aes.EncryptEcb(counter, ks, PaddingMode.None);
            for (int j = 0; j < 16 && block * 16 + j < data.Length; j++)
                result[block * 16 + j] = (byte)(data[block * 16 + j] ^ ks[j]);
        }
        return result;
    }
}

public class AudioCacheTests
{
    [Fact]
    public void TrimRemovesOldestOverLimit_AndClearEmpties()
    {
        var dir = Directory.CreateTempSubdirectory("undertone-test-");
        try
        {
            var cache = new AudioCache(maxBytes: 2500, directory: dir.FullName);
            for (int i = 0; i < 4; i++)
            {
                var path = cache.PathFor(i.ToString());
                File.WriteAllBytes(path, new byte[1000]);
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddMinutes(i)); // 3 — самый свежий
            }
            File.WriteAllBytes(Path.Combine(dir.FullName, "x.mp3.part"), new byte[10]); // брошенная закачка

            cache.Trim();
            Assert.Equal(new[] { "2.mp3", "3.mp3" }, Directory.GetFiles(dir.FullName).Select(Path.GetFileName).OrderBy(n => n));
            Assert.Equal((2000L, 2), cache.GetUsage());

            Assert.Equal(2000, cache.Clear());
            Assert.Empty(Directory.GetFiles(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindCached_ChecksFormatsInOrder()
    {
        var dir = Directory.CreateTempSubdirectory("undertone-test-");
        try
        {
            var cache = new AudioCache(1 << 20, dir.FullName);
            File.WriteAllBytes(cache.PathFor("7", "m4a"), new byte[1]);
            Assert.Equal(cache.PathFor("7", "m4a"), cache.FindCached("7", "flac", "m4a"));
            Assert.Null(cache.FindCached("7", "mp3"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
