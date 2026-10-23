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
            _nextRound = null;
            BuildOrder(_items.Count == 0 ? -1 : startIndex);
            _pos = _items.Count == 0 ? -1 : _order.IndexOf(startIndex);
        }

        public void Clear()
        {
            _items.Clear();
            _order.Clear();
            _nextRound = null;
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
            _nextRound = null;
        }

        /// <summary>Вставить сразу после текущего («Играть следующим»).</summary>
        public void InsertNext(YaTrack track)
        {
            _items.Add(track);
            _order.Insert(Math.Max(0, _pos + 1), _items.Count - 1);
            if (_pos < 0) _pos = 0;
            _nextRound = null;
        }

        public bool SetShuffle(bool value)
        {
            if (Source == QueueSource.Wave) value = false;
            if (Shuffle == value) return Shuffle;
            Shuffle = value;
            _nextRound = null;
            var current = _pos >= 0 && _pos < _order.Count ? _order[_pos] : -1;
            BuildOrder(current);
            _pos = current >= 0 ? _order.IndexOf(current) : -1;
            return Shuffle;
        }

        /// <summary>
        /// Порядок следующего круга при «повторе списка» с перемешиванием. Строится заранее,
        /// чтобы PeekNext/PeekAhead знали, какой трек будет после последнего (и предзагрузка работала на стыке).
        /// </summary>
        private List<int>? _nextRound;

        private bool WrapsAround => Repeat != RepeatMode.Off && Source != QueueSource.Wave;

        /// <summary>Следующий трек. auto=true — трек доиграл сам (учитывается RepeatOne).</summary>
        public YaTrack? MoveNext(bool auto)
        {
            if (_order.Count == 0) return null;
            if (auto && Repeat == RepeatMode.One && Current != null) return Current;
            if (_pos + 1 < _order.Count) { _pos++; return Current; }
            if (WrapsAround)
            {
                if (Shuffle) _order = NextRound();
                _nextRound = null;
                _pos = 0;
                return Current;
            }
            return null;
        }

        private List<int> NextRound()
        {
            if (_nextRound != null && _nextRound.Count == _items.Count) return _nextRound;
            var idx = Enumerable.Range(0, _items.Count).ToList();
            for (int i = idx.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (idx[i], idx[j]) = (idx[j], idx[i]);
            }
            // не начинать новый круг тем же треком, которым закончился прошлый
            var last = _order.Count > 0 ? _order[^1] : -1;
            if (idx.Count > 1 && idx[0] == last) (idx[0], idx[1]) = (idx[1], idx[0]);
            return _nextRound = idx;
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
            if (WrapsAround) return _items[(Shuffle ? NextRound() : _order)[0]];
            return null;
        }

        /// <summary>
        /// До count треков, которые пойдут после текущего при переключении вперёд (как по кнопке «Следующий»).
        /// Учитывает переход на новый круг при «повторе списка». Для предзагрузки.
        /// </summary>
        public IReadOnlyList<YaTrack> PeekAhead(int count)
        {
            var result = new List<YaTrack>();
            if (_order.Count == 0 || count <= 0) return result;
            for (int p = _pos + 1; p < _order.Count && result.Count < count; p++)
                result.Add(_items[_order[p]]);
            if (result.Count < count && WrapsAround)
            {
                var round = Shuffle ? NextRound() : _order;
                for (int p = 0; p < round.Count && result.Count < count; p++)
                {
                    var t = _items[round[p]];
                    if (!ReferenceEquals(t, Current)) result.Add(t);
                }
            }
            return result;
        }

        /// <summary>
        /// Кусок очереди в порядке воспроизведения вокруг текущего трека — для сохранения сессии.
        /// Index — позиция текущего трека внутри возвращённого списка.
        /// </summary>
        public (List<YaTrack> Tracks, int Index) Snapshot(int before, int after)
        {
            if (_pos < 0 || _order.Count == 0) return (new List<YaTrack>(), -1);
            var from = Math.Max(0, _pos - before);
            var to = Math.Min(_order.Count - 1, _pos + after);
            var list = new List<YaTrack>(to - from + 1);
            for (int p = from; p <= to; p++) list.Add(_items[_order[p]]);
            return (list, _pos - from);
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

        // ────────── правка очереди «Далее» (offset 0 — трек сразу после текущего) ──────────

        public bool RemoveUpcomingAt(int offset)
        {
            var p = _pos + 1 + offset;
            if (offset < 0 || p >= _order.Count) return false;
            _order.RemoveAt(p);
            _nextRound = null;
            return true;
        }

        public bool MoveUpcoming(int from, int to)
        {
            var count = _order.Count - _pos - 1;
            if (from < 0 || to < 0 || from >= count || to >= count || from == to) return false;
            var item = _order[_pos + 1 + from];
            _order.RemoveAt(_pos + 1 + from);
            _order.Insert(_pos + 1 + to, item);
            _nextRound = null;
            return true;
        }

        /// <summary>Убирает всё после текущего трека.</summary>
        public void ClearUpcoming()
        {
            if (_pos + 1 < _order.Count) _order.RemoveRange(_pos + 1, _order.Count - _pos - 1);
            _nextRound = null;
        }

        /// <summary>Добавить трек в конец очереди («Добавить в очередь»).</summary>
        public void AddToEnd(YaTrack track)
        {
            _items.Add(track);
            _order.Add(_items.Count - 1);
            if (_pos < 0) _pos = 0;
            _nextRound = null;
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
