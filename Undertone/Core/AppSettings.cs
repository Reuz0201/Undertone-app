using System;
using System.IO;
using System.Text.Json;

namespace Undertone.Core
{
    /// <summary>Настройки интерфейса (громкость, shuffle, repeat) — %LOCALAPPDATA%\Undertone\settings.json.</summary>
    public sealed class AppSettings
    {
        public double Volume { get; set; } = 0.7;
        public bool Muted { get; set; }
        public bool Shuffle { get; set; }
        public RepeatMode Repeat { get; set; } = RepeatMode.Off;
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Undertone", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
