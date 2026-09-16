using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace MDM
{
    /// <summary>JSON tabanlı yerelleştirme — uygulama + eklenti aynı dil kodunu paylaşır.</summary>
    public static class Loc
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
        private static string _code = "tr";

        public static string Code => _code;
        public static bool IsRtl => _code is "ar" or "fa";
        public static event Action? Changed;

        public static readonly (string Code, string NativeName)[] Languages =
        {
            ("tr", "Türkçe"),
            ("en", "English"),
            ("de", "Deutsch"),
            ("fr", "Français"),
            ("es", "Español"),
            ("ru", "Русский"),
            ("ar", "العربية"),
            ("zh-CN", "简体中文"),
            ("zh-TW", "繁體中文"),
            ("ja", "日本語"),
            ("ko", "한국어"),
            ("it", "Italiano"),
            ("fa", "فارسی"),
        };

        public static void Initialize()
        {
            string code = "tr";
            try { code = AppSettingsStore.Load().UiLanguage ?? "tr"; }
            catch { /* ignore */ }
            Apply(code, raise: false);
        }

        public static void Apply(string? code, bool raise = true)
        {
            string c = NormalizeCode(code);
            _map = LoadMap(c);
            _code = c;
            try
            {
                var culture = CultureInfo.GetCultureInfo(c is "zh-CN" ? "zh-Hans" : c is "zh-TW" ? "zh-Hant" : c);
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch { /* ignore */ }

            if (raise)
                Changed?.Invoke();
        }

        public static string T(string key, string? fallback = null)
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback ?? "";
            if (_map.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v))
                return v;
            // tr fallback
            if (!string.Equals(_code, "tr", StringComparison.OrdinalIgnoreCase))
            {
                var tr = LoadMap("tr");
                if (tr.TryGetValue(key, out string? tv) && !string.IsNullOrEmpty(tv))
                    return tv;
            }
            return fallback ?? key;
        }

        public static string Tf(string key, params object[] args)
        {
            string fmt = T(key);
            try { return string.Format(CultureInfo.CurrentUICulture, fmt, args); }
            catch { return fmt; }
        }

        /// <summary>
        /// Pencere yerleşimi her dilde soldan sağa kalır. Arapça/Farsça'da tüm arayüzü
        /// aynalamak araç çubuğu, sütunlar ve pencere düğmelerini bozuyordu; metin yönü
        /// zaten satır içinde doğru çözülür.
        /// </summary>
        public static FlowDirection Flow => FlowDirection.LeftToRight;

        public static string NormalizeCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "tr";
            string c = code.Trim();
            foreach (var (k, _) in Languages)
            {
                if (k.Equals(c, StringComparison.OrdinalIgnoreCase))
                    return k;
            }
            // aliases
            if (c.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return c.Contains("TW", StringComparison.OrdinalIgnoreCase) || c.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    ? "zh-TW" : "zh-CN";
            if (c.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            return "tr";
        }

        private static Dictionary<string, string> LoadMap(string code)
        {
            return Cache.GetOrAdd(code, LoadMapUncached);
        }

        private static Dictionary<string, string> LoadMapUncached(string code)
        {
            try
            {
                foreach (string path in CandidatePaths(code))
                {
                    if (!File.Exists(path)) continue;
                    string json = File.ReadAllText(path);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts);
                    if (dict != null && dict.Count > 0)
                        return new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { /* ignore */ }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> CandidatePaths(string code)
        {
            string baseDir = AppContext.BaseDirectory;
            yield return Path.Combine(baseDir, "Locales", code + ".json");
            yield return Path.Combine(baseDir, "locales", code + ".json");
            yield return Path.Combine(baseDir, "MDM_Eklenti", "locales", code + ".json");
            // Dev: proje kökü
            string? proj = FindProjectLocales();
            if (proj != null)
                yield return Path.Combine(proj, code + ".json");
        }

        private static string? FindProjectLocales()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                {
                    string p = Path.Combine(dir.FullName, "Locales");
                    if (Directory.Exists(p)) return p;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        public static void InvalidateCache() => Cache.Clear();
    }
}
