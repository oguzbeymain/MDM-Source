using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace MDM.Setup
{
    /// <summary>Sihirbaz metinleri: uygulamanın gömülü Locales/*.json dosyalarından okunur.</summary>
    public static class SetupLoc
    {
        public sealed record Language(string Code, string NativeName);

        public static readonly Language[] Languages =
        {
            new("tr", "Türkçe"),
            new("en", "English"),
            new("de", "Deutsch"),
            new("fr", "Français"),
            new("es", "Español"),
            new("it", "Italiano"),
            new("ru", "Русский"),
            new("ar", "العربية"),
            new("fa", "فارسی"),
            new("zh-CN", "简体中文"),
            new("zh-TW", "繁體中文"),
            new("ja", "日本語"),
            new("ko", "한국어")
        };

        private static readonly Dictionary<string, Dictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _map = new(StringComparer.Ordinal);
        private static Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

        public static string Code { get; private set; } = "tr";

        public static bool IsRightToLeft => Code is "ar" or "fa";

        public static FlowDirection Flow => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        /// <summary>Windows dilinden en yakın desteklenen kodu seçer.</summary>
        public static string DetectSystemLanguage()
        {
            string ui = System.Globalization.CultureInfo.CurrentUICulture.Name;
            foreach (var lang in Languages)
            {
                if (string.Equals(lang.Code, ui, StringComparison.OrdinalIgnoreCase))
                    return lang.Code;
            }

            string two = ui.Length >= 2 ? ui[..2] : "en";
            if (two.Equals("zh", StringComparison.OrdinalIgnoreCase))
                return ui.Contains("TW", StringComparison.OrdinalIgnoreCase)
                    || ui.Contains("HK", StringComparison.OrdinalIgnoreCase)
                    || ui.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    ? "zh-TW" : "zh-CN";

            foreach (var lang in Languages)
            {
                if (lang.Code.StartsWith(two, StringComparison.OrdinalIgnoreCase))
                    return lang.Code;
            }
            return "en";
        }

        public static void Apply(string code)
        {
            Code = Languages.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ? code : "tr";
            _map = LoadMap(Code);
            _fallback = Code == "tr" ? _map : LoadMap("tr");
        }

        public static string T(string key, string fallback = "")
        {
            if (_map.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v)) return v;
            if (_fallback.TryGetValue(key, out string? f) && !string.IsNullOrEmpty(f)) return f;
            return string.IsNullOrEmpty(fallback) ? key : fallback;
        }

        /// <summary>Belirli bir dilin haritası (kategori klasör adları için).</summary>
        public static Dictionary<string, string> MapFor(string code) => LoadMap(code);

        private static Dictionary<string, string> LoadMap(string code)
        {
            if (Cache.TryGetValue(code, out var cached)) return cached;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using Stream? s = asm.GetManifestResourceStream($"Locales.{code}.json");
                if (s != null)
                {
                    using var doc = JsonDocument.Parse(s);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            map[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }
            }
            catch { /* dil dosyası okunamazsa anahtarlar yedeğe düşer */ }

            Cache[code] = map;
            return map;
        }
    }
}
