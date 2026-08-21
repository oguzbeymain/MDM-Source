using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DownloadMuck
{
    /// <summary>
    /// Eklentiyi AppData'ya kopyalar; gelistirici modu, registry/policy ve CRX paketleme ile
    /// tarayici kurulumunu otomatiklestirmeye calisir (IDM benzeri best-effort).
    /// </summary>
    public static class ExtensionInstaller
    {
        public static string InstallRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MuckDownloadManager", "BrowserExtension");

        public static void EnsureInstalled()
        {
            try
            {
                string? source = ResolveExtensionSource();
                if (source == null)
                {
                    Debug.WriteLine("ExtensionInstaller: kaynak klasor bulunamadi");
                    return;
                }

                Directory.CreateDirectory(InstallRoot);
                foreach (string name in new[] { "manifest.json", "background.js" })
                {
                    string src = Path.Combine(source, name);
                    if (File.Exists(src))
                        File.Copy(src, Path.Combine(InstallRoot, name), overwrite: true);
                }

                WriteLoaderHtml();
                WriteUpdateXml(Path.Combine(InstallRoot, "update.xml"));

                EnableDeveloperMode("Google\\Chrome\\User Data");
                EnableDeveloperMode("Microsoft\\Edge\\User Data");
                EnableDeveloperMode("BraveSoftware\\Brave-Browser\\User Data");
                EnableDeveloperMode("Vivaldi\\User Data");

                WriteExternalExtensionsJson(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Google\\Chrome\\User Data\\External Extensions"));
                WriteExternalExtensionsJson(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Microsoft\\Edge\\User Data\\External Extensions"));
                WriteExternalExtensionsJson(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BraveSoftware\\Brave-Browser\\User Data\\External Extensions"));

                WriteRegistryPath(@"Software\Google\Chrome\Extensions");
                WriteRegistryPath(@"Software\Microsoft\Edge\Extensions");
                WriteRegistryPath(@"Software\BraveSoftware\Brave\Extensions");

                WriteForceInstallPolicy(@"Software\Policies\Google\Chrome\ExtensionInstallForcelist");
                WriteForceInstallPolicy(@"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist");
                WriteForceInstallPolicy(@"Software\Policies\BraveSoftware\Brave\ExtensionInstallForcelist");

                TrySetInstallSources(@"Software\Policies\Google\Chrome\ExtensionInstallSources");
                TrySetInstallSources(@"Software\Policies\Microsoft\Edge\ExtensionInstallSources");

                TryPackCrx();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtensionInstaller: {ex.Message}");
            }
        }

        private static string? ResolveExtensionSource()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "DownloadMuck_Eklenti"),
                Path.Combine(baseDir, "BrowserExtension"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "DownloadMuck_Eklenti")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "DownloadMuck_Eklenti"))
            };

            foreach (string dir in candidates)
            {
                if (File.Exists(Path.Combine(dir, "manifest.json")) &&
                    File.Exists(Path.Combine(dir, "background.js")))
                    return dir;
            }

            return null;
        }

        private static void EnableDeveloperMode(string relativeUserData)
        {
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                relativeUserData);
            if (!Directory.Exists(userData)) return;

            foreach (string profileDir in Directory.GetDirectories(userData))
            {
                string prefsPath = Path.Combine(profileDir, "Preferences");
                if (!File.Exists(prefsPath)) continue;
                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(prefsPath, Encoding.UTF8));
                    if (root is not JsonObject obj) continue;
                    obj["extensions"] ??= new JsonObject();
                    var extensions = obj["extensions"]!.AsObject();
                    extensions["ui"] ??= new JsonObject();
                    extensions["ui"]!["developer_mode"] = true;

                    string tmp = prefsPath + ".mdm.tmp";
                    File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), Encoding.UTF8);
                    File.Copy(tmp, prefsPath, overwrite: true);
                    try { File.Delete(tmp); } catch { /* ignore */ }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Prefs {prefsPath}: {ex.Message}");
                }
            }
        }

        private static void WriteExternalExtensionsJson(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string id = "abcdefghijklmnopqrstuvwxyzabcdef";
                string crx = Path.Combine(InstallRoot, "mdm.crx");
                string json = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["external_version"] = "1.5",
                    ["external_crx"] = crx
                });
                File.WriteAllText(Path.Combine(folder, id + ".json"), json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExternalExtensions: {ex.Message}");
            }
        }

        private static void WriteRegistryPath(string subKey)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{subKey}\abcdefghijklmnopqrstuvwxyzabcdef");
                key?.SetValue("path", InstallRoot, RegistryValueKind.String);
                key?.SetValue("version", "1.5", RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Registry: {ex.Message}");
            }
        }

        private static void WriteForceInstallPolicy(string subKey)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(subKey);
                string updateXml = Path.Combine(InstallRoot, "update.xml");
                string fileUrl = new Uri(updateXml).AbsoluteUri;
                key?.SetValue("1", $"abcdefghijklmnopqrstuvwxyzabcdef;{fileUrl}", RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Policy: {ex.Message}");
            }
        }

        private static void TrySetInstallSources(string subKey)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(subKey);
                key?.SetValue("1", "file://*", RegistryValueKind.String);
                key?.SetValue("2", "http://127.0.0.1:*", RegistryValueKind.String);
            }
            catch { /* ignore */ }
        }

        private static void WriteUpdateXml(string path)
        {
            string crx = Path.Combine(InstallRoot, "mdm.crx").Replace('\\', '/');
            string xml = $"""
                <?xml version='1.0' encoding='UTF-8'?>
                <gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>
                  <app appid='abcdefghijklmnopqrstuvwxyzabcdef'>
                    <updatecheck codebase='file:///{crx}' version='1.5' />
                  </app>
                </gupdate>
                """;
            File.WriteAllText(path, xml);
        }

        private static void WriteLoaderHtml()
        {
            string html = $$"""
                <!DOCTYPE html>
                <html><head><meta charset="utf-8"><title>MDM Eklenti</title>
                <style>
                body{font-family:Segoe UI,sans-serif;background:#1a1a1a;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
                .card{background:#222;border:1px solid #333;border-radius:14px;padding:28px;max-width:420px}
                code{background:#111;padding:4px 8px;border-radius:6px;color:#ff6b00}
                </style></head><body><div class="card">
                <h2 style="color:#ff6b00;margin-top:0">MDM Eklenti</h2>
                <p>Gerekirse Chrome/Edge → Eklentiler → Geliştirici modu → Paketlenmemiş yükle:</p>
                <p><code>{{InstallRoot}}</code></p>
                </div></body></html>
                """;
            File.WriteAllText(Path.Combine(InstallRoot, "KURULUM.html"), html);
        }

        private static void TryPackCrx()
        {
            try
            {
                string? browser = FindBrowser(
                    @"Google\Chrome\Application\chrome.exe",
                    @"Microsoft\Edge\Application\msedge.exe");
                if (browser == null) return;

                string packDir = Path.Combine(InstallRoot, "_pack");
                Directory.CreateDirectory(packDir);
                File.Copy(Path.Combine(InstallRoot, "manifest.json"), Path.Combine(packDir, "manifest.json"), true);
                File.Copy(Path.Combine(InstallRoot, "background.js"), Path.Combine(packDir, "background.js"), true);

                string pem = Path.Combine(InstallRoot, "mdm.pem");
                string args = File.Exists(pem)
                    ? $"--pack-extension=\"{packDir}\" --pack-extension-key=\"{pem}\" --no-message-box"
                    : $"--pack-extension=\"{packDir}\" --no-message-box";

                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = browser,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                p?.WaitForExit(25000);

                string produced = Path.Combine(InstallRoot, "_pack.crx");
                string dest = Path.Combine(InstallRoot, "mdm.crx");
                if (File.Exists(produced))
                    File.Copy(produced, dest, overwrite: true);

                string producedPem = Path.Combine(InstallRoot, "_pack.pem");
                if (File.Exists(producedPem))
                    File.Copy(producedPem, pem, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Pack CRX: {ex.Message}");
            }
        }

        private static string? FindBrowser(params string[] relativePaths)
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };

            foreach (string rel in relativePaths)
            {
                foreach (string root in roots)
                {
                    string path = Path.Combine(root, rel);
                    if (File.Exists(path)) return path;
                }
            }

            return null;
        }
    }
}
