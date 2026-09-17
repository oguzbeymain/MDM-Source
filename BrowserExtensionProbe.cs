using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace MDM
{
    public sealed class BrowserExtensionStatus
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public bool BrowserInstalled { get; init; }
        /// <summary>true = yüklü ve açık (Aktif); false = yok veya kapalı (Devre dışı).</summary>
        public bool ExtensionActive { get; init; }
        public string Detail { get; init; } = "";
        public string AccentHex { get; init; } = "#888888";
    }

    /// <summary>
    /// Edge/Chrome/Brave Preferences + Secure Preferences üzerinden MDM eklentisini bulur.
    /// Yenile: aktif / devre dışı / yok. Mümkün — tarayıcı profil dosyaları okunarak yapılır.
    /// </summary>
    public static class BrowserExtensionProbe
    {
        private static readonly string[] NameNeedles =
        {
            "MDM Integration",
            "MuckDownloadManager",
            "Muck Download",
            "DownloadMuck" // eski eklenti adı
        };

        public static IReadOnlyList<BrowserExtensionStatus> ProbeAll()
        {
            string extRoot = ExtensionInstaller.InstallRoot;
            return new[]
            {
                Probe("edge", "Microsoft Edge", "#0078D4",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Microsoft", "Edge", "User Data"),
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe",
                    extRoot),
                Probe("chrome", "Google Chrome", "#34A853",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Google", "Chrome", "User Data"),
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
                    extRoot),
                Probe("brave", "Brave", "#FB542B",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BraveSoftware", "Brave-Browser", "User Data"),
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\brave.exe",
                    extRoot),
                ProbeFirefoxRelease(),
                ProbeFirefoxDeveloper(),
            };
        }

        private static BrowserExtensionStatus ProbeFirefoxRelease()
            => ProbeFirefoxChannel(
                id: "firefox",
                name: "Mozilla Firefox",
                accent: "#FF7139",
                exe: ExtensionInstaller.ResolveFirefoxReleaseExe(),
                developer: false);

        private static BrowserExtensionStatus ProbeFirefoxDeveloper()
            => ProbeFirefoxChannel(
                id: "firefox-developer",
                name: "Firefox Developer Edition",
                accent: "#00D4AA",
                exe: ExtensionInstaller.ResolveFirefoxDeveloperExe(out _),
                developer: true);

        private static BrowserExtensionStatus ProbeFirefoxChannel(
            string id, string name, string accent, string? exe, bool developer)
        {
            bool browserOk = !string.IsNullOrWhiteSpace(exe);
            if (!browserOk)
            {
                return new BrowserExtensionStatus
                {
                    Id = id, Name = name, AccentHex = accent,
                    BrowserInstalled = false, ExtensionActive = false,
                    Detail = Loc.T("extstatus.browser_missing", "Tarayıcı yüklü değil")
                };
            }

            bool live = ExtensionPresence.SeenRecentlyForBrowser(id, TimeSpan.FromMinutes(5));
            bool onDisk = ExtensionInstaller.FirefoxExtensionPresentOnDisk(developer);
            bool active = live;
            string detail;
            if (active)
                detail = Loc.T("extstatus.active", "Eklenti aktif");
            else if (onDisk)
                detail = Loc.T("extstatus.firefox_off", "Eklenti yüklü ama kapalı / oturum yok");
            else
                detail = Loc.T("extstatus.firefox_missing", "Eklenti yok — «Kur»");

            Debug.WriteLine($"ExtProbe[{id}]: live={live} disk={onDisk} → active={active}");

            return new BrowserExtensionStatus
            {
                Id = id,
                Name = name,
                AccentHex = accent,
                BrowserInstalled = true,
                ExtensionActive = active,
                Detail = detail
            };
        }

        private static BrowserExtensionStatus Probe(
            string id, string name, string accent, string userData, string appPathKey, string extRoot)
        {
            bool browserOk = BrowserInstalled(appPathKey) || Directory.Exists(userData);
            if (!browserOk)
            {
                return new BrowserExtensionStatus
                {
                    Id = id, Name = name, AccentHex = accent,
                    BrowserInstalled = false, ExtensionActive = false,
                    Detail = Loc.T("extstatus.browser_missing", "Tarayıcı yüklü değil")
                };
            }

            // Canlı ping: eklenti gerçekten çalışıyorsa kesin Aktif
            bool live = ExtensionPresence.SeenRecentlyForBrowser(id, TimeSpan.FromMinutes(5));
            var disk = ScanUserData(userData, extRoot);

            bool active = live || disk.Enabled;
            string detail;
            if (active)
                detail = Loc.T("extstatus.active", "Eklenti aktif");
            else if (disk.Present)
                detail = Loc.T("extstatus.installed_off", "Eklenti yüklü ama kapalı");
            else
                detail = Loc.T("extstatus.missing", "Eklenti yok / kaldırılmış");

            Debug.WriteLine($"ExtProbe[{id}]: live={live} present={disk.Present} enabled={disk.Enabled} → active={active}");

            return new BrowserExtensionStatus
            {
                Id = id,
                Name = name,
                AccentHex = accent,
                BrowserInstalled = true,
                ExtensionActive = active,
                Detail = detail
            };
        }

        private readonly record struct DiskState(bool Present, bool Enabled);

        private static DiskState ScanUserData(string userDataRoot, string extRoot)
        {
            if (!Directory.Exists(userDataRoot))
                return new DiskState(false, false);

            string needle = Normalize(extRoot);
            bool present = false;
            bool enabled = false;

            foreach (string profile in EnumerateProfiles(userDataRoot))
            {
                foreach (string file in new[] { "Secure Preferences", "Preferences" })
                {
                    var hit = ScanPrefsFile(Path.Combine(profile, file), needle);
                    if (hit.Present) present = true;
                    if (hit.Enabled) enabled = true;
                }
            }

            return new DiskState(present, enabled);
        }

        private static DiskState ScanPrefsFile(string prefsPath, string needle)
        {
            string? json = ReadShared(prefsPath);
            if (string.IsNullOrWhiteSpace(json))
                return new DiskState(false, false);

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("extensions", out var ext))
                    return new DiskState(false, false);
                if (!ext.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                    return new DiskState(false, false);

                bool present = false;
                bool enabled = false;
                foreach (var prop in settings.EnumerateObject())
                {
                    if (!IsOurs(prop.Value, needle))
                        continue;
                    present = true;
                    if (IsEnabled(prop.Value))
                        enabled = true;
                }
                return new DiskState(present, enabled);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtProbe read fail {prefsPath}: {ex.Message}");
                return new DiskState(false, false);
            }
        }

        private static bool IsOurs(JsonElement entry, string needle)
        {
            // 1) path
            if (entry.TryGetProperty("path", out var pathEl))
            {
                string path = pathEl.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(path))
                {
                    string n = Normalize(path);
                    if (n.Contains("MDM_Eklenti", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("browserextension", StringComparison.OrdinalIgnoreCase)
                           && n.Contains("muck", StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(needle)
                            && (n.Equals(needle, StringComparison.OrdinalIgnoreCase)
                                || n.StartsWith(needle + "\\", StringComparison.OrdinalIgnoreCase)
                                || n.StartsWith(needle + "/", StringComparison.OrdinalIgnoreCase))))
                        return true;

                    // Diskteki manifest adı
                    if (ManifestNameMatchesFolder(path))
                        return true;
                }
            }

            // 2) gömülü manifest.name
            if (entry.TryGetProperty("manifest", out var man)
                && man.TryGetProperty("name", out var nameEl))
            {
                string name = nameEl.GetString() ?? "";
                if (NameMatches(name))
                    return true;
            }

            return false;
        }

        private static bool ManifestNameMatchesFolder(string folder)
        {
            try
            {
                string mf = Path.Combine(folder, "manifest.json");
                if (!File.Exists(mf)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                if (doc.RootElement.TryGetProperty("name", out var n))
                    return NameMatches(n.GetString() ?? "");
            }
            catch { /* ignore */ }
            return false;
        }

        private static bool NameMatches(string name)
        {
            foreach (string needle in NameNeedles)
            {
                if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Chromium: state 1 = enabled. Eksik state + boş disable_reasons = genelde aktif unpacked.
        /// state 0 veya dolu disable_reasons = kapalı.
        /// </summary>
        private static bool IsEnabled(JsonElement entry)
        {
            if (HasDisableReasons(entry))
                return false;

            if (entry.TryGetProperty("state", out var stateEl))
            {
                if (stateEl.ValueKind == JsonValueKind.Number)
                    return stateEl.GetInt32() == 1;
                if (stateEl.ValueKind == JsonValueKind.String
                    && int.TryParse(stateEl.GetString(), out int s))
                    return s == 1;
            }

            // state yoksa unpacked path varsa aktif say
            return entry.TryGetProperty("path", out _);
        }

        private static bool HasDisableReasons(JsonElement entry)
        {
            if (!entry.TryGetProperty("disable_reasons", out var dr))
                return false;

            return dr.ValueKind switch
            {
                JsonValueKind.Array => dr.GetArrayLength() > 0,
                JsonValueKind.Number => dr.GetInt64() != 0,
                JsonValueKind.Object => dr.EnumerateObject().Any(),
                JsonValueKind.String => !string.IsNullOrWhiteSpace(dr.GetString()) && dr.GetString() is not ("0" or "[]"),
                _ => false
            };
        }

        private static IEnumerable<string> EnumerateProfiles(string userDataRoot)
        {
            var list = new List<string>();
            void Add(string dir)
            {
                if (Directory.Exists(dir) && !list.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    list.Add(dir);
            }

            Add(Path.Combine(userDataRoot, "Default"));
            try
            {
                foreach (string d in Directory.GetDirectories(userDataRoot, "Profile *"))
                    Add(d);
            }
            catch { /* ignore */ }

            try
            {
                string? ls = ReadShared(Path.Combine(userDataRoot, "Local State"));
                if (!string.IsNullOrWhiteSpace(ls))
                {
                    using var doc = JsonDocument.Parse(ls);
                    if (doc.RootElement.TryGetProperty("profile", out var p)
                        && p.TryGetProperty("info_cache", out var cache)
                        && cache.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var item in cache.EnumerateObject())
                            Add(Path.Combine(userDataRoot, item.Name));
                    }
                }
            }
            catch { /* ignore */ }

            return list;
        }

        private static bool BrowserInstalled(string appPathKey)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(appPathKey)
                    ?? Registry.CurrentUser.OpenSubKey(appPathKey);
                string? path = key?.GetValue(null) as string;
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
            catch { return false; }
        }

        private static string? ReadShared(string path)
        {
            if (!File.Exists(path)) return null;
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var r = new StreamReader(fs, Encoding.UTF8, true);
                    return r.ReadToEnd();
                }
                catch (IOException)
                {
                    Thread.Sleep(50 + i * 40);
                }
                catch { return null; }
            }
            return null;
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant(); }
            catch { return (path ?? "").Replace('/', '\\').Trim().TrimEnd('\\').ToLowerInvariant(); }
        }
    }
}
