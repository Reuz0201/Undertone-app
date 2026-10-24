using System.Windows.Media;
using Undertone.Core;

namespace Undertone.UI
{
    /// <summary>Карточка умного плейлиста на странице «Подборки».</summary>
    public sealed class SmartItem
    {
        // спокойные градиенты: у каждой подборки свой, по порядку
        private static readonly (Color A, Color B)[] Palettes =
        {
            (Color.FromRgb(0x3B, 0x2F, 0x6B), Color.FromRgb(0x9A, 0x7B, 0xF5)),
            (Color.FromRgb(0x14, 0x4E, 0x5A), Color.FromRgb(0x4F, 0xC1, 0xB5)),
            (Color.FromRgb(0x1C, 0x22, 0x4A), Color.FromRgb(0x5B, 0x8E, 0xF0)),
            (Color.FromRgb(0x6B, 0x2A, 0x3A), Color.FromRgb(0xF0, 0x8A, 0x6A)),
            (Color.FromRgb(0x3A, 0x45, 0x1E), Color.FromRgb(0xB5, 0xC9, 0x5B)),
        };

        public SmartItem(SmartPlaylist rule, int index)
        {
            Rule = rule;
            var (a, b) = Palettes[index % Palettes.Length];
            var brush = new LinearGradientBrush(a, b, 45);
            brush.Freeze();
            Background = brush;
        }

        public SmartPlaylist Rule { get; }
        public string Title => Rule.Name;
        public string Sub => Rule.Describe();
        public Brush Background { get; }
        public string Glyph => Rule.DayPart == DayPart.Night ? ""
            : Rule.Sort == SmartSort.MostPlayed ? ""
            : Rule.Source == SmartSource.Local ? ""
            : Rule.NotPlayedDays > 0 ? ""
            : "";
    }
}
