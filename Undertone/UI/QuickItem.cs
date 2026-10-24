using Undertone.Core;

namespace Undertone.UI
{
    public enum QuickKind { Track, Artist, Album, Playlist, AllResults }

    /// <summary>Строка быстрого поиска (Ctrl+K).</summary>
    public sealed class QuickItem
    {
        public QuickKind Kind { get; init; }
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public string? Cover { get; init; }
        public object? Payload { get; init; }
        /// <summary>Подпись секции над первой строкой группы («Треки», «Артисты»…); пусто — без подписи.</summary>
        public string Section { get; init; } = "";
        public bool HasSection => Section.Length > 0;
        public bool IsRound => Kind == QuickKind.Artist;
        public bool IsAll => Kind == QuickKind.AllResults;
        public string Glyph => Kind switch
        {
            QuickKind.Artist => "",
            QuickKind.Album => "",
            QuickKind.Playlist => "",
            QuickKind.AllResults => "",
            _ => "",
        };
    }
}
