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
        public bool AutoExtractArchives { get; set; }
        public bool DeleteArchiveAfterExtract { get; set; }
        public bool ScheduleEnabled { get; set; }
        public int ScheduleStartHour { get; set; }
        public int ScheduleEndHour { get; set; }
        public bool RemoteApiLan { get; set; }
        public string RemoteApiToken { get; set; } = "";
        public int CrawlDepth { get; set; } = 1;
        public bool PreferHttp3 { get; set; } = true;
        public bool SkipDuplicateUrls { get; set; } = true;
        public string SkipExtensions { get; set; } = "";
        public string SkipUrlContains { get; set; } = "";
        public string SkipDomains { get; set; } = "";
        public string SkipUrlRegex { get; set; } = "";
        public string SkipMimeContains { get; set; } = "";
        public int SkipMinSizeMb { get; set; }
        public int SkipMaxSizeMb { get; set; }
        public string RenamePattern { get; set; } = "{name}{ext}";
        public int SpeedLimitKBps { get; set; }
        public int MaxConcurrentDownloads { get; set; }
        public int HttpMaxChannels { get; set; }
        public bool NotifyOnComplete { get; set; } = true;
        public bool NotifyOnTrayMinimize { get; set; }
        public bool AutoCreateCategoryFolders { get; set; } = true;
        public string Theme { get; set; } = "Dark";
        /// <summary>Açık tema parlaklığı (70–100). Yalnızca beyaz modda uygulanır.</summary>
        public int LightThemeBrightness { get; set; } = 100;
        public bool CopyFilesHotkeyEnabled { get; set; } = true;
        public string CopyFilesHotkey { get; set; } = "Ctrl+C";
        public bool DeleteKeyShortcutsEnabled { get; set; }
        public int TorrentListenPort { get; set; } = 6881;
        public bool TorrentDht { get; set; } = true;
        public bool TorrentLocalPeers { get; set; } = true;
        public bool TorrentPortForward { get; set; } = true;
        public double TorrentSeedRatio { get; set; }
        public bool TorrentSequential { get; set; }
        public int TorrentMaxConnections { get; set; } = 120;
        public bool AutoReconnect { get; set; } = true;
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
