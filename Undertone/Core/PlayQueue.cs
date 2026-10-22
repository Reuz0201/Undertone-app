using System;
using System.Collections.Generic;
using System.Linq;

namespace Undertone.Core
{
    public enum RepeatMode { Off, All, One }

    public enum QueueSource { None, Wave, Liked, Search, Collection }

    /// <summary>Очередь воспроизведения: shuffle, repeat, бесконечное дополнение для волны.</summary>
    public sealed class PlayQueue
    {
        private readonly List<YaTrack> _items = new();
        private List<int> _order = new();
        private int _pos = -1;
        private readonly Random _rng = new();

        public QueueSource Source { get; private set; } = QueueSource.None;
        public bool Shuffle { get; private set; }
        public RepeatMode Repeat { get; set; } = RepeatMode.Off;

        public int Count => _items.Count;
        public bool IsEmpty => _items.Count == 0;
        public YaTrack? Current => _pos >= 0 && _pos < _order.Count ? _items[_order[_pos]] : null;
        /// <summary>Сколько треков осталось после текущего (без учёта repeat).</summary>
        public int Remaining => _pos < 0 ? _order.Count : _order.Count - _pos - 1;

        public void Set(IEnumerable<YaTrack> tracks, int startIndex, QueueSource source, bool? shuffle = null)
        {
            _items.Clear();
            _items.AddRange(tracks);
            Source = source;
            if (shuffle.HasValue) Shuffle = shuffle.Value;
            if (Source == QueueSource.Wave) Shuffle = false;
            startIndex = Math.Clamp(startIndex, 0, Math.Max(0, _items.Count - 1));
            BuildOrder(_items.Count == 0 ? -1 : startIndex);
            _pos = _items.Count == 0 ? -1 : _order.IndexOf(startIndex);
        }

        public void Clear()
        {
            _items.Clear();
            _order.Clear();
            _pos = -1;
            Source = QueueSource.None;
        }

        /// <summary>Добавить в конец (волна подгружает следующую пачку).</summary>
        public void Append(IEnumerable<YaTrack> tracks)
        {
            foreach (var t in tracks)
            {
                _items.Add(t);
                _order.Add(_items.Count - 1);
            }
        }

        /// <summary>Вставить сразу после текущего («Играть следующим»).</summary>
        public void InsertNext(YaTrack track)
        {
            _items.Add(track);
            _order.Insert(Math.Max(0, _pos + 1), _items.Count - 1);
            if (_pos < 0) _pos = 0;
        }

        public bool SetShuffle(bool value)
        {
            if (Source == QueueSource.Wave) value = false;
            if (Shuffle == value) return Shuffle;
            Shuffle = value;
            var current = _pos >= 0 && _pos < _order.Count ? _order[_pos] : -1;
            BuildOrder(current);
            _pos = current >= 0 ? _order.IndexOf(current) : -1;
            return Shuffle;
        }

        /// <summary>Следующий трек. auto=true — трек доиграл сам (учитывается RepeatOne).</summary>
        public YaTrack? MoveNext(bool auto)
        {
            if (_order.Count == 0) return null;
            if (auto && Repeat == RepeatMode.One && Current != null) return Current;
            if (_pos + 1 < _order.Count) { _pos++; return Current; }
            if (Repeat != RepeatMode.Off && Source != QueueSource.Wave)
            {
                if (Shuffle) BuildOrder(-1);
                _pos = 0;
                return Current;
            }
            return null;
        }

        public YaTrack? MovePrev()
        {
            if (_order.Count == 0) return null;
            if (_pos > 0) { _pos--; return Current; }
            if (Repeat == RepeatMode.All && Source != QueueSource.Wave) { _pos = _order.Count - 1; return Current; }
            return Current;
        }

        public YaTrack? PeekNext()
        {
            if (_order.Count == 0) return null;
            if (Repeat == RepeatMode.One) return Current;
            if (_pos + 1 < _order.Count) return _items[_order[_pos + 1]];
            if (Repeat == RepeatMode.All && !Shuffle && Source != QueueSource.Wave) return _items[_order[0]];
            return null;
        }

        public IReadOnlyList<YaTrack> Upcoming(int max) =>
            _order.Skip(_pos + 1).Take(max).Select(i => _items[i]).ToList();

        /// <summary>Уже сыгранные (для параметра queue у волны).</summary>
        public IEnumerable<string> PlayedIds => _order.Take(_pos + 1).Select(i => _items[i].LikeId);

        /// <summary>Перепрыгнуть на steps треков вперёд (клик по «Далее»).</summary>
        public YaTrack? SkipAhead(int steps)
        {
            if (steps < 1 || _pos + steps >= _order.Count) return null;
            _pos += steps;
            return Current;
        }

        public void Remove(string trackId)
        {
            for (int p = _order.Count - 1; p > _pos; p--)
                if (_items[_order[p]].Id == trackId) _order.RemoveAt(p);
        }

        private void BuildOrder(int first)
        {
            var idx = Enumerable.Range(0, _items.Count).ToList();
            if (Shuffle)
            {
                for (int i = idx.Count - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    (idx[i], idx[j]) = (idx[j], idx[i]);
                }
                if (first >= 0)
                {
                    idx.Remove(first);
                    idx.Insert(0, first);
                }
                _order = idx;
            }
            else
            {
                _order = idx;
            }
        }
    }
}
