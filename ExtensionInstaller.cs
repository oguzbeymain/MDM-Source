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
            string name = channelId is "firefox-developer" or "firefox-dev" or "developer"
                ? "MDM-FirefoxDev.xpi"
                : "MDM-Firefox.xpi";
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", name);
        }

        public static void EnsureInstalled()
        {
            try
            {
                CleanupBrokenAutoInstallArtifacts();
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
            title = "Firefox eklentisi";
            message = "";
            detail = "";

            try
            {
                if (!Directory.Exists(InstallRoot) || !File.Exists(Path.Combine(InstallRoot, "manifest.json")))
                {
                    message = "Eklenti klasörü bulunamadı.";
                    detail = InstallRoot;
                    return false;
                }

                if (mode is FirefoxInstallMode.Auto or FirefoxInstallMode.Temporary)
                    return TryInstallFirefoxTemporary(out title, out message, out detail);

                return TryInstallFirefoxPermanent(out title, out message, out detail);
            }
            catch (Exception ex)
            {
                message = "Kurulum başlatılamadı.";
                detail = ex.Message;
                return false;
            }
        }

        private static bool TryInstallFirefoxTemporary(out string title, out string message, out string detail)
        {
            title = "Firefox eklentisi";
            string? releaseExe = ResolveFirefoxReleaseExe();
            if (string.IsNullOrWhiteSpace(releaseExe))
            {
                message = "Mozilla Firefox bulunamadı.";
                detail = "Normal Firefox (Mozilla Firefox) kurulu değil.";
                return false;
            }

            PrepareFirefoxStaging("firefox");
            string staging = Path.GetFullPath(FirefoxStagingRoot);
            string manifest = Path.Combine(staging, "manifest.json");

            LaunchFirefox(releaseExe, "about:debugging#/runtime/this-firefox");
            TryRevealFile(manifest);
            TryCopyText(manifest);

            message = "Geçici kurulum — bir adım kaldı.";
            detail =
                "1) Açılan Firefox sayfasında «Geçici eklenti yükle»ye tıkla.\n" +
                "2) Explorer’da seçili dosyayı seç:\n" +
                $"   {manifest}\n\n" +
                "İzinleri onayla. Bu oturumda çalışır; Firefox kapanınca silinir.\n" +
                "Kalıcı kurulum için Firefox Developer Edition gerekir.";
            return true;
        }

        private static bool TryInstallFirefoxPermanent(out string title, out string message, out string detail)
        {
            title = "Firefox eklentisi";
            string? devExe = ResolveFirefoxDeveloperExe(out string channel);
            if (string.IsNullOrWhiteSpace(devExe))
            {
                message = "Firefox Developer Edition bulunamadı.";
                detail = "Önce Developer Edition’ı kur, sonra tekrar dene.\n"
                         + DeveloperEditionDownloadUrl;
                return false;
            }

            InstallFirefoxChannel(devExe, "firefox-developer", permanent: true);
            string xpi = Path.GetFullPath(FirefoxXpiPathFor("firefox-developer"));

            message = "Kalıcı kurulum — eklentiyi onaylayın.";
            detail =
                $"Kanal: {ChannelLabel(channel)}\n\n" +
                "1) Açılan pencerede «Ekle» deyin (imza uyarısı normal).\n" +
                "2) Gelmezse: about:addons → dişli → «Dosyadan eklenti yükle» →\n" +
                $"   {xpi}\n\n" +
                "Bu kanalda eklenti Firefox kapanınca da kalır.";
            return true;
        }

        private static void TryRevealFile(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
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
            LaunchFirefox(exe, quotedXpi);
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

            string boot = channelId is "firefox-developer" or "firefox-dev" or "developer"
                ? "firefox-developer"
                : "firefox";
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

        private static string DetectChannel(string exePath)
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
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            });
        }

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
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Mozilla", "Firefox");
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
        {
            try
            {
                string id = FirefoxAddonId;
                foreach (string profile in EnumerateFirefoxProfiles())
                {
                    bool isDev = IsDeveloperProfile(profile);
                    if (developerProfiles == true && !isDev) continue;
                    if (developerProfiles == false && isDev) continue;
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
