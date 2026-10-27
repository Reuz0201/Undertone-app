using System.Windows.Media;
using Undertone.Core;

namespace Undertone.UI
{
    /// <summary>Карточка микса Spotify на странице «Подборки».</summary>
    public sealed class SpotifyMixItem
    {
        // зелёные и тёплые градиенты — чтобы отличались от подборок по правилам
        private static readonly (Color A, Color B)[] Palettes =
        {
            (Color.FromRgb(0x0E, 0x4D, 0x2C), Color.FromRgb(0x1D, 0xB9, 0x54)),
            (Color.FromRgb(0x3D, 0x1E, 0x5C), Color.FromRgb(0xC0, 0x5B, 0xE8)),
            (Color.FromRgb(0x5A, 0x2A, 0x10), Color.FromRgb(0xF2, 0x9B, 0x38)),
            (Color.FromRgb(0x10, 0x34, 0x5A), Color.FromRgb(0x3B, 0xA7, 0xE8)),
            (Color.FromRgb(0x5C, 0x14, 0x30), Color.FromRgb(0xF0, 0x5A, 0x7E)),
            (Color.FromRgb(0x2A, 0x4A, 0x14), Color.FromRgb(0x9C, 0xD6, 0x3C)),
        };

        public SpotifyMixItem(SpotifyMix mix, int index)
        {
            Mix = mix;
            var (a, b) = Palettes[index % Palettes.Length];
            var brush = new LinearGradientBrush(a, b, 45);
            brush.Freeze();
            Background = brush;
        }

        public SpotifyMix Mix { get; }
        public string Title => Mix.Title;
        public string Sub => Mix.Sub;
        public string Glyph => Mix.Glyph;
        public Brush Background { get; }
    }
}
