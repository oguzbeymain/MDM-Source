using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;

namespace MDM
{
    /// <summary>
    /// Eklenti çıktı klasörü: uygulamanın yanındaki MDM_Eklenti
    /// (Chromium'da paketlenmemiş olarak bu klasör yüklenir).
    /// Firefox: XPI + about:debugging / dosyadan yükleme.
    /// </summary>
    public static class ExtensionInstaller
    {
        public const string FirefoxAddonId = "mdm@muckdownloadmanager.local";
        public const string DeveloperEditionDownloadUrl = "https://www.mozilla.org/firefox/developer/";
        private static readonly HashSet<string> OpenedBrowserUi = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> OpenedExplorerFolders = new(StringComparer.OrdinalIgnoreCase);

        public static string InstallRoot =>
            Path.Combine(AppContext.BaseDirectory, "MDM_Eklenti");

        public static string FirefoxStagingRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", "FirefoxExtension");

        public static string FirefoxXpiPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", "MDM-Firefox.xpi");

        public static string FirefoxXpiPathFor(string channelId)
        {
            string name = channelId switch
            {
                "firefox-developer" or "firefox-dev" or "developer" => "MDM-FirefoxDev.xpi",
                "zen" => "MDM-Zen.xpi",
                _ => "MDM-Firefox.xpi"
            };
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", name);
        }

        public static string ChromiumStagingRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", "ChromiumExtension");

        public static string ChromiumCrxPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", "MDM-Integration.crx");

        /// <summary>
        /// Tarayıcıya göre eklenti kurulumu. Gecko: XPI «Ekle» diyaloğu.
        /// Chromium: eklentiler sayfası + paketlenmemiş klasör (CRX indirme yok).
        /// </summary>
        public static bool TryInstallBrowser(string browserId, out string title, out string message, out string detail)
        {
            var target = BrowserTargets.Find(browserId);
            title = target?.Name ?? browserId;
            message = "";
            detail = "";
            if (target == null)
            {
                message = string.Format(Loc.T("settings.ext.browser_missing_install", "{0} yüklü değil."), browserId);
                detail = browserId;
                return false;
            }

            title = string.Format(Loc.T("settings.ext.chromium_title", "{0} eklentisi"), target.Name);
            if (target.Family == BrowserFamily.Gecko)
                return TryInstallGeckoBrowser(target, out title, out message, out detail);
            return TryInstallChromiumBrowser(target, out title, out message, out detail);
        }

        public static void EnsureInstalled()
        {
            try
            {
                CleanupBrokenAutoInstallArtifacts();
                if (Directory.Exists(InstallRoot) && File.Exists(Path.Combine(InstallRoot, "manifest.json")))
                    PrepareChromiumStaging("chrome");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtensionInstaller: {ex.Message}");
            }
        }

        public enum FirefoxInstallMode
        {
            Auto,
            Temporary,
            Permanent
        }

        /// <summary>
        /// Firefox eklenti kurulumu.
        /// Temporary: about:debugging + manifest.json (kullanıcı elle yükler).
        /// Permanent: Developer Edition'a XPI / «Ekle».
        /// </summary>
        public static bool TryInstallFirefox(out string title, out string message, out string detail,
            FirefoxInstallMode mode = FirefoxInstallMode.Auto)
        {
            title = Loc.T("firefox.title", "Firefox eklentisi");
            message = "";
            detail = "";

            try
            {
                if (!Directory.Exists(InstallRoot) || !File.Exists(Path.Combine(InstallRoot, "manifest.json")))
                {
                    message = Loc.T("firefox.howto.folder_missing", "Eklenti klasörü bulunamadı.");
                    detail = InstallRoot;
                    return false;
                }

                if (mode is FirefoxInstallMode.Auto or FirefoxInstallMode.Temporary)
                    return TryInstallFirefoxTemporary(out title, out message, out detail);

                return TryInstallFirefoxPermanent(out title, out message, out detail);
            }
            catch (Exception ex)
            {
                message = Loc.T("settings.ext.firefox_error_message", "Kurulum başlatılamadı.");
                detail = ex.Message;
                return false;
            }
        }

