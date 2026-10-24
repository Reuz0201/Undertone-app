using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Undertone.Core;
using Undertone.UI;

namespace Undertone
{
    /// <summary>Режим вечеринки (F11 или кнопка в панели): полноэкранное окно поверх, главное продолжает играть.</summary>
    public partial class MainWindow
    {
        private PartyWindow? _party;

        private void Party_Click(object sender, RoutedEventArgs e) => ToggleParty();

        private void ToggleParty()
        {
            if (_party != null)
            {
                _party.Close();
                return;
            }
            var party = new PartyWindow();
            party.Resources["LiveAccent"] = _liveAccent;
            party.Resources["LiveAccentSoft"] = _liveAccentSoft;
            // открываем на том же мониторе, где главное окно; дальше Win+Shift+→ перенесёт на телевизор
            party.WindowStartupLocation = WindowStartupLocation.Manual;
            party.Left = Left + 40;
            party.Top = Top + 40;
            party.Closed += (_, _) =>
            {
                _party = null;
                Activate();
            };
            party.KeyPressed += key => _ = PartyKeyAsync(key);
            _party = party;
            party.Show();
            SyncParty(full: true);
            // текст нужен и без открытой панели — для караоке
            _lyricsTrackId = null;
            _ = LoadLyricsAsync();
        }

        private async System.Threading.Tasks.Task PartyKeyAsync(Key key)
        {
            switch (key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Right: await NextAsync(auto: false); break;
                case Key.Left: await PrevAsync(); break;
                case Key.L: if (_queue.Current is { } c) await ToggleLikeAsync(c); break;
            }
        }

        /// <summary>Отдать окну вечеринки текущее состояние. full — трек сменился.</summary>
        private void SyncParty(bool full)
        {
            var party = _party;
            if (party == null) return;
            if (full && _queue.Current is { } t)
            {
                party.SetTrack(t, string.IsNullOrEmpty(_queueLabel) ? "СЕЙЧАС ИГРАЕТ" : _queueLabel);
                party.SetPalette(_livePalette, _accentShown);
                party.SetRoom(_inRoom ? "Слушаем вместе: " + PartyRoomNames() : "");
                if (_lyricsTrackId == t.Id) party.SetLyrics(_lyrics);
            }
            var position = _hasMedia ? _player.Position : _resumeAt?.Position ?? TimeSpan.Zero;
            var duration = _player.NaturalDuration ?? _queue.Current?.Duration ?? TimeSpan.Zero;
            party.SetPlayback(position, duration);
        }

        private string PartyRoomNames()
        {
            var others = RoomFriends();
            return others.Count == 0 ? "ты пока один" : "ты, " + Names(others);
        }
    }
}
