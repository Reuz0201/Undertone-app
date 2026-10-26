using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Undertone.Audio;

namespace Undertone
{
    /// <summary>Эффекты звука: slowed + reverb, speed up, nightcore, 8D — окошко над кнопкой в нижней панели.</summary>
    public partial class MainWindow
    {
        private bool _fxUpdating;

        private SoundEffects Fx => _player.Effects;

        /// <summary>При старте — эффекты из настроек.</summary>
        private void InitEffects()
        {
            Fx.Speed = _settings.FxSpeed;
            Fx.Reverb = _settings.FxReverb;
            Fx.Spin = _settings.FxSpin;

            foreach (var preset in SoundEffects.Presets)
            {
                var p = preset;
                var pill = new RadioButton
                {
                    Style = (Style)FindResource("ChoicePill"),
                    GroupName = "FxPreset",
                    Content = p.Name,
                    Tag = p.Id,
                    ToolTip = p.Hint,
                };
                pill.Checked += (_, _) =>
                {
                    if (_fxUpdating) return;
                    Fx.Apply(p);
                    OnEffectsChanged(announce: true);
                };
                FxPresets.Children.Add(pill);
            }
            UpdateEffectsUi();
        }

        private void Fx_Click(object sender, RoutedEventArgs e) => ToggleFxPopup();

        private void ToggleFxPopup()
        {
            UpdateEffectsUi();
            FxPopup.IsOpen = !FxPopup.IsOpen;
        }

        private void FxSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_fxUpdating || !IsLoaded) return;
            // липнем к 1× и к ровным шагам — так проще попасть в «как было»
            var v = Math.Round(e.NewValue / 0.01) * 0.01;
            if (Math.Abs(v - 1) < 0.025) v = 1;
            Fx.Speed = (float)v;
            OnEffectsChanged(announce: false);
        }

        private void FxSpeed_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            Fx.Speed = 1f;
            OnEffectsChanged(announce: false);
        }

        private void FxReverb_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_fxUpdating || !IsLoaded) return;
            Fx.Reverb = e.NewValue < 0.02 ? 0f : (float)e.NewValue;
            OnEffectsChanged(announce: false);
        }

        private void FxSpin_Changed(object sender, RoutedEventArgs e)
        {
            if (_fxUpdating || !IsLoaded) return;
            Fx.Spin = FxSpinToggle.IsChecked == true;
            OnEffectsChanged(announce: false);
        }

        private void FxReset_Click(object sender, MouseButtonEventArgs e)
        {
            Fx.Apply(SoundEffects.Presets[0]);
            OnEffectsChanged(announce: true);
        }

        private void OnEffectsChanged(bool announce)
        {
            _settings.FxSpeed = Fx.Speed;
            _settings.FxReverb = Fx.Reverb;
            _settings.FxSpin = Fx.Spin;
            PersistSettings();
            UpdateEffectsUi();
            if (announce && !FxPopup.IsOpen)
                ShowToast(Fx.CurrentPreset() is { Id: not "off" } p ? $"Эффект: {p.Name}" : "Эффекты выключены");
        }

        /// <summary>Вход/выход из комнаты: в комнате скорость всегда 1×, иначе разъедемся с друзьями.</summary>
        private void ApplyEffectsRoomLock()
        {
            Fx.LockSpeed = _inRoom;
            UpdateEffectsUi();
        }

        private void UpdateEffectsUi()
        {
            _fxUpdating = true;
            try
            {
                var preset = Fx.CurrentPreset();
                foreach (var pill in FxPresets.Children.OfType<RadioButton>())
                    pill.IsChecked = preset != null && (string)pill.Tag == preset.Id;

                FxSpeedSlider.Value = Fx.Speed;
                FxSpeedSlider.IsEnabled = !Fx.LockSpeed;
                FxSpeedText.Text = Fx.LockSpeed
                    ? "1× в комнате"
                    : Fx.Speed.ToString("0.00", CultureInfo.InvariantCulture) + "×";
                FxReverbSlider.Value = Fx.Reverb;
                FxReverbText.Text = Fx.Reverb <= 0.001f ? "нет" : $"{Math.Round(Fx.Reverb * 100)}%";
                FxSpinToggle.IsChecked = Fx.Spin;

                FxHint.Text = _player.IsExternal
                    ? "Spotify играет своим плеером — эффекты на него не действуют. Работают на Яндексе, SoundCloud и своих файлах."
                    : Fx.LockSpeed
                    ? "Ты в комнате с друзьями — скорость 1×, чтобы музыка шла у всех одинаково. Эхо и 8D работают."
                    : preset != null ? preset.Hint : "Свои настройки";
                FxReset.Visibility = Fx.IsActive ? Visibility.Visible : Visibility.Collapsed;

                var active = Fx.IsActive;
                FxDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                FxBtn.ToolTip = active
                    ? $"Эффекты: {preset?.Name ?? "свои"} (E)"
                    : "Эффекты: slowed + reverb, speed up, 8D (E)";
            }
            finally
            {
                _fxUpdating = false;
            }
        }
    }
}