        private static bool TryInstallFirefoxTemporary(out string title, out string message, out string detail)
        {
            title = Loc.T("firefox.title", "Firefox eklentisi");
            string? releaseExe = ResolveFirefoxReleaseExe();
            if (string.IsNullOrWhiteSpace(releaseExe))
            {
                message = Loc.T("firefox.howto.missing_release", "Mozilla Firefox bulunamadı.");
                detail = Loc.T("firefox.howto.missing_release_detail", "Normal Firefox (Mozilla Firefox) kurulu değil.");
                return false;
            }

            PrepareFirefoxStaging("firefox");
            string staging = Path.GetFullPath(FirefoxStagingRoot);
            string manifest = Path.Combine(staging, "manifest.json");

            LaunchGeckoPage(releaseExe, "about:debugging#/runtime/this-firefox");
            TryRevealFile(manifest);
            TryCopyText(manifest);

            message = Loc.T("firefox.howto.temp_message", "Geçici kurulum — bir adım kaldı.");
            detail = string.Format(
                Loc.T("firefox.howto.temp_detail",
                    "1) Açılan Firefox sayfasında «Geçici eklenti yükle»ye tıkla.\n2) Explorer’da seçili dosyayı seç:\n   {0}\n\nİzinleri onayla. Bu oturumda çalışır; Firefox kapanınca silinir.\nKalıcı kurulum için Firefox Developer Edition gerekir."),
                manifest);
            return true;
        }

        private static bool TryInstallFirefoxPermanent(out string title, out string message, out string detail)
        {
            title = Loc.T("firefox.title", "Firefox eklentisi");
            string? devExe = ResolveFirefoxDeveloperExe(out string channel);
            if (string.IsNullOrWhiteSpace(devExe))
            {
                message = Loc.T("firefox.howto.missing_dev", "Firefox Developer Edition bulunamadı.");
                detail = Loc.T("firefox.howto.missing_dev_detail", "Önce Developer Edition’ı kur, sonra tekrar dene.")
                         + "\n" + DeveloperEditionDownloadUrl;
                return false;
            }

            InstallFirefoxChannel(devExe, "firefox-developer", permanent: true);
            string xpi = Path.GetFullPath(FirefoxXpiPathFor("firefox-developer"));

            message = Loc.T("firefox.howto.perm_message", "Kalıcı kurulum — eklentiyi onaylayın.");
            detail = string.Format(
                Loc.T("firefox.howto.perm_detail",
                    "Kanal: {0}\n\n1) Açılan pencerede «Ekle» deyin (imza uyarısı normal).\n2) Gelmezse: about:addons → dişli → «Dosyadan eklenti yükle» →\n   {1}\n\nBu kanalda eklenti Firefox kapanınca da kalır."),
                ChannelLabel(channel),
                xpi);
            return true;
        }

        private static string NormalizeDir(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.TrimEnd('\\', '/'); }
        }

