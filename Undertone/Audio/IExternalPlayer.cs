using System;

namespace Undertone.Audio
{
    /// <summary>
    /// Плеер, который играет звук сам (Spotify: Web Playback SDK или приложение через Connect).
    /// Движок только передаёт ему команды — эквалайзер, эффекты, кроссфейд и визуализатор к нему не применяются.
    /// События приходят в UI-потоке.
    /// </summary>
    public interface IExternalPlayer
    {
        bool IsPlaying { get; }
        TimeSpan Position { get; }
        TimeSpan? Duration { get; }
        void Play();
        void Pause();
        void Stop();
        void Seek(TimeSpan position);
        /// <summary>0..1, уже с кривой громкости.</summary>
        void SetVolume(double volume);
        event EventHandler? Ended;
        event EventHandler<Exception>? Failed;
    }
}
