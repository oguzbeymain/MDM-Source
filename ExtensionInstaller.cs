using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        /// Firefox'a eklenti kurulumunu başlatır.
        /// Release: geçici (about:debugging). Developer/Nightly/ESR: kalıcı XPI.
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

                string? firefoxExe;
                string channel;
                if (mode == FirefoxInstallMode.Permanent)
                {
                    firefoxExe = ResolveFirefoxDeveloperExe(out channel);
                    if (string.IsNullOrWhiteSpace(firefoxExe))
                    {
                        message = "Firefox Developer Edition bulunamadı.";
                        detail = "Önce Developer Edition’ı kur, sonra tekrar dene.\n"
                                 + DeveloperEditionDownloadUrl;
                        return false;
                    }
                }
                else
                {
                    firefoxExe = ResolveFirefoxExe(out channel);
                    if (string.IsNullOrWhiteSpace(firefoxExe))
                    {
                        message = "Firefox yüklü değil.";
                        detail = "Mozilla Firefox veya Firefox Developer Edition kurun, sonra tekrar deneyin.";
                        return false;
                    }
                }

                PrepareFirefoxStaging();
                PackFirefoxXpi();

                bool preferPermanent = mode == FirefoxInstallMode.Permanent
                    || (mode == FirefoxInstallMode.Auto && channel is "developer" or "nightly" or "esr");
                // Geçici zorlandıysa Release akışı
                if (mode == FirefoxInstallMode.Temporary)
                    preferPermanent = false;

                string staging = FirefoxStagingRoot;
                string xpi = FirefoxXpiPath;
                string manifest = Path.Combine(staging, "manifest.json");

                TrySideloadXpiIntoProfiles(xpi);

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{manifest}\"",
                        UseShellExecute = true
                    });
                }
                catch { /* ignore */ }

                if (preferPermanent)
                {
                    TryRelaxSignaturePref();
                    LaunchFirefox(firefoxExe, $"\"{xpi}\"");
                    LaunchFirefox(firefoxExe, "about:addons");

                    title = "Firefox eklentisi";
                    message = "Kalıcı kurulum — eklentiyi onaylayın.";
                    detail =
                        $"Kanal: {ChannelLabel(channel)}\n\n" +
                        "1) Açılan pencerede «Ekle» deyin (imza uyarısı normal).\n" +
                        "2) Gelmezse: about:addons → dişli → «Dosyadan eklenti yükle» →\n" +
                        $"   {xpi}\n\n" +
                        "Bu kanalda eklenti Firefox kapanınca da kalır.";
                    return true;
                }

                // Geçici: about:debugging
                LaunchFirefox(firefoxExe, "about:debugging#/runtime/this-firefox");

                title = "Firefox eklentisi";
                message = "Geçici kurulum — bir adım kaldı.";
                detail =
                    "1) Açılan sayfada «Geçici eklenti yükle»ye tıkla.\n" +
                    "2) Explorer’da seçili manifest.json dosyasını seç:\n" +
                    $"   {manifest}\n\n" +
                    "İzinleri onayla. Bu oturumda çalışır; Firefox kapanınca silinir.\n" +
                    "Kalıcı kurulum için Firefox Developer Edition kullan.";
                return true;
            }
            catch (Exception ex)
            {
                message = "Kurulum başlatılamadı.";
                detail = ex.Message;
                return false;
            }
        }

        public static bool IsDeveloperEditionInstalled()
            => !string.IsNullOrWhiteSpace(ResolveFirefoxDeveloperExe(out _));

        public static string? ResolveFirefoxDeveloperExe(out string channel)
        {
            channel = "developer";
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] candidates =
            {
                Path.Combine(local, "Firefox Developer Edition", "firefox.exe"),
                Path.Combine(pf, "Firefox Developer Edition", "firefox.exe"),
                Path.Combine(local, "Firefox Nightly", "firefox.exe"),
                Path.Combine(pf, "Firefox Nightly", "firefox.exe"),
            };
            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                channel = DetectChannel(path);
                return path;
            }
            return null;
        }

        public static void PrepareFirefoxStaging()
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

            WriteFirefoxManifest(Path.Combine(dst, "manifest.json"));
        }

        public static void PackFirefoxXpi()
        {
            PrepareFirefoxStaging();
            string staging = FirefoxStagingRoot;
            string xpi = FirefoxXpiPath;
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
                    ["strict_min_version"] = "115.0"
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

        public static string? ResolveFirefoxExe(out string channel)
        {
            channel = "release";
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            // Önce kullanıcının varsayılan Firefox'u (App Paths) — yanlışlıkla Dev'e sapma
            string? fromReg = ReadAppPath(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe");
            if (!string.IsNullOrWhiteSpace(fromReg) && File.Exists(fromReg))
            {
                channel = DetectChannel(fromReg);
                return fromReg;
            }

            var ordered = new (string Path, string Channel)[]
            {
                (Path.Combine(pf, "Mozilla Firefox", "firefox.exe"), "release"),
                (Path.Combine(pf86, "Mozilla Firefox", "firefox.exe"), "release"),
                (Path.Combine(local, "Mozilla Firefox", "firefox.exe"), "release"),
                (Path.Combine(local, "Firefox Developer Edition", "firefox.exe"), "developer"),
                (Path.Combine(pf, "Firefox Developer Edition", "firefox.exe"), "developer"),
                (Path.Combine(local, "Firefox Nightly", "firefox.exe"), "nightly"),
                (Path.Combine(pf, "Firefox Nightly", "firefox.exe"), "nightly"),
            };

            foreach (var (path, ch) in ordered)
            {
                if (File.Exists(path))
                {
                    channel = ch;
                    return path;
                }
            }

            return null;
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
                UseShellExecute = true
            });
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
        public static int TrySideloadXpiIntoProfiles(string xpiPath)
        {
            if (!File.Exists(xpiPath)) return 0;
            int n = 0;
            foreach (string profile in EnumerateFirefoxProfiles())
            {
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

        public static bool FirefoxExtensionPresentOnDisk()
        {
            try
            {
                string id = FirefoxAddonId;
                foreach (string profile in EnumerateFirefoxProfiles())
                {
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
