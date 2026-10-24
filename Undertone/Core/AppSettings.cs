using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Undertone.Core
{
    public enum AudioQuality { Economy, High, Lossless }

    /// <summary>Настройки приложения — %LOCALAPPDATA%\Undertone\settings.json.</summary>
    public sealed class AppSettings
    {
        // ── интерфейс и плеер ──
        public double Volume { get; set; } = 0.7;
        public bool Muted { get; set; }
        public bool Shuffle { get; set; }
        public RepeatMode Repeat { get; set; } = RepeatMode.Off;
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
        public bool TrayHintShown { get; set; }
        public int VisualizerMode { get; set; }
        /// <summary>Живой фон из цветов обложки на экране «Сейчас играет».</summary>
        public bool LiveBackground { get; set; } = true;
        /// <summary>Показывать перевод текста песен на русский.</summary>
        public bool TranslateLyrics { get; set; }

        // ── страница «Настройки» ──
        /// <summary>Лимит кэша аудио в мегабайтах.</summary>
        public int CacheLimitMb { get; set; } = 2048;
        /// <summary>true — крестик сворачивает в трей, false — закрывает приложение.</summary>
        public bool CloseToTray { get; set; } = true;
        /// <summary>Сколько следующих треков качать заранее.</summary>
        public int PrefetchCount { get; set; } = 2;
        /// <summary>Начинать играть, не дожидаясь полной загрузки трека.</summary>
        public bool StreamPlayback { get; set; } = true;
        /// <summary>После перезапуска предлагать продолжить с того же трека и места.</summary>
        public bool ResumeSession { get; set; } = true;
        /// <summary>id устройства вывода; null — системное по умолчанию.</summary>
        public string? OutputDeviceId { get; set; }
        public AudioQuality Quality { get; set; } = AudioQuality.High;
        /// <summary>Переход между треками: -1 — обычный (с паузой), 0 — без паузы, N — кроссфейд N секунд.</summary>
        public int CrossfadeSeconds { get; set; } = -1;
        /// <summary>Выравнивать громкость треков (если Яндекс сообщает громкость трека).</summary>
        public bool Normalize { get; set; } = true;

        // ── эквалайзер ──
        public bool EqEnabled { get; set; }
        public float[] EqGains { get; set; } = new float[10];

        // ── Моя волна ──
        /// <summary>"" — любое; favorite / discover / popular.</summary>
        public string WaveDiversity { get; set; } = "";
        /// <summary>"" — любое; active / fun / calm / sad.</summary>
        public string WaveMood { get; set; } = "";
        /// <summary>"" — любой; russian / not-russian / without-words.</summary>
        public string WaveLanguage { get; set; } = "";

        // ── интеграции ──
        /// <summary>Отправлять прослушивания в Яндекс (рекомендации и история учитывают Undertone).</summary>
        public bool ReportPlays { get; set; } = true;
        public bool GlobalHotkeys { get; set; } = true;
        /// <summary>Действие → сочетание, например "Like" → "Ctrl+Alt+L".</summary>
        public Dictionary<string, string> Hotkeys { get; set; } = new();
        public bool DiscordEnabled { get; set; }
        /// <summary>Application ID из Discord Developer Portal.</summary>
        public string DiscordAppId { get; set; } = "";

        // ── лента друзей ──
        public bool FriendsEnabled { get; set; }
        /// <summary>Код компании: у кого он одинаковый, те видят друг друга.</summary>
        public string FriendsCode { get; set; } = "";
        /// <summary>Как тебя видят друзья (пусто — имя из Яндекса).</summary>
        public string FriendsName { get; set; } = "";
        /// <summary>Случайный id этой установки — чтобы отличать себя от друзей.</summary>
        public string FriendsMemberId { get; set; } = "";

        // ── умные плейлисты (null — ещё не создавали, покажем готовые) ──
        public List<SmartPlaylist>? SmartPlaylists { get; set; }

        // ── пульт с телефона ──
        public bool RemoteEnabled { get; set; }
        /// <summary>Секрет в ссылке хозяина (полное управление).</summary>
        public string RemoteToken { get; set; } = "";
        /// <summary>Секрет в гостевой ссылке (только поиск и «предложить»).</summary>
        public string RemoteGuestToken { get; set; } = "";

        // ── свои файлы ──
        public List<string> LocalFolders { get; set; } = new();

        // ── мини-плеер ──
        public double? MiniLeft { get; set; }
        public double? MiniTop { get; set; }

        public const int MinCacheMb = 256;

        [JsonIgnore]
        public long CacheLimitBytes =>Math.Max(MinCacheMb, CacheLimitMb) * 1024L * 1024L;

        private static string FilePath => Path.Combine(EnvFile.DataDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn("settings.json не прочитан, беру настройки по умолчанию", ex);
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("settings.json не сохранён", ex);
            }
        }
    }
}
