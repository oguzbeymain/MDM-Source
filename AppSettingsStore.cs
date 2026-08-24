using System.IO;
using System.Text.Json;

namespace DownloadMuck
{
    public sealed class AppSettings
    {
        public bool AutoStart { get; set; } = true;
        public bool AutoStartMinimized { get; set; } = true;
        public string? DefaultDownloadFolder { get; set; }
        public bool ExtensionPromptDone { get; set; }
        public string ListDensity { get; set; } = "Medium";
        public string ListSort { get; set; } = "Date";
        public bool DeleteFilesFromDisk { get; set; } = true;
    }

    public static class AppSettingsStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
        private static AppSettings? _cache;

        public static string StoreDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuckDownloadManager");

        public static string SettingsPath => Path.Combine(StoreDir, "settings.json");

        public static AppSettings Load()
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts);
                    if (s != null)
                    {
                        _cache = s;
                        return _cache;
                    }
                }
            }
            catch { /* ignore */ }

            _cache = new AppSettings();
            return _cache;
        }

        public static void Save(AppSettings settings)
        {
            _cache = settings;
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
            }
            catch { /* ignore */ }
        }

        public static void Invalidate() => _cache = null;
    }
}
