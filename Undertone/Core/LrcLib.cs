using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Тексты песен из LRCLIB (lrclib.net) — открытой базы текстов с таймкодами, без ключа.
    /// Нужна там, где сервис текста не даёт: Spotify сторонним приложениям тексты закрыл, у своих файлов
    /// текста нет вовсе, а у Яндекса он есть не у всех треков.
    /// </summary>
    public static class LrcLib
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12), BaseAddress = new Uri("https://lrclib.net/") };
            // LRCLIB просит представляться: название приложения и где его найти
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Undertone (https://github.com/Reuz0201/Undertone-app)");
            return http;
        }

        public sealed record Found(bool Synced, string Text);

        /// <summary>Текст трека: с таймкодами, если есть, иначе обычный. null — не нашёлся.</summary>
        public static async Task<Found?> FindAsync(YaTrack track, CancellationToken ct = default)
        {
            var artist = track.Artists.FirstOrDefault() ?? "";
            var title = CleanTitle(track.Title);
            if (artist.Length == 0 || title.Length == 0) return null;
            var seconds = (int)Math.Round(track.DurationMs / 1000.0);

            // точный запрос: исполнитель + название + длительность (LRCLIB сам допускает ±2 с)
            var exact = $"api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}" +
                        (seconds > 0 ? $"&duration={seconds}" : "");
            if (Pick(await GetAsync(exact, ct), seconds) is { } hit) return hit;

            // поиск: другое написание, «feat.» в названии — берём ближайший по длительности, с таймкодами в приоритете
            var search = $"api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            return Pick(await GetAsync(search, ct), seconds);
        }

        /// <summary>JSON ответа или null (404 — нет такого). На «сервер занят» один повтор через секунду.</summary>
        private static async Task<string?> GetAsync(string path, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var response = await Http.GetAsync(path, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt == 0)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            return null;
        }

        /// <summary>
        /// Из ответа (одна запись или массив результатов поиска) — лучший текст: длительность совпадает
        /// (±4 с, если известна), синхронный лучше обычного, инструментал без текста пропускаем.
        /// </summary>
        internal static Found? Pick(string? json, int seconds)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root };

            Found? plain = null;
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("instrumental", out var instr) && instr.ValueKind == JsonValueKind.True) continue;
                if (seconds > 0 && item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number &&
                    Math.Abs(d.GetDouble() - seconds) > 4)
                    continue;
                if (Text(item, "syncedLyrics") is { } synced) return new Found(true, synced);
                if (plain == null && Text(item, "plainLyrics") is { } text) plain = new Found(false, text);
            }
            return plain;
        }

        private static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;

        /// <summary>
        /// Название без приписок, которых в базе обычно нет: «(feat. …)», «(with …)», «- Remastered 2011»,
        /// «(Radio Edit)». «(Slowed)», «(Remix)» и т. п. оставляем — это другая версия с другой длительностью.
        /// </summary>
        internal static string CleanTitle(string title)
        {
            var t = Regex.Replace(title, @"\s*[\(\[](feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s+-\s+(\d{4}\s+)?(remaster(ed)?|radio edit|single version|mono|stereo)(\s+\d{4})?(\s+version)?\s*$", "",
                RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s*[\(\[](radio edit|remaster(ed)?(\s+\d{4})?|\d{4}\s+remaster(ed)?)[\)\]]", "", RegexOptions.IgnoreCase);
            return t.Trim();
        }
    }
}
