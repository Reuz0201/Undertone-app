using System.ComponentModel;
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
        public bool Available => Track.Available;
        public string Duration => Format.Time(Track.Duration);

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

        public bool IsLiked
        {
            get => _isLiked;
            set { if (_isLiked != value) { _isLiked = value; OnChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class Format
    {
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
