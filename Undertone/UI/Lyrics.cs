using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Undertone.UI
{
    /// <summary>Строка текста песни. Time == null — текст без таймкодов.</summary>
    public sealed class LyricLine : INotifyPropertyChanged
    {
        private bool _isActive;
        private bool _isPast;

        public LyricLine(TimeSpan? time, string text)
        {
            Time = time;
            Text = text;
        }

        public TimeSpan? Time { get; }
        public string Text { get; }
        public bool IsPlain => Time == null;

        public bool IsActive
        {
            get => _isActive;
            set { if (_isActive != value) { _isActive = value; OnChanged(); } }
        }

        public bool IsPast
        {
            get => _isPast;
            set { if (_isPast != value) { _isPast = value; OnChanged(); } }
        }

        private string _translation = "";

        /// <summary>Перевод строки (пусто — не переводили или переводить нечего).</summary>
        public string Translation
        {
            get => _translation;
            set { if (_translation != value) { _translation = value; OnChanged(); OnChanged(nameof(HasTranslation)); } }
        }

        public bool HasTranslation => _translation.Length > 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class LyricsParser
    {
        // [01:23.45] или [01:23] или [1:23.456]; в строке может быть несколько меток
        private static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly Regex Tag = new(@"^\[[a-zA-Z]+:.*\]$", RegexOptions.Compiled);

        /// <summary>LRC → строки по времени. Пустые строки (проигрыши) превращаются в «♪».</summary>
        public static List<LyricLine> ParseLrc(string lrc)
        {
            var result = new List<(TimeSpan Time, string Text)>();
            foreach (var raw in lrc.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || Tag.IsMatch(line)) continue;
                var matches = Stamp.Matches(line);
                if (matches.Count == 0) continue;
                var text = Stamp.Replace(line, "").Trim();
                if (text.Length == 0) text = "♪";
                foreach (Match m in matches)
                {
                    var min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    var sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    var frac = m.Groups[3].Success ? m.Groups[3].Value : "0";
                    var ms = int.Parse(frac.PadRight(3, '0').Substring(0, 3), CultureInfo.InvariantCulture);
                    result.Add((new TimeSpan(0, 0, min, sec, ms), text));
                }
            }
            // подряд идущие «♪» схлопываем
            var lines = new List<LyricLine>();
            foreach (var (time, text) in result.OrderBy(r => r.Time))
            {
                if (text == "♪" && lines.Count > 0 && lines[^1].Text == "♪") continue;
                lines.Add(new LyricLine(time, text));
            }
            return lines;
        }

        public static List<LyricLine> ParsePlain(string text) =>
            text.Replace("\r", "").Split('\n').Select(l => new LyricLine(null, l.Length == 0 ? " " : l)).ToList();

        /// <summary>Индекс строки, которая звучит в момент position (последняя с временем ≤ position), или -1.</summary>
        public static int ActiveIndex(IReadOnlyList<LyricLine> lines, TimeSpan position)
        {
            int lo = 0, hi = lines.Count - 1, found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var t = lines[mid].Time;
                if (t == null) return -1;
                if (t.Value <= position) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }
    }
}
