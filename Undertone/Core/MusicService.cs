using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>Откуда играет музыка: у каждого сервиса свой аккаунт и свои токены.</summary>
    public enum MusicServiceKind
    {
        Yandex,
        SoundCloud,
        Spotify,
    }

    public static class MusicServices
    {
        public static readonly MusicServiceKind[] All = { MusicServiceKind.Yandex, MusicServiceKind.SoundCloud, MusicServiceKind.Spotify };

        /// <summary>Ключ для settings.json.</summary>
        public static string Id(this MusicServiceKind kind) => kind switch
        {
            MusicServiceKind.SoundCloud => "soundcloud",
            MusicServiceKind.Spotify => "spotify",
            _ => "yandex",
        };

        public static MusicServiceKind Parse(string? id) => id?.ToLowerInvariant() switch
        {
            "soundcloud" => MusicServiceKind.SoundCloud,
            "spotify" => MusicServiceKind.Spotify,
            _ => MusicServiceKind.Yandex,
        };

        public static string Title(this MusicServiceKind kind) => kind switch
        {
            MusicServiceKind.SoundCloud => "SoundCloud",
            MusicServiceKind.Spotify => "Spotify",
            _ => "Яндекс Музыка",
        };

        /// <summary>«в каталоге …», «удалён из …», «подключиться к …».</summary>
        public static string TitleGenitive(this MusicServiceKind kind) => kind switch
        {
            MusicServiceKind.SoundCloud => "SoundCloud",
            MusicServiceKind.Spotify => "Spotify",
            _ => "Яндекс Музыки",
        };

        /// <summary>Остальные сервисы по порядку — куда переключиться после выхода, где искать сохранённый вход.</summary>
        public static IEnumerable<MusicServiceKind> Others(this MusicServiceKind kind)
        {
            foreach (var k in All)
                if (k != kind) yield return k;
        }

        /// <summary>Сервис трека по его id: у SoundCloud id вида sc-123, у Spotify — sp-… (как local-… у своих файлов).</summary>
        public static MusicServiceKind? Of(YaTrack track) =>
            ScApi.IsScId(track.Id) ? MusicServiceKind.SoundCloud
            : SpotifyApi.IsSpId(track.Id) ? MusicServiceKind.Spotify
            : LocalLibrary.IsLocal(track) ? null
            : MusicServiceKind.Yandex;
    }

    /// <summary>
    /// Музыкальный сервис глазами окна. Модели общие (YaTrack, YaPlaylist…) — исторически названы по Яндексу.
    /// Чего сервис не умеет (Моя волна, дизлайк, lossless, тексты) — смотри Supports*: окно прячет эти кнопки.
    /// </summary>
    public interface IMusicService : IDisposable
    {
        MusicServiceKind Kind { get; }
        bool SupportsWave { get; }
        bool SupportsDislike { get; }
        bool SupportsLossless { get; }
        bool SupportsLyrics { get; }
        bool SupportsAlbums { get; }

        /// <summary>
        /// Звук можно скачать в кэш (и, значит, играть через свой движок: эквалайзер, эффекты, кроссфейд, офлайн).
        /// false — сервис играет сам (Spotify: только своим плеером, файлы не отдаёт).
        /// </summary>
        bool SupportsDownload => true;

        /// <summary>id аккаунта для локальных хранилищ (кэш библиотеки, история, сессия). У SoundCloud отрицательный — не пересекается с Яндексом.</summary>
        long Uid { get; }

        Task<YaAccount> GetAccountAsync(CancellationToken ct = default);
        Task<string?> GetAvatarUrlAsync(CancellationToken ct = default);

        Task<(int Revision, List<string> Ids)> GetLikedIdsAsync(CancellationToken ct = default);
        Task<List<YaTrack>> GetTracksAsync(IReadOnlyList<string> ids, CancellationToken ct = default);
        Task<YaSearchResult> SearchAsync(string query, CancellationToken ct = default);
        Task<List<YaTrack>> SearchTracksAsync(string query, int page = 0, CancellationToken ct = default);
        Task SetLikeAsync(YaTrack track, bool like, CancellationToken ct = default);
        Task DislikeAsync(YaTrack track, CancellationToken ct = default);

        /// <summary>Откуда качать звук трека (mp3). lowest — экономный битрейт, если сервис такой даёт.</summary>
        Task<CacheSource> ResolveAudioAsync(YaTrack track, bool lowest, CancellationToken ct = default);
        Task<YaFileInfo> GetFileInfoAsync(string trackId, string quality, CancellationToken ct = default);
        Task<string?> GetLyricsAsync(string trackId, bool sync, CancellationToken ct = default);
        Task ReportPlayAsync(YaTrack track, double playedSeconds, double endPositionSeconds,
            string playId, string? playlistId = null, CancellationToken ct = default);
        Task<List<YaTrack>> GetSimilarTracksAsync(string trackId, CancellationToken ct = default);

        Task<YaArtistPage> GetArtistAsync(string artistId, CancellationToken ct = default);
        Task<(YaAlbum Album, List<YaTrack> Tracks)> GetAlbumAsync(string albumId, CancellationToken ct = default);

        Task<List<YaPlaylist>> GetPlaylistsAsync(CancellationToken ct = default);
        Task<(YaPlaylist Playlist, List<YaTrack> Tracks)> GetPlaylistAsync(string kind, CancellationToken ct = default);
        Task<(YaPlaylist Playlist, List<YaTrack> Tracks)> GetPlaylistAsync(long ownerUid, string kind, CancellationToken ct = default);
        Task<YaPlaylist> CreatePlaylistAsync(string title, CancellationToken ct = default);
        Task<YaPlaylist> RenamePlaylistAsync(string kind, string title, CancellationToken ct = default);
        Task DeletePlaylistAsync(string kind, CancellationToken ct = default);
        Task<YaPlaylist> AddToPlaylistAsync(YaPlaylist playlist, YaTrack track, CancellationToken ct = default);
        Task<YaPlaylist> RemoveTrackFromPlaylistAsync(string kind, string trackId, int hintIndex, CancellationToken ct = default);

        Task<WaveBatch> StartWaveAsync(IEnumerable<string> seeds, CancellationToken ct = default);
        Task<WaveBatch> GetWaveTracksAsync(string sessionId, IEnumerable<string> queue, CancellationToken ct = default);
        Task WaveFeedbackAsync(string sessionId, string? batchId, string type,
            string? trackId = null, double? playedSeconds = null, CancellationToken ct = default);
    }

    /// <summary>
    /// Ссылки на обложки: у Яндекса шаблон без схемы с «%%» → «200x200», у SoundCloud полный URL с «%%» → «t300x300»,
    /// у своих файлов — file:… как есть.
    /// </summary>
    public static class CoverUris
    {
        public static string? Resolve(string? uri, int size)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            if (uri.StartsWith("file:", StringComparison.Ordinal)) return uri;
            if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return uri.Replace("%%", SoundCloudSize(size));
            return "https://" + uri.Replace("%%", $"{size}x{size}");
        }

        /// <summary>Ближайший больший размер артворка SoundCloud.</summary>
        internal static string SoundCloudSize(int size) => size switch
        {
            <= 67 => "t67x67",
            <= 100 => "large",
            <= 300 => "t300x300",
            _ => "t500x500",
        };
    }
}
