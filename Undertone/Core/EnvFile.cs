using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Undertone.Core
{
    /// <summary>
    /// Чтение и запись .env в корне проекта.
    /// Понимает оба формата: <c>key: value</c> (как пишет main.py) и <c>key=value</c>.
    /// При записи сохраняет комментарии, порядок строк и разделитель каждой строки.
    /// </summary>
    public sealed class EnvFile
    {
        public const string FileName = ".env";

        private readonly List<string> _lines = new();
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string FilePath { get; }
        public bool Exists => File.Exists(FilePath);

        private EnvFile(string path)
        {
            FilePath = path;
            Reload();
        }

        /// <summary>
        /// Из исходников: корень = папка, где лежит папка проекта Undertone (там же start.bat, main.py).
        /// Релизная сборка: .env рядом с Undertone.exe. Переопределение — переменная YAMUSIC_ENV.
        /// </summary>
        public static EnvFile Locate()
        {
            var overridePath = Environment.GetEnvironmentVariable("YAMUSIC_ENV");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return new EnvFile(Path.GetFullPath(overridePath));

            var baseDir = AppContext.BaseDirectory;

            // запуск из исходников (start.bat → bin\Release\...) — .env в корне проекта
            var root = FindProjectRoot(baseDir);
            if (root != null)
                return new EnvFile(Path.Combine(root, FileName));

            // релизная сборка — .env строго рядом с exe (не ищем по родительским папкам,
            // чтобы не подхватить чужой .env)
            return new EnvFile(Path.Combine(baseDir, FileName));
        }

        /// <summary>true, если exe лежит внутри папки с исходниками (собран через start.bat).</summary>
        public static bool IsRunningFromSource => FindProjectRoot(AppContext.BaseDirectory) != null;

        public static EnvFile Open(string path) => new(Path.GetFullPath(path));

        private static string? FindProjectRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                // корень: <root>/Undertone/Undertone.csproj
                if (File.Exists(Path.Combine(dir.FullName, "Undertone", "Undertone.csproj")))
                    return dir.FullName;
                // мы внутри папки проекта: корень — её родитель
                if (File.Exists(Path.Combine(dir.FullName, "Undertone.csproj")) && dir.Parent != null)
                    return dir.Parent.FullName;
            }
            return null;
        }

        public void Reload()
        {
            _lines.Clear();
            _values.Clear();
            if (!File.Exists(FilePath)) return;

            _lines.AddRange(File.ReadAllLines(FilePath, Encoding.UTF8));
            foreach (var line in _lines)
            {
                var parsed = ParseLine(line);
                if (parsed != null) _values[parsed.Value.Key] = parsed.Value.Value;
            }
        }

        public string? Get(string key) =>
            _values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        public IReadOnlyDictionary<string, string> All => _values;

        /// <summary>Обновляет/добавляет ключи и сразу пишет файл.</summary>
        public void Set(IReadOnlyDictionary<string, string?> values)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _lines.Count; i++)
            {
                var parsed = ParseLine(_lines[i]);
                if (parsed == null) continue;
                var (key, _, sep) = parsed.Value;
                if (!values.TryGetValue(key, out var newValue)) continue;
                _lines[i] = Format(key, newValue ?? "", sep);
                seen.Add(key);
            }
            foreach (var (key, value) in values)
                if (!seen.Contains(key))
                    _lines.Add(Format(key, value ?? "", ':'));

            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, string.Join("\n", _lines) + "\n", new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
            Reload();
        }

        private static string Format(string key, string value, char sep) =>
            sep == '=' ? $"{key}={value}" : $"{key}: {value}";

        internal static (string Key, string Value, char Sep)? ParseLine(string line)
        {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith('#')) return null;
            if (s.StartsWith("export ", StringComparison.Ordinal)) s = s.Substring(7).TrimStart();

            int eq = s.IndexOf('=');
            int colon = s.IndexOf(':');
            int idx;
            char sep;
            // как в Python-версии: '=' если до него нет ':'
            if (eq >= 0 && (colon < 0 || eq < colon)) { idx = eq; sep = '='; }
            else if (colon >= 0) { idx = colon; sep = ':'; }
            else return null;

            var key = s.Substring(0, idx).Trim();
            if (key.Length == 0) return null;
            var value = s.Substring(idx + 1).Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value.Substring(1, value.Length - 2);
            return (key, value, sep);
        }
    }
}
