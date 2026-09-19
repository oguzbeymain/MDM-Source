using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MDM.Setup
{
    public sealed class InstallOptions
    {
        public string Language { get; set; } = "tr";
        public string InstallDir { get; set; } = DefaultInstallDir;
        public string DownloadDir { get; set; } = DefaultDownloadDir;
        public bool DarkTheme { get; set; } = true;
        public bool DesktopShortcut { get; set; } = true;
        public bool StartMenuShortcut { get; set; } = true;
        public bool AutoStart { get; set; } = true;
        public bool CreateCategoryFolders { get; set; } = true;
        /// <summary>
        /// Sessiz yükseltmede kullanılır: mevcut settings.json değerleri (tema, dil,
        /// indirme klasörü) korunur, yalnızca eksik anahtarlar yazılır.
        /// </summary>
        public bool PreserveExistingSettings { get; set; }

        public static string DefaultInstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", Installer.AppName);

        public static string DefaultDownloadDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static class Installer
    {
        public const string AppName = "MuckDownloadManager";
        public const string ExeName = "MDM.exe";
        /// <summary>Kurulum klasöründe kalan tek yardımcı: çift tıklanınca kaldırma başlar.</summary>
        public const string UninstallExeName = "Uninstall.exe";
        public const string Publisher = "MuckDownloadManager";
        /// <summary>Derleme sürümünden okunur; elle yazılınca her sürümde kayıyordu.</summary>
        public static readonly string Version =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        private const string UninstallKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MuckDownloadManager";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static readonly string[] BuiltinIds = { "Documents", "Videos", "Audio", "Archives", "Images", "Apps" };

        /// <summary>Uygulama verisi (ayarlar, kategoriler) klasörü — uygulamayla aynı yol.</summary>
        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

        public static bool HasPayload
        {
            get
            {
                using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
                return s != null;
            }
        }

        /// <summary>Zaten kurulu sürümün klasörü (kayıt defterinden).</summary>
        public static string? FindExistingInstall()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
                string? dir = key?.GetValue("InstallLocation") as string;
                return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
            }
            catch { return null; }
        }

        /// <summary>Kayıt defterindeki kurulu sürüm; bakım ekranı bunu gösterir.</summary>
        public static string? ReadInstalledVersion()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
                string? version = key?.GetValue("DisplayVersion") as string;
                return string.IsNullOrWhiteSpace(version) ? null : version;
            }
            catch { return null; }
        }

        /// <summary>Kurulu uygulamanın UI dili; kaldırma sihirbazı aynı dilde açılır.</summary>
        public static string? ReadInstalledLanguage() => ReadSetting("UiLanguage");

        /// <summary>Kurulu uygulamanın indirme klasörü; onarımda korunur.</summary>
        public static string? ReadInstalledDownloadFolder() => ReadSetting("DefaultDownloadFolder");

        /// <summary>Kurulu tema ("Dark"/"Light"); bakım ekranı aynı görünümle açılır.</summary>
        public static string? ReadInstalledTheme() => ReadSetting("Theme");

        private static string? ReadSetting(string key)
        {
            try
            {
                string path = Path.Combine(DataDir, "settings.json");
                if (!File.Exists(path)) return null;
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return null;
                string? value = root[key]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch { return null; }
        }

        public static async Task InstallAsync(InstallOptions options, IProgress<(string Step, double Percent)> progress)
        {
            progress.Report((SetupLoc.T("setup.step_close", "Açık uygulama kapatılıyor…"), 0.02));
            await Task.Run(() => CloseRunningApp()).ConfigureAwait(false);

            await Task.Run(() =>
            {
                Directory.CreateDirectory(options.InstallDir);

                progress.Report((SetupLoc.T("setup.step_extract", "Dosyalar kopyalanıyor…"), 0.08));
                CleanParkedFiles(options.InstallDir);
                RemoveForeignArchitectureFiles(options.InstallDir);
            }).ConfigureAwait(false);

            await ExtractPayloadAsync(options.InstallDir, progress).ConfigureAwait(false);

            await Task.Run(() =>
            {
                progress.Report((SetupLoc.T("setup.step_settings", "Ayarlar hazırlanıyor…"), 0.86));
                CopySelf(options.InstallDir);
                SeedSettings(options);
                SeedCategories(options);

                if (options.CreateCategoryFolders)
                {
                    progress.Report((SetupLoc.T("setup.step_folders", "Kategori klasörleri oluşturuluyor…"), 0.92));
                    CreateCategoryFolders(options);
                }

                progress.Report((SetupLoc.T("setup.step_shortcuts", "Kısayollar oluşturuluyor…"), 0.96));
                CreateShortcuts(options);
                WriteUninstallEntry(options);
            }).ConfigureAwait(false);

            progress.Report((SetupLoc.T("setup.step_done", "Tamamlanıyor…"), 1.0));
        }

        public static async Task UninstallAsync(bool removeData, IProgress<(string Step, double Percent)> progress)
        {
            progress.Report((SetupLoc.T("setup.step_close", "Açık uygulama kapatılıyor…"), 0.1));
            await Task.Run(() => CloseRunningApp()).ConfigureAwait(false);

            await Task.Run(() =>
            {
                progress.Report((SetupLoc.T("setup.step_remove_shortcuts", "Kısayollar kaldırılıyor…"), 0.35));
                RemoveShortcuts();
                RemoveAutoStart();

                progress.Report((SetupLoc.T("setup.step_remove_files", "Dosyalar kaldırılıyor…"), 0.6));
                // Setup'ın bakım ekranından çalıştırıldığında süreç kurulum klasöründe
                // olmaz; hedef her zaman kayıtlı kurulum klasörü olmalı
                string dir = FindExistingInstall()
                    ?? Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                RemoveInstallFiles(dir);

                if (removeData)
                {
                    progress.Report((SetupLoc.T("setup.step_remove_data", "Ayarlar siliniyor…"), 0.85));
                    TryDeleteDirectory(DataDir);
                }

                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); }
                catch { /* kayıt yoksa sorun değil */ }
            }).ConfigureAwait(false);

            progress.Report((SetupLoc.T("setup.step_done", "Tamamlanıyor…"), 1.0));
        }

        public static void LaunchApp(string installDir)
        {
            try
            {
                string exe = Path.Combine(installDir, ExeName);
                if (File.Exists(exe))
                    Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDir, UseShellExecute = true });
            }
            catch { /* başlatılamazsa kullanıcı kısayoldan açar */ }
        }

        // --- Adımlar ---

        private static void CloseRunningApp()
        {
            foreach (string name in new[] { "MDM", "MDM.Updater" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                        if (!p.WaitForExit(4000))
                        {
                            p.Kill(entireProcessTree: true);
                            // Kill asenkron: beklemezsek dosyalar hâlâ kilitliyken kopyalamaya başlarız
                            p.WaitForExit(4000);
                        }
                    }
                    catch { /* erişilemeyen süreç */ }
                    finally { p.Dispose(); }
                }
            }
        }

        private static async Task ExtractPayloadAsync(string targetDir, IProgress<(string, double)> progress)
        {
            using Stream? embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if (embedded != null)
            {
                ExtractZipStream(embedded, targetDir, progress, 0.08, 0.86);
                return;
            }

            progress.Report((SetupLoc.T("setup.step_download", "Kurulum paketi indiriliyor…"), 0.08));
            string url = $"https://github.com/oguzbeymain/MDM-App/releases/download/v{Version}/MDM-{Version}-win-x64.zip";
            string tempZip = Path.Combine(Path.GetTempPath(), $"mdm-payload-{Version}.zip");
            try
            {
                await DownloadFileAsync(url, tempZip, progress).ConfigureAwait(false);
                await using FileStream fs = File.OpenRead(tempZip);
                ExtractZipStream(fs, targetDir, progress, 0.55, 0.86);
            }
            finally
            {
                try { File.Delete(tempZip); } catch { /* ignore */ }
            }
        }

        private static async Task DownloadFileAsync(string url, string dest, IProgress<(string, double)> progress)
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "MDM-Setup");
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using Stream src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using FileStream dst = File.Create(dest);
            byte[] buffer = new byte[128 * 1024];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                read += n;
                if (total > 0)
                {
                    double pct = 0.08 + 0.45 * read / total;
                    progress.Report((SetupLoc.T("setup.step_download", "Kurulum paketi indiriliyor…"), pct));
                }
            }
        }

        private static void ExtractZipStream(
            Stream zipStream, string targetDir, IProgress<(string, double)> progress, double fromPct, double toPct)
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = zip.Entries;
            int done = 0;

            foreach (ZipArchiveEntry entry in entries)
            {
                string full = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                if (!full.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                    continue; // zip slip

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(full);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    ExtractOverLockedFile(entry, full);
                }

                done++;
                if (done % 8 == 0 || done == entries.Count)
                {
                    double pct = fromPct + (toPct - fromPct) * done / Math.Max(1, entries.Count);
                    progress.Report((SetupLoc.T("setup.step_extract", "Dosyalar kopyalanıyor…"), pct));
                }
            }
        }

        /// <summary>
        /// Onarım/güncellemede hedef dosya hâlâ kullanımda olabilir (dll'ler uygulama
        /// kapandıktan sonra da kısa süre kilitli kalır). Windows kilitli dosyayı silmeye
        /// izin vermez ama yeniden adlandırmaya izin verir; eski kopya .old olarak
        /// kenara çekilip yeni dosya yazılır, kalıntılar sonraki kurulumda silinir.
        /// </summary>
        private static void ExtractOverLockedFile(ZipArchiveEntry entry, string target)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    entry.ExtractToFile(target, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    if (attempt == 0) { Thread.Sleep(250); continue; }

                    string parked = target + ".old-" + Guid.NewGuid().ToString("N")[..6];
                    try { File.Move(target, parked); }
                    catch (IOException) { Thread.Sleep(400); continue; }
                    TryDeleteFile(parked);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    Thread.Sleep(250);
                }
            }
        }

        /// <summary>
        /// 32-bit kurulumun üzerine 64-bit sürüm gelirse (veya tersi) klasörde kalan eski
        /// mimarideki .dll'ler BadImageFormatException'a yol açar. Mimari değiştiyse uygulama
        /// dosyaları silinir; kullanıcı ayarları ayrı klasörde olduğu için etkilenmez.
        /// </summary>
        private static void RemoveForeignArchitectureFiles(string installDir)
        {
            try
            {
                string installed = Path.Combine(installDir, ExeName);
                if (!File.Exists(installed)) return;

                ushort? existing = ReadPeMachine(installed);
                ushort? incoming = ReadPeMachine(Environment.ProcessPath ?? "");
                if (existing == null || incoming == null || existing == incoming) return;

                foreach (string file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(file, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (file.EndsWith(UninstallExeName, StringComparison.OrdinalIgnoreCase)) continue;
                    TryDeleteFile(file);
                }
            }
            catch { /* okunamazsa normal kurulum akışı devam eder */ }
        }

        /// <summary>PE başlığındaki makine tipi: 0x8664 = x64, 0x014C = x86, 0xAA64 = arm64.</summary>
        private static ushort? ReadPeMachine(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = File.OpenRead(path);
                using var reader = new BinaryReader(fs);
                if (reader.ReadUInt16() != 0x5A4D) return null;   // MZ

                fs.Position = 0x3C;
                fs.Position = reader.ReadInt32();
                if (reader.ReadUInt32() != 0x00004550) return null; // PE\0\0
                return reader.ReadUInt16();
            }
            catch { return null; }
        }

        /// <summary>Kilitli dosya yüzünden kenara çekilmiş eski kopyalar.</summary>
        private static void CleanParkedFiles(string installDir)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(installDir, "*.old-*", SearchOption.AllDirectories))
                    TryDeleteFile(file);
            }
            catch { /* temizlik başarısız olsa da kurulum çalışır */ }
        }

        private static void CopySelf(string installDir)
        {
            try
            {
                string self = Environment.ProcessPath ?? "";
                string target = Path.Combine(installDir, UninstallExeName);
                if (self.Length > 0 && !string.Equals(self, target, StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, target, overwrite: true);

                // Kurulum klasöründe setup kopyası kalmasın; sürüm ekli adlar da (MDM-Setup-1.0.36.exe) temizlenir
                foreach (string legacy in Directory.EnumerateFiles(installDir, "MDM-Setup*.exe"))
                {
                    if (string.Equals(legacy, self, StringComparison.OrdinalIgnoreCase)) continue;
                    TryDeleteFile(legacy);
                }
            }
            catch { /* kaldırıcı kopyalanamazsa kurulum yine geçerli */ }
        }

        private static void SeedSettings(InstallOptions options)
        {
            Directory.CreateDirectory(DataDir);
            string path = Path.Combine(DataDir, "settings.json");

            JsonObject root = ReadJsonObject(path) ?? new JsonObject();

            void Set(string key, JsonNode value)
            {
                // Yükseltmede kullanıcının seçimleri kalır; ilk kurulumda sihirbaz kazanır
                if (options.PreserveExistingSettings && root.ContainsKey(key)) return;
                root[key] = value;
            }

            Set("UiLanguage", options.Language);
            Set("Theme", options.DarkTheme ? "Dark" : "Light");
            Set("DefaultDownloadFolder", options.DownloadDir);
            Set("AutoCreateCategoryFolders", options.CreateCategoryFolders);
            Set("AutoStart", options.AutoStart);
            // Delete tuşu kısayolu kurulumda açık gelsin
            Set("DeleteKeyShortcutsEnabled", true);

            Directory.CreateDirectory(options.DownloadDir);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Kategori adları ve disk klasörleri seçilen dile göre yazılır. Kullanıcının
        /// eklediği kategoriler korunur; yalnızca yerleşik olanlar güncellenir.
        /// </summary>
        private static void SeedCategories(InstallOptions options)
        {
            Directory.CreateDirectory(DataDir);
            string path = Path.Combine(DataDir, "categories.json");

            JsonArray list = ReadJsonArray(path) ?? new JsonArray();
            var byId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode? node in list)
            {
                if (node is JsonObject obj && obj["Id"]?.GetValue<string>() is string id)
                    byId[id] = obj;
            }

            var result = new JsonArray();
            foreach (JsonNode? node in list)
            {
                if (node is JsonObject obj && obj["Id"]?.GetValue<string>() is string id
                    && (id.Equals("All", StringComparison.OrdinalIgnoreCase) || BuiltinIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
                    continue; // yerleşikler aşağıda yeniden yazılır
                if (node != null)
                    result.Add(node.DeepClone());
            }

            var all = new JsonObject
            {
                ["Id"] = "All",
                ["Name"] = LocalizedCategoryName(options.Language, "All"),
                ["Icon"] = "⚡",
                ["IsBuiltin"] = true,
                ["ParentId"] = null,
                ["IsExpanded"] = true,
                ["Extensions"] = null,
                ["CustomFolderPath"] = null
            };
            result.Insert(0, all);

            int index = 1;
            foreach (string id in BuiltinIds)
            {
                string name = LocalizedCategoryName(options.Language, id);
                string folder = Path.Combine(options.DownloadDir, SanitizeFolderName(name));

                var extensions = new JsonArray();
                foreach (string ext in DefaultExtensions(id))
                    extensions.Add(ext);

                var obj = new JsonObject
                {
                    ["Id"] = id,
                    ["Name"] = name,
                    ["Icon"] = DefaultIcon(id),
                    ["IsBuiltin"] = true,
                    ["ParentId"] = null,
                    ["IsExpanded"] = byId.TryGetValue(id, out JsonObject? old)
                        && old["IsExpanded"]?.GetValue<bool>() is bool exp ? exp : true,
                    ["Extensions"] = extensions,
                    ["CustomFolderPath"] = folder
                };
                result.Insert(index++, obj);
            }

            File.WriteAllText(path, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void CreateCategoryFolders(InstallOptions options)
        {
            Directory.CreateDirectory(options.DownloadDir);
            foreach (string id in BuiltinIds)
            {
                try
                {
                    string name = SanitizeFolderName(LocalizedCategoryName(options.Language, id));
                    Directory.CreateDirectory(Path.Combine(options.DownloadDir, name));
                }
                catch { /* oluşturulamayan klasör uygulama açılışında yeniden denenir */ }
            }
        }

        private static void CreateShortcuts(InstallOptions options)
        {
            string exe = Path.Combine(options.InstallDir, ExeName);
            if (!File.Exists(exe)) return;

            if (options.DesktopShortcut)
                CreateShortcut(Path.Combine(DesktopDir, AppName + ".lnk"), exe, options.InstallDir);

            if (options.StartMenuShortcut)
            {
                Directory.CreateDirectory(StartMenuDir);
                CreateShortcut(Path.Combine(StartMenuDir, AppName + ".lnk"), exe, options.InstallDir);
            }
        }

        private static void RemoveShortcuts()
        {
            TryDeleteFile(Path.Combine(DesktopDir, AppName + ".lnk"));
            TryDeleteFile(Path.Combine(StartMenuDir, AppName + ".lnk"));
        }

        private static void RemoveAutoStart()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null) return;
                foreach (string name in new[] { "MDM", AppName })
                {
                    if (key.GetValue(name) != null) key.DeleteValue(name, throwOnMissingValue: false);
                }
            }
            catch { /* erişilemezse bırak */ }
        }

        private static void RemoveInstallFiles(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return;

            string self = Environment.ProcessPath ?? "";
            foreach (string file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase)) continue;
                TryDeleteFile(file);
            }
            foreach (string dir in Directory.EnumerateDirectories(installDir))
                TryDeleteDirectory(dir);

            // Çalışan kaldırıcı kendi dosyasını silemez — çıkıştan sonra temizlenir
            ScheduleSelfDelete(installDir);
        }

        private static void ScheduleSelfDelete(string installDir)
        {
            try
            {
                // Kaldırıcı kapanana kadar dosya kilitli olabilir; 15 saniye boyunca denenir
                Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c for /l %i in (1,1,15) do (timeout /t 1 /nobreak > nul & " +
                    $"rd /s /q \"{installDir}\" > nul 2>&1 & if not exist \"{installDir}\" exit)")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { /* kalan tek dosya kullanıcıya zarar vermez */ }
        }

        private static void WriteUninstallEntry(InstallOptions options)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true);
                string uninstaller = Path.Combine(options.InstallDir, UninstallExeName);

                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", Publisher);
                key.SetValue("DisplayIcon", Path.Combine(options.InstallDir, ExeName));
                key.SetValue("InstallLocation", options.InstallDir);
                // Denetim Masası / Ayarlar > Uygulamalar buradan Uninstall.exe'yi açar
                key.SetValue("UninstallString", $"\"{uninstaller}\"");
                key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /silent");
                // Ayarlar > Uygulamalar'daki Değiştir düğmesi onar/değiştir/kaldır ekranını açar
                key.SetValue("ModifyPath", $"\"{uninstaller}\" /maintenance");
                key.SetValue("NoModify", 0, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", DirectorySizeKb(options.InstallDir), RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            catch { /* kayıt yazılamazsa kurulum yine çalışır */ }
        }

        // --- Yardımcılar ---

        private static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        private static string StartMenuDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");

        private static void CreateShortcut(string linkPath, string exePath, string workingDir)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object? shell = Activator.CreateInstance(shellType);
                if (shell == null) return;

                object? link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { linkPath });
                if (link == null) return;

                Type linkType = link.GetType();
                void Set(string name, object value) => linkType.InvokeMember(name,
                    BindingFlags.SetProperty, null, link, new[] { value });

                Set("TargetPath", exePath);
                Set("WorkingDirectory", workingDir);
                Set("IconLocation", exePath + ",0");
                Set("Description", AppName);
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch { /* kısayol oluşturulamazsa kurulum sürer */ }
        }

        private static string LocalizedCategoryName(string language, string id)
        {
            var map = SetupLoc.MapFor(language);
            string key = id switch
            {
                "All" => "cat.all",
                "Documents" => "cat.documents",
                "Videos" => "cat.videos",
                "Audio" => "cat.audio",
                "Archives" => "cat.archives",
                "Images" => "cat.images",
                "Apps" => "cat.apps",
                _ => ""
            };
            if (key.Length > 0 && map.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v))
                return v;

            return id switch
            {
                "All" => "Tüm İndirilenler",
                "Documents" => "Dökümanlar",
                "Videos" => "Videolar",
                "Audio" => "Sesler",
                "Archives" => "Arşivler",
                "Images" => "Resimler",
                "Apps" => "Uygulamalar",
                _ => id
            };
        }

        private static string DefaultIcon(string id) => id switch
        {
            "Documents" => "📁",
            "Videos" => "🎬",
            "Audio" => "🎵",
            "Archives" => "📦",
            "Images" => "📷",
            "Apps" => "🚀",
            _ => "📁"
        };

        private static string[] DefaultExtensions(string id) => id switch
        {
            "Documents" => new[] { "pdf", "doc", "docx", "txt", "rtf", "odt", "xls", "xlsx", "ppt", "pptx", "csv", "md" },
            "Videos" => new[] { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpeg", "mpg" },
            "Audio" => new[] { "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "opus" },
            "Archives" => new[] { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab", "torrent" },
            "Images" => new[] { "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "ico", "tif", "tiff", "heic", "avif", "jfif" },
            "Apps" => new[] { "exe", "msi", "apk", "bat", "cmd", "msix", "appx", "dmg" },
            _ => Array.Empty<string>()
        };

        /// <summary>Uygulamanın CategoryStore.SanitizeFolderName davranışıyla aynı.</summary>
        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Kategori";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(name) ? "Kategori" : name;
        }

        private static JsonObject? ReadJsonObject(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
            catch { return null; }
        }

        private static JsonArray? ReadJsonArray(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonNode.Parse(File.ReadAllText(path)) as JsonArray;
            }
            catch { return null; }
        }

        private static int DirectorySizeKb(string dir)
        {
            try
            {
                long bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                return (int)Math.Min(int.MaxValue, bytes / 1024);
            }
            catch { return 0; }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* kilitli dosya */ }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch { /* kilitli klasör */ }
        }
    }
}
