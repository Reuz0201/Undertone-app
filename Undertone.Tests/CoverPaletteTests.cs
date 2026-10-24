using Undertone.UI;

namespace Undertone.Tests;

public class CoverPaletteTests
{
    private static byte[] Fill(int count, byte r, byte g, byte b)
    {
        var px = new byte[count * 4];
        for (int i = 0; i < count; i++) { px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255; }
        return px;
    }

    [Fact]
    public void OrangeCover_GivesWarmReadableAccent()
    {
        var (r, g, b) = CoverPalette.Pick(Fill(100, 240, 130, 20));
        Assert.True(r > g && g > b, $"{r},{g},{b}");
        // светлый: читается на тёмном фоне
        Assert.True((r + g + b) / 3 > 140);
    }

    [Fact]
    public void GrayCover_GivesNeutral()
    {
        var (r, g, b) = CoverPalette.Pick(Fill(100, 120, 120, 122));
        Assert.Equal((CoverPalette.Neutral.R, CoverPalette.Neutral.G, CoverPalette.Neutral.B), (r, g, b));
    }

    [Fact]
    public void SmallVividSpot_WinsOverDarkBackground()
    {
        var px = Fill(100, 10, 10, 12);
        var spot = Fill(20, 40, 90, 230);
        spot.CopyTo(px, 0);
        var (r, g, b) = CoverPalette.Pick(px);
        Assert.True(b > r && b > g, $"{r},{g},{b}");
    }

    [Theory]
    [InlineData("smoroz07@gmail.com", "smoroz07")]
    [InlineData("  Никитка ", "Никитка")]
    [InlineData("", "Друг")]
    [InlineData("@", "@")]
    public void PublicName_HidesEmail(string input, string expected) =>
        Assert.Equal(expected, Format.PublicName(input));
}
