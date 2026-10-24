using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Undertone.Core;

namespace Undertone.UI
{
    /// <summary>Строка трека в списках (Мне нравится / Поиск / Далее).</summary>
    public sealed class TrackItem : INotifyPropertyChanged
    {
        private int _index;
        private bool _isPlaying;
        private bool _isLiked;

        public TrackItem(YaTrack track, int index)
        {
            Track = track;
            _index = index;
        }

        public YaTrack Track { get; }

        public string Title => Track.FullTitle;
        public string Artists => Track.ArtistsText;
        public string Album => Track.Album;
        public string? Cover => Track.CoverUrl(100);
        /// <summary>Крупная обложка для карточек на полках.</summary>
        public string? CoverLarge => Track.CoverUrl(300);
        public bool Available => Track.Available;
        public string Duration => Format.Time(Track.Duration);
        public IReadOnlyList<ArtistLink> ArtistLinks => ArtistLink.From(Track.ArtistRefs);

        public int Index
        {
            get => _index;
            set { if (_index != value) { _index = value; OnChanged(); } }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set { if (_isPlaying != value) { _isPlaying = value; OnChanged(); } }
        }

        /// <summary>В комнате: кто поставил трек в очередь («Федуня»).</summary>
        public string AddedBy { get; init; } = "";
        public string AddedText => AddedBy.Length > 0 ? $" · добавил {AddedBy}" : "";

        public bool IsLiked
        {
            get => _isLiked;
            set { if (_isLiked != value) { _isLiked = value; OnChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Имя артиста-ссылка + запятая после (кроме последнего).</summary>
    public sealed record ArtistLink(string Id, string Name, string Separator)
    {
        public static IReadOnlyList<ArtistLink> From(IReadOnlyList<YaArtistRef> refs) =>
            refs.Select((a, i) => new ArtistLink(a.Id, a.Name, i < refs.Count - 1 ? ", " : "")).ToList();
    }

    public sealed class PlaylistItem
    {
        public PlaylistItem(YaPlaylist playlist) => Playlist = playlist;
        public YaPlaylist Playlist { get; }
        public string Title => Playlist.Title;
        public string Sub => $"{Playlist.TrackCount} {Format.Plural(Playlist.TrackCount, "трек", "трека", "треков")}";
        public string? Cover => Playlist.CoverUrl(100);
    }

    public sealed class AlbumItem
    {
        public AlbumItem(YaAlbum album) => Album = album;
        public YaAlbum Album { get; }
        public string Title => Album.Title;
        public string Sub
        {
            get
            {
                var kind = Album.Type switch { "single" => "Сингл", "compilation" => "Сборник", "podcast" => "Подкаст", _ => "Альбом" };
                return Album.Year > 0 ? $"{Album.Year} · {kind}" : kind;
            }
        }
        public string? Cover => Album.CoverUrl(300);
    }

    /// <summary>Карточка друга на странице «Друзья».</summary>
    public sealed class FriendItem
    {
        public FriendItem(FriendPresence p)
        {
            Presence = p;
            var ago = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(p.Ts);
            // «слушает сейчас» — играет и последний сигнал свежий (статус шлётся раз в минуту)
            IsLive = p.Online && p.Playing && ago < TimeSpan.FromMinutes(3);
            StatusText = IsLive ? "слушает сейчас"
                : p.Online && ago < TimeSpan.FromMinutes(3) ? "на паузе"
                : "был " + Ago(ago);
        }

        public FriendPresence Presence { get; }
        public string Name => Format.PublicName(Presence.Name);
        public string Initial => Name.Substring(0, 1).ToUpperInvariant();
        public string Title => Presence.Title.Length > 0 ? Presence.Title : "Пока ничего не слушал";
        public string Artists => Presence.Artists;
        public string? Cover => Presence.Cover;
        public bool HasTrack => Presence.TrackId.Length > 0;
        public bool IsLive { get; }
        public string StatusText { get; }

        private static string Ago(TimeSpan t)
        {
            if (t < TimeSpan.FromMinutes(1)) return "только что";
            if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes} мин назад";
            if (t < TimeSpan.FromDays(1)) return $"{(int)t.TotalHours} ч назад";
            var d = (int)t.TotalDays;
            return $"{d} {Format.Plural(d, "день", "дня", "дней")} назад";
        }
    }

    public enum SearchCardKind { Artist, Album, Playlist }

    /// <summary>Карточка в результатах поиска: артист, альбом или плейлист.</summary>
    public sealed class SearchCard
    {
        public SearchCardKind Kind { get; init; }
        public string Id { get; init; } = "";
        public long OwnerUid { get; init; }
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public string? Cover { get; init; }
        public bool IsRound => Kind == SearchCardKind.Artist;
        public string Glyph => Kind switch
        {
            SearchCardKind.Artist => "",
            SearchCardKind.Album => "",
            _ => "",
        };

        public static SearchCard From(YaArtist a) => new()
        {
            Kind = SearchCardKind.Artist,
            Id = a.Id,
            Title = a.Name,
            Sub = a.Genres.Length > 0 ? string.Join(", ", a.Genres.Take(2)) : "Артист",
            Cover = a.CoverUrl(300),
        };

        public static SearchCard From(YaAlbum a) => new()
        {
            Kind = SearchCardKind.Album,
            Id = a.Id,
            Title = a.Title,
            Sub = string.Join(" · ", new[] { a.ArtistsText, a.Year > 0 ? a.Year.ToString() : "" }.Where(x => x.Length > 0)),
            Cover = a.CoverUrl(300),
        };

        public static SearchCard From(YaPlaylist p) => new()
        {
            Kind = SearchCardKind.Playlist,
            Id = p.Kind,
            OwnerUid = p.OwnerUid,
            Title = p.Title,
            Sub = string.Join(" · ", new[]
            {
                p.OwnerName,
                p.TrackCount > 0 ? $"{p.TrackCount} {Format.Plural(p.TrackCount, "трек", "трека", "треков")}" : "",
            }.Where(x => x.Length > 0)),
            Cover = p.CoverUrl(300),
        };
    }

    public static class Format
    {
        /// <summary>Имя для друзей: логин бывает почтой — показываем только часть до «@».</summary>
        public static string PublicName(string? name)
        {
            name = (name ?? "").Trim();
            var at = name.IndexOf('@');
            if (at > 0) name = name[..at];
            return name.Length > 0 ? name : "Друг";
        }

        public static string Time(System.TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        public static string Plural(int n, string one, string few, string many)
        {
            var m10 = n % 10;
            var m100 = n % 100;
            if (m10 == 1 && m100 != 11) return one;
            if (m10 is >= 2 and <= 4 && (m100 < 10 || m100 >= 20)) return few;
            return many;
        }

        public static string Total(System.TimeSpan t)
        {
            var h = (int)t.TotalHours;
            return h > 0 ? $"{h} ч {t.Minutes} мин" : $"{t.Minutes} мин";
        }
    }
}
