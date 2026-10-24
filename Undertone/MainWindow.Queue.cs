using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Управление очередью «Далее»: перетаскивание, удаление, очистка, «добавить в конец».</summary>
    public partial class MainWindow
    {
        private Point _dragStart;
        private TrackItem? _dragCandidate;
        private DateTime _suppressRowClickUntil;

        private bool RowClickSuppressed => DateTime.UtcNow < _suppressRowClickUntil;

        private static TrackItem? RowAt(object? source)
        {
            for (var d = source as DependencyObject; d != null; d = ParentOf(d))
            {
                if (d is ListBoxItem { DataContext: TrackItem item }) return item;
                if (d is ListBox) return null;
            }
            return null;
        }

        private void UpNext_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // сердечко и ссылки внутри строки — не перетаскивание
            if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) { _dragCandidate = null; return; }
            _dragStart = e.GetPosition(UpNextList);
            _dragCandidate = RowAt(e.OriginalSource);
        }

        private void UpNext_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(UpNextList) - _dragStart;
            if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2 &&
                Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;

            var item = _dragCandidate;
            _dragCandidate = null;
            try
            {
                DragDrop.DoDragDrop(UpNextList, new DataObject(typeof(TrackItem), item), DragDropEffects.Move);
            }
            finally
            {
                // отпускание мыши после перетаскивания не должно включать трек
                _suppressRowClickUntil = DateTime.UtcNow.AddMilliseconds(400);
            }
        }

        private void UpNext_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(TrackItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void UpNext_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(TrackItem)) is not TrackItem dragged) return;
            var from = _upNextItems.IndexOf(dragged);
            var target = RowAt(e.OriginalSource);
            var to = target != null ? _upNextItems.IndexOf(target) : _upNextItems.Count - 1;
            if (from < 0 || to < 0 || from == to) return;
            if (_queue.MoveUpcoming(from, to)) QueueEdited();
        }

        private void UpNextClear_Click(object sender, RoutedEventArgs e)
        {
            if (_queue.Remaining == 0) return;
            _queue.ClearUpcoming();
            QueueEdited();
            ShowToast(_queue.Source == QueueSource.Wave
                ? "Очередь очищена — волна подберёт новые треки"
                : "Очередь очищена: после этого трека музыка остановится");
            if (_queue.Source == QueueSource.Wave) _ = EnsureWaveBufferAsync(force: true);
        }

        private void RemoveFromQueue(TrackItem row)
        {
            var offset = _upNextItems.IndexOf(row);
            if (offset < 0 || !_queue.RemoveUpcomingAt(offset)) return;
            QueueEdited();
        }

        private void MoveToNext(TrackItem row)
        {
            var offset = _upNextItems.IndexOf(row);
            if (offset <= 0 || !_queue.MoveUpcoming(offset, 0)) return;
            QueueEdited();
        }

        private void AddToQueueEnd(YaTrack track)
        {
            if (_queue.Current == null)
            {
                PlayNext(track);
                return;
            }
            if (RoomSuggestInstead(track)) return;
            _queue.AddToEnd(track);
            QueueEdited();
            ShowToast($"«{track.FullTitle}» добавлен в конец очереди");
        }

        /// <summary>После любой правки очереди: переход к следующему мог устареть, предзагрузка — тоже.</summary>
        private void QueueEdited()
        {
            InvalidatePreparedNext();
            UpdateUpNext();
            _ = PrefetchNextAsync();
            SaveSession();
            RoomPublishLocal();
        }

        private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            for (var cur = d; cur != null; cur = ParentOf(cur))
                if (cur is T t) return t;
            return null;
        }

        /// <summary>Родитель в визуальном дереве; у текстовых элементов (Run) — в логическом.</summary>
        private static DependencyObject? ParentOf(DependencyObject d) =>
            d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
    }
}