        private static void TryRevealFile(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string folder = Directory.Exists(full)
                    ? full
                    : Path.GetDirectoryName(full) ?? full;
                folder = NormalizeDir(folder);
                if (OpenedExplorerFolders.Contains(folder))
                    return;
                if (ExplorerWindowExists(folder))
                {
                    OpenedExplorerFolders.Add(folder);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + folder + "\"",
                    UseShellExecute = true
                });
                OpenedExplorerFolders.Add(folder);
            }
            catch { /* ignore */ }
        }

        private static bool ExplorerWindowExists(string folder)
        {
            try
            {
                string want = NormalizeDir(folder);
                string parent = NormalizeDir(Path.GetDirectoryName(want) ?? want);
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;
                dynamic shell = Activator.CreateInstance(shellType)!;
                foreach (dynamic win in shell.Windows())
                {
                    try
                    {
                        string? loc = null;
                        try { loc = win.Document.Folder.Self.Path as string; }
                        catch { /* ignore */ }
                        if (string.IsNullOrWhiteSpace(loc)) continue;
                        string have = NormalizeDir(loc);
                        if (have.Equals(want, StringComparison.OrdinalIgnoreCase)
                            || have.Equals(parent, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { /* ignore this window */ }
                }
            }
            catch { /* COM yok */ }
            return false;
        }

        private static void TryCopyText(string text)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch { /* ignore */ }
        }

        private static void InstallFirefoxChannel(string exe, string channelId, bool permanent)
        {
            PackFirefoxXpi(channelId);
            string xpi = Path.GetFullPath(FirefoxXpiPathFor(channelId));
            string quotedXpi = "\"" + xpi + "\"";

            if (!permanent)
                return;

            TryRelaxSignaturePref();
            TrySideloadXpiIntoProfiles(xpi, developerOnly: true);
            LaunchGeckoFile(exe, xpi);
        }

        private static bool TryInstallGeckoBrowser(BrowserTarget target, out string title, out string message, out string detail)
        {
            title = string.Format(Loc.T("settings.ext.chromium_title", "{0} eklentisi"), target.Name);
            message = "";
            detail = "";

            if (!Directory.Exists(InstallRoot) || !File.Exists(Path.Combine(InstallRoot, "manifest.json")))
            {
                message = Loc.T("settings.ext.firefox_error_message", "Kurulum başlatılamadı.");
                detail = InstallRoot;
                return false;
            }

            string? exe = target.ResolveExe();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                message = string.Format(
                    Loc.T("settings.ext.browser_missing_install", "{0} yüklü değil."),
                    target.Name);
                return false;
            }

            PackFirefoxXpi(target.Id);
            string xpi = Path.GetFullPath(FirefoxXpiPathFor(target.Id));
            foreach (string profile in EnumerateGeckoProfiles(target.GeckoRoot()))
            {
                try
                {
                    EnsureFirefoxUserPrefs(profile);
                    string extDir = Path.Combine(profile, "extensions");
                    Directory.CreateDirectory(extDir);
                    File.Copy(xpi, Path.Combine(extDir, FirefoxAddonId + ".xpi"), overwrite: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Gecko sideload [{target.Id}]: {ex.Message}");
                }
            }

            LaunchGeckoFile(exe, xpi);
            message = Loc.T("settings.ext.prompt_message", "Kalıcı kurulum — eklentiyi onaylayın.");
            detail = string.Format(
                Loc.T("settings.ext.prompt_detail",
                    "1) Açılan pencerede «Ekle» deyin (imza uyarısı normal).\n2) Gelmezse eklentiler sayfasında dosyadan yükleyin:\n   {0}\n\nEkledikten sonra eklenti tarayıcı kapanınca da kalır."),
                xpi);
            return true;
        }

        private static bool TryInstallChromiumBrowser(BrowserTarget target, out string title, out string message, out string detail)
        {
            title = string.Format(Loc.T("settings.ext.chromium_title", "{0} eklentisi"), target.Name);
            message = "";
            detail = "";

            if (!Directory.Exists(InstallRoot) || !File.Exists(Path.Combine(InstallRoot, "manifest.json")))
            {
                message = Loc.T("settings.ext.firefox_error_message", "Kurulum başlatılamadı.");
                detail = InstallRoot;
                return false;
            }

            string? exe = target.ResolveExe();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                message = string.Format(
                    Loc.T("settings.ext.browser_missing_install", "{0} yüklü değil."),
                    target.Name);
                return false;
            }

            PrepareChromiumStaging(target.Id);
            string staging = Path.GetFullPath(ChromiumStagingRoot);
            TryDeleteFile(ChromiumCrxPath);
            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "MDM-Integration.crx"));

            TryEnableChromiumDeveloperMode(target.UserDataRoot());
            LaunchChromiumExtensions(exe, target);
            TryRevealFile(staging);
            TryCopyText(staging);

            message = Loc.T("settings.ext.chromium_message", "Kurulum — bir adım kaldı.");
            detail = string.Format(
                Loc.T("settings.ext.chromium_detail",
                    "1) Açılan eklentiler sayfasında «Geliştirici modu»nu aç.\n2) «Paketlenmemiş öğe yükle»ye tıkla.\n3) Explorer’da zaten seçili klasörü seç — klasörü başka yere kopyalaman gerekmez:\n   {0}"),
                staging);
            return true;
        }

        private static void TryEnableChromiumDeveloperMode(string? userData)
        {
            if (string.IsNullOrWhiteSpace(userData) || !Directory.Exists(userData))
                return;
            TrySetDeveloperModeFlag(Path.Combine(userData, "Local State"));
            TrySetDeveloperModeFlag(Path.Combine(userData, "Default", "Preferences"));
        }

        private static void TrySetDeveloperModeFlag(string jsonPath)
        {
            if (!File.Exists(jsonPath)) return;
            try
            {
                string raw;
                using (var fs = new FileStream(jsonPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
                        raw = reader.ReadToEnd();

                    var root = JsonNode.Parse(raw)?.AsObject();
                    if (root == null) return;
                    var ext = root["extensions"] as JsonObject ?? new JsonObject();
                    root["extensions"] = ext;
                    var ui = ext["ui"] as JsonObject ?? new JsonObject();
                    ext["ui"] = ui;
                    ui["developer_mode"] = true;
                    byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString() + "\n");
                    fs.SetLength(0);
                    fs.Position = 0;
                    fs.Write(bytes);
                }
            }
            catch
            {
                /* tarayıcı dosyayı kilitliyorsa atla */
            }
        }

        public static void PrepareChromiumStaging(string channelId)
        {
            string src = InstallRoot;
            string dst = ChromiumStagingRoot;
            Directory.CreateDirectory(dst);

            foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(src, file);
                string target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(file, target, overwrite: true); }
                catch { /* tarayıcı dosyayı kilitliyorsa atla */ }
            }

            try
            {
                File.WriteAllText(
                    Path.Combine(dst, "mdm-channel.js"),
                    $"self.MDM_BROWSER_CHANNEL = \"{channelId}\";\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch { /* ignore */ }
        }

        private static void LaunchChromiumExtensions(string exe, BrowserTarget target)
        {
            string url = ChromiumExtensionsCliUrl(target, exe);
            LaunchBrowserOnce(exe, "--new-tab " + url, url);
        }

        /// <summary>
        /// brave:// ve opera:// komut satırında yok sayılır; tarayıcı boş pencere açar.
        /// Chromium ailesi chrome://extensions kabul eder; Edge edge:// de kabul eder.
        /// </summary>
        private static string ChromiumExtensionsCliUrl(BrowserTarget target, string exe)
        {
            string id = (target.Id ?? "").ToLowerInvariant();
            string path = exe.ToLowerInvariant();
            if (id.Contains("edge") || path.Contains("msedge"))
                return "edge://extensions/";
            return "chrome://extensions/";
        }

        private static void LaunchGeckoPage(string exe, string url)
        {
            LaunchBrowserOnce(exe, "-new-tab \"" + url + "\"", url);
        }

        private static void LaunchGeckoFile(string exe, string file)
        {
            string full = Path.GetFullPath(file);
            LaunchBrowserOnce(exe, "\"" + full + "\"", full);
        }

        private static void LaunchBrowserOnce(string exe, string args, string uiKey)
        {
            string key = NormalizeExeKey(exe) + "|" + uiKey;
            if (OpenedBrowserUi.Contains(key) && IsBrowserProcessRunning(exe))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
                });
                OpenedBrowserUi.Add(key);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LaunchBrowserOnce: {ex.Message}");
            }
        }

        private static string NormalizeExeKey(string exe)
        {
            try { return Path.GetFullPath(exe); }
            catch { return exe; }
        }

        private static bool IsBrowserProcessRunning(string exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return false;
            string name = Path.GetFileNameWithoutExtension(exe);
            if (string.IsNullOrWhiteSpace(name)) return false;
            string? appDir = null;
            try { appDir = Path.GetDirectoryName(Path.GetFullPath(exe)); }
            catch { /* ignore */ }

            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { return false; }

            try
            {
                foreach (Process p in procs)
                {
                    try
                    {
                        string? path = p.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(path))
                            return true;
                        if (path.Equals(NormalizeExeKey(exe), StringComparison.OrdinalIgnoreCase))
                            return true;
                        if (!string.IsNullOrWhiteSpace(appDir)
                            && path.StartsWith(appDir, StringComparison.OrdinalIgnoreCase))
                            return true;
                        string? parent = Path.GetDirectoryName(path);
                        if (!string.IsNullOrWhiteSpace(appDir) && !string.IsNullOrWhiteSpace(parent)
                            && string.Equals(Path.GetDirectoryName(parent), appDir, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                foreach (Process p in procs)
                    p.Dispose();
            }
        }

        public static bool IsFirefoxReleaseInstalled()
            => !string.IsNullOrWhiteSpace(ResolveFirefoxReleaseExe());

        public static bool IsDeveloperEditionInstalled()
            => !string.IsNullOrWhiteSpace(ResolveFirefoxDeveloperExe(out _));

        public static string? ResolveFirefoxDeveloperExe(out string channel)
        {
            channel = "developer";
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            // Program Files (x86) da denenmeli: Dev Edition oraya kurulabiliyor ve
            // eksikliği yüzünden kurulu sürüm bulunamayıp indirme sayfası açılıyordu
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates =
            {
                Path.Combine(local, "Firefox Developer Edition", "firefox.exe"),
                Path.Combine(pf, "Firefox Developer Edition", "firefox.exe"),
                Path.Combine(pf86, "Firefox Developer Edition", "firefox.exe"),
                Path.Combine(local, "Firefox Nightly", "firefox.exe"),
                Path.Combine(pf, "Firefox Nightly", "firefox.exe"),
                Path.Combine(pf86, "Firefox Nightly", "firefox.exe"),
            };
            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                channel = DetectChannel(path);
                return path;
            }

            // Farklı klasöre kurulmuş olabilir; kaldırma kaydı gerçek yolu tutuyor
            foreach (string path in DeveloperExePathsFromRegistry())
            {
                if (!File.Exists(path)) continue;
                channel = DetectChannel(path);
                return path;
            }
            return null;
        }

        /// <summary>
        /// Kayıt defterindeki Mozilla kurulumları (HKCU + HKLM, 32/64-bit görünüm) içinden
        /// Developer Edition / Nightly yollarını toplar.
        /// </summary>
        private static IEnumerable<string> DeveloperExePathsFromRegistry()
        {
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };

            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in views)
                {
                    RegistryKey? mozilla = null;
                    try
                    {
                        using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                        mozilla = root.OpenSubKey(@"SOFTWARE\Mozilla");
                        if (mozilla == null) continue;

                        foreach (string product in mozilla.GetSubKeyNames())
                        {
                            if (!product.Contains("Developer", StringComparison.OrdinalIgnoreCase)
                                && !product.Contains("Nightly", StringComparison.OrdinalIgnoreCase))
                                continue;

                            using RegistryKey? versions = mozilla.OpenSubKey(product);
                            if (versions == null) continue;

                            foreach (string version in versions.GetSubKeyNames())
                            {
                                using RegistryKey? main = versions.OpenSubKey($@"{version}\Main");
                                if (main?.GetValue("PathToExe") is string exe && exe.Length > 0)
                                    yield return exe;
                            }
                        }
                    }
                    finally { mozilla?.Dispose(); }
                }
            }
        }

        public static void PrepareFirefoxStaging(string channelId = "firefox")
        {
            string src = InstallRoot;
            string dst = FirefoxStagingRoot;
            if (Directory.Exists(dst))
            {
                try { Directory.Delete(dst, recursive: true); }
                catch
                {
                    foreach (var f in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(f); } catch { /* ignore */ }
                    }
                }
            }
            Directory.CreateDirectory(dst);

            foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(src, file);
                if (rel.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                string target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            string boot = channelId switch
            {
                "firefox-developer" or "firefox-dev" or "developer" => "firefox-developer",
                "zen" => "zen",
                _ => "firefox"
            };
            File.WriteAllText(
                Path.Combine(dst, "mdm-channel.js"),
                $"self.MDM_BROWSER_CHANNEL = \"{boot}\";\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            WriteFirefoxManifest(Path.Combine(dst, "manifest.json"));
        }

        public static void PackFirefoxXpi(string channelId = "firefox")
        {
            PrepareFirefoxStaging(channelId);
            string staging = FirefoxStagingRoot;
            string xpi = FirefoxXpiPathFor(channelId);
            Directory.CreateDirectory(Path.GetDirectoryName(xpi)!);
            if (File.Exists(xpi))
                File.Delete(xpi);

            ZipFile.CreateFromDirectory(staging, xpi, CompressionLevel.Optimal, includeBaseDirectory: false);
        }

        private static void WriteFirefoxManifest(string path)
        {
            string srcManifest = Path.Combine(InstallRoot, "manifest.json");
            string raw = File.ReadAllText(srcManifest, Encoding.UTF8);
            var root = JsonNode.Parse(raw)?.AsObject()
                       ?? throw new InvalidOperationException("manifest.json okunamadı.");

            // Firefox imzasız / geçici yükleme için sabit gecko id
            root["browser_specific_settings"] = new JsonObject
            {
                ["gecko"] = new JsonObject
                {
                    ["id"] = FirefoxAddonId,
                    ["strict_min_version"] = "115.0",
                    ["data_collection_permissions"] = new JsonObject
                    {
                        ["required"] = new JsonArray("none")
                    }
                }
            };

            // downloads.shelf Chromium-only — Firefox rededer
            if (root["permissions"] is JsonArray perms)
            {
                for (int i = perms.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(perms[i]?.GetValue<string>(), "downloads.shelf", StringComparison.OrdinalIgnoreCase))
                        perms.RemoveAt(i);
                }
                bool hasBlocking = false;
                foreach (var p in perms)
                {
                    if (string.Equals(p?.GetValue<string>(), "webRequestBlocking", StringComparison.OrdinalIgnoreCase))
                    {
                        hasBlocking = true;
                        break;
                    }
                }
                if (!hasBlocking)
                    perms.Add("webRequestBlocking");
            }
            else
            {
                root["permissions"] = new JsonArray(
                    "downloads", "storage", "alarms", "webRequest", "webRequestBlocking",
                    "webNavigation", "cookies", "scripting", "tabs", "contextMenus");
            }

            // Firefox MV3: background.scripts — importScripts SW'ye özel; hepsini sırayla yükle
            root["background"] = new JsonObject
            {
                ["scripts"] = new JsonArray(
                    "mdm-channel.js",
                    "i18n.js",
                    "classifier.js",
                    "capture-store.js",
                    "formats.js",
                    "desktop.js",
                    "menus.js",
                    "background.js")
            };

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, root.ToJsonString(opts) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>
        /// Yalnızca normal Mozilla Firefox. App Paths çoğu zaman Developer Edition'ı
        /// gösterir — onu burada asla kullanma.
        /// </summary>
        public static string? ResolveFirefoxReleaseExe()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates =
            {
                Path.Combine(pf, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(pf86, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(local, "Mozilla Firefox", "firefox.exe"),
            };
            foreach (string path in candidates)
            {
                if (File.Exists(path) && DetectChannel(path) == "release")
                    return path;
            }

            foreach (string path in ReleaseExePathsFromRegistry())
            {
                if (File.Exists(path) && DetectChannel(path) == "release")
                    return path;
            }

            return null;
        }

        public static string? ResolveFirefoxExe(out string channel)
        {
            string? release = ResolveFirefoxReleaseExe();
            if (!string.IsNullOrWhiteSpace(release))
            {
                channel = "release";
                return release;
            }

            string? dev = ResolveFirefoxDeveloperExe(out channel);
            return dev;
        }

        private static IEnumerable<string> ReleaseExePathsFromRegistry()
        {
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };

            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in views)
                {
                    RegistryKey? mozilla = null;
                    try
                    {
                        using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                        mozilla = root.OpenSubKey(@"SOFTWARE\Mozilla");
                        if (mozilla == null) continue;

                        foreach (string product in mozilla.GetSubKeyNames())
                        {
                            if (!product.Equals("Mozilla Firefox", StringComparison.OrdinalIgnoreCase)
                                && !product.Equals("Firefox", StringComparison.OrdinalIgnoreCase))
                                continue;

                            using RegistryKey? versions = mozilla.OpenSubKey(product);
                            if (versions == null) continue;

                            foreach (string version in versions.GetSubKeyNames())
                            {
                                using RegistryKey? main = versions.OpenSubKey($@"{version}\Main");
                                if (main?.GetValue("PathToExe") is string exe && exe.Length > 0)
                                    yield return exe;
                            }
                        }
                    }
                    finally { mozilla?.Dispose(); }
                }
            }
        }

        internal static string DetectChannel(string exePath)
        {
            string p = exePath.ToLowerInvariant();
            if (p.Contains("developer")) return "developer";
            if (p.Contains("nightly")) return "nightly";
            if (p.Contains("esr")) return "esr";
            return "release";
        }

        private static string ChannelLabel(string channel) => channel switch
        {
            "developer" => "Firefox Developer Edition",
            "nightly" => "Firefox Nightly",
            "esr" => "Firefox ESR",
            _ => "Firefox"
        };

        private static void LaunchFirefox(string exe, string args)
            => LaunchBrowserOnce(exe, args, args);

        public static bool IsFirefoxExeRunning(string exe)
        {
            return EnumerateFirefoxProcesses(exe).Count > 0;
        }

        /// <summary>
        /// Yalnızca verilen firefox.exe oturumunu kapatır (Developer Edition'a dokunmaz).
        /// Geçici eklenti yüklemek için Marionette'in yeni örnek başlatması gerekir.
        /// </summary>
        public static void CloseFirefoxExe(string exe)
        {
            var procs = EnumerateFirefoxProcesses(exe);
            if (procs.Count == 0) return;

            foreach (Process p in procs)
            {
                try { p.CloseMainWindow(); }
                catch { /* ignore */ }
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 4000 && procs.Any(p => { try { return !p.HasExited; } catch { return false; } }))
                Thread.Sleep(150);

            foreach (Process p in procs)
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch { /* ignore */ }
                finally { p.Dispose(); }
            }

            sw.Restart();
            while (sw.ElapsedMilliseconds < 3000 && IsFirefoxExeRunning(exe))
                Thread.Sleep(150);
        }

        private static List<Process> EnumerateFirefoxProcesses(string exe)
        {
            var list = new List<Process>();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return list;
            string full;
            try { full = Path.GetFullPath(exe); }
            catch { return list; }

            foreach (Process p in Process.GetProcessesByName("firefox"))
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)
                        && path.Equals(full, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(p);
                        continue;
                    }
                }
                catch { /* erişim reddi */ }
                p.Dispose();
            }
            return list;
        }

        /// <summary>Developer/Nightly profiline imza zorunluluğunu kapatır (user.js).</summary>
        private static void TryRelaxSignaturePref()
        {
            try
            {
                foreach (string profile in EnumerateFirefoxProfiles())
                    EnsureFirefoxUserPrefs(profile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Firefox user.js: {ex.Message}");
            }
        }

        private static void EnsureFirefoxUserPrefs(string profileDir)
        {
            string userJs = Path.Combine(profileDir, "user.js");
            string[] lines =
            {
                @"user_pref(""xpinstall.signatures.required"", false);",
                @"user_pref(""extensions.autoDisableScopes"", 0);",
                @"user_pref(""extensions.enabledScopes"", 15);",
            };
            string existing = File.Exists(userJs) ? File.ReadAllText(userJs, Encoding.UTF8) : "";
            var sb = new StringBuilder(existing);
            if (sb.Length > 0 && !existing.EndsWith('\n'))
                sb.Append('\n');
            foreach (string line in lines)
            {
                string key = line.Split(',')[0];
                if (existing.Contains(key, StringComparison.Ordinal))
                    continue;
                sb.AppendLine(line);
            }
            if (sb.ToString() != existing)
                File.WriteAllText(userJs, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// XPI'yi tüm Firefox profillerinin extensions klasörüne kopyalar.
        /// Dev/Nightly/ESR (+ imza pref) ile kalıcı olur; Release'te çoğu zaman disabled kalır.
        /// </summary>
        public static int TrySideloadXpiIntoProfiles(string xpiPath, bool developerOnly = false)
        {
            if (!File.Exists(xpiPath)) return 0;
            int n = 0;
            foreach (string profile in EnumerateFirefoxProfiles())
            {
                if (developerOnly && !IsDeveloperProfile(profile))
                    continue;
                if (!developerOnly && IsDeveloperProfile(profile))
                    continue;
                try
                {
                    EnsureFirefoxUserPrefs(profile);
                    string extDir = Path.Combine(profile, "extensions");
                    Directory.CreateDirectory(extDir);
                    string dest = Path.Combine(extDir, FirefoxAddonId + ".xpi");
                    File.Copy(xpiPath, dest, overwrite: true);
                    n++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Firefox sideload: {ex.Message}");
                }
            }
            return n;
        }

        public static bool IsDeveloperProfile(string profileDir)
        {
            string n = (profileDir ?? "").Replace('/', '\\').ToLowerInvariant();
            return n.Contains("dev-edition") || n.Contains("developer");
        }

        public static IEnumerable<string> EnumerateFirefoxProfiles()
            => EnumerateGeckoProfiles(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Mozilla", "Firefox"));

        public static IEnumerable<string> EnumerateGeckoProfiles(string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
                yield break;
            string ini = Path.Combine(root, "profiles.ini");
            if (!File.Exists(ini))
                yield break;

            string? path = null;
            bool relative = true;
            foreach (string line in File.ReadLines(ini))
            {
                string t = line.Trim();
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        string full = relative ? Path.Combine(root, path) : path;
                        if (Directory.Exists(full))
                            yield return full;
                    }
                    path = null;
                    relative = true;
                    continue;
                }
                if (t.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                    path = t[5..].Trim();
                else if (t.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase))
                    relative = t[11..].Trim() != "0";
            }
            if (!string.IsNullOrWhiteSpace(path))
            {
                string full = relative ? Path.Combine(root, path) : path;
                if (Directory.Exists(full))
                    yield return full;
            }
        }

        public static bool FirefoxExtensionPresentOnDisk(bool? developerProfiles = null)
            => GeckoExtensionPresentOnDisk(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Mozilla", "Firefox"),
                developerProfiles);

        public static bool GeckoExtensionPresentOnDisk(string? geckoRoot, bool? developerProfiles = null)
        {
            try
            {
                string id = FirefoxAddonId;
                foreach (string profile in EnumerateGeckoProfiles(geckoRoot))
                {
                    if (developerProfiles != null)
                    {
                        bool isDev = IsDeveloperProfile(profile);
                        if (developerProfiles == true && !isDev) continue;
                        if (developerProfiles == false && isDev) continue;
                    }
                    string extDir = Path.Combine(profile, "extensions");
                    if (File.Exists(Path.Combine(extDir, id + ".xpi")))
                        return true;
                    if (Directory.Exists(Path.Combine(extDir, id)))
                        return true;
                }
            }
            catch { /* ignore */ }
            return false;
        }

        private static string? ReadAppPath(string key)
        {
            try
            {
                using var hk = Registry.LocalMachine.OpenSubKey(key)
                    ?? Registry.CurrentUser.OpenSubKey(key);
                return hk?.GetValue(null) as string;
            }
            catch { return null; }
        }

        private static void CleanupBrokenAutoInstallArtifacts()
        {
            const string fakeId = "abcdefghijklmnopqrstuvwxyzabcdef";
            string appDataExt = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", "BrowserExtension");

            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google\\Chrome\\User Data\\External Extensions", fakeId + ".json"));
            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft\\Edge\\User Data\\External Extensions", fakeId + ".json"));
            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BraveSoftware\\Brave-Browser\\User Data\\External Extensions", fakeId + ".json"));

            TryDeleteRegistryValue(@"Software\Policies\Google\Chrome\ExtensionInstallForcelist", "1");
            TryDeleteRegistryValue(@"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist", "1");
            TryDeleteRegistryValue(@"Software\Policies\BraveSoftware\Brave\ExtensionInstallForcelist", "1");

            TryDeleteRegistryKey(@"Software\Google\Chrome\Extensions\" + fakeId);
            TryDeleteRegistryKey(@"Software\Microsoft\Edge\Extensions\" + fakeId);
            TryDeleteRegistryKey(@"Software\BraveSoftware\Brave\Extensions\" + fakeId);

            TryDeleteFile(Path.Combine(appDataExt, "update.xml"));
            TryDeleteFile(Path.Combine(appDataExt, "KURULUM.html"));
            TryDeleteFile(Path.Combine(appDataExt, "loader.html"));
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* ignore */ }
        }

        private static void TryDeleteRegistryValue(string subKey, string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
            }
            catch { /* ignore */ }
        }

        private static void TryDeleteRegistryKey(string subKey)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            }
            catch { /* ignore */ }
        }
    }
}
