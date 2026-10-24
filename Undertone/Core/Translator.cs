using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Undertone.Core
{
    /// <summary>
    /// Перевод текстов песен через бесплатный веб-переводчик Google (тот же, что в браузере, без ключа).
    /// Переводим весь текст одним запросом, строки — через перевод строки, чтобы сохранить разбивку.
    /// </summary>
    public static class Translator
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public sealed record Result(string SourceLanguage, List<string> Lines);

        public static async Task<Result> TranslateAsync(IReadOnlyList<string> lines, string target, CancellationToken ct = default)
        {
            var text = string.Join("\n", lines);
            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={target}&dt=t";
            using var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", text) });
            using var response = await Http.PostAsync(url, form, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Parse(json, lines.Count);
        }

        /// <summary>[[["перевод","оригинал",…],…],null,"en",…] → строки перевода по числу исходных.</summary>
        internal static Result Parse(string json, int lineCount)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
                foreach (var seg in root[0].EnumerateArray())
                    if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                        sb.Append(seg[0].GetString());
            var lang = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
                ? root[2].GetString() ?? "" : "";
            var outLines = sb.ToString().Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
            // переводчик иногда склеивает или теряет пустые строки — выравниваем по числу исходных
            while (outLines.Count < lineCount) outLines.Add("");
            if (outLines.Count > lineCount) outLines = outLines.Take(lineCount).ToList();
            return new Result(lang, outLines);
        }
    }
}
