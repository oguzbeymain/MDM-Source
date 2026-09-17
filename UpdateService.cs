using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MDM
{
    public sealed class UpdateCheckResult
    {
        public bool IsUpToDate { get; init; }
        public bool UpdateAvailable { get; init; }
        public bool Applying { get; init; }
        public bool HadError { get; init; }
        public string Message { get; init; } = "";
        public string? RemoteVersion { get; init; }
    }

    /// <summary>
    /// Uygulama içi güncelleme denetimi — açılışta değil, ayarlardan tetiklenir.
    /// </summary>
    public static class UpdateService
    {
        public const string ReleasesLatestUrl = "https://api.github.com/repos/oguzbeymain/MDM-App/releases/latest";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MDM", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static string CurrentVersionText
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        /// <summary>
        /// Zip ile güncellemede setup çalışmaz; kayıt defterindeki sürüm eski kalıyordu.
        /// Açılışta girdi varsa sürüm ve konum tazelenir (Uygulamalar ve özellikler doğru gösterir).
        /// </summary>
        public static void SyncInstallRegistryVersion()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MuckDownloadManager", writable: true);
                if (key == null) return;

                string appDir = AppContext.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (key.GetValue("InstallLocation") as string is not { Length: > 0 } dir
                    || !string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), appDir, StringComparison.OrdinalIgnoreCase))
                    return; // başka bir kopya çalışıyor; kurulu sürümün girdisine dokunma

                if (key.GetValue("DisplayVersion") as string == CurrentVersionText) return;
                key.SetValue("DisplayVersion", CurrentVersionText);
            }
            catch { /* kayıt defteri yazılamazsa güncelleme yine geçerli */ }
        }

        public static async Task<UpdateCheckResult> CheckAndApplyAsync(
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Version current = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

            try
            {
                status?.Report("Güncelleme kontrol ediliyor…");

                using var response = await Http.GetAsync(ReleasesLatestUrl, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new UpdateCheckResult
                    {
                        IsUpToDate = true,
                        Message = "Henüz yayınlanmış sürüm yok."
                    };
                }

                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                Version? remote = ParseVersion(tagName);
                if (remote == null)
                {
                    return new UpdateCheckResult
                    {
                        HadError = true,
                        Message = $"Sürüm etiketi okunamadı: {tagName}"
                    };
                }

                if (remote <= current)
                {
                    return new UpdateCheckResult
                    {
                        IsUpToDate = true,
                        Message = $"Uygulama güncel (v{current.Major}.{current.Minor}.{current.Build})."
                    };
                }

                if (!TryPickAsset(doc.RootElement, out string assetName, out string downloadUrl, out PackageKind kind))
                {
                    return new UpdateCheckResult
                    {
                        HadError = true,
                        Message = "Yayın paketinde .zip/.exe bulunamadı."
                    };
                }

                string remoteText = $"{remote.Major}.{remote.Minor}.{remote.Build}";
                status?.Report($"v{remoteText} indiriliyor…");

                string tempRoot = Path.Combine(Path.GetTempPath(), "MDM-Update", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);
                string downloadPath = Path.Combine(tempRoot, assetName);
                await DownloadFileAsync(downloadUrl, downloadPath, status, cancellationToken);

                // Setup paketi dosya kopyalanarak kurulamaz; kendi sessiz kurulumunu çalıştırır
                if (kind == PackageKind.Installer)
                {
                    status?.Report("Kurulum başlatılıyor, uygulama yeniden başlatılacak…");
                    RunInstaller(downloadPath);

                    return new UpdateCheckResult
                    {
                        UpdateAvailable = true,
                        Applying = true,
                        RemoteVersion = remoteText,
                        Message = $"v{remoteText} kuruluyor. Uygulama kapanıp yeniden açılacak."
                    };
                }

                string extractDir = Path.Combine(tempRoot, "extracted");
                Directory.CreateDirectory(extractDir);

                string payloadDir;
                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    status?.Report("Paket açılıyor…");
                    ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
                    payloadDir = FindPayloadDirectory(extractDir);
                }
                else
                {
                    string exeTarget = Path.Combine(extractDir, assetName);
                    File.Copy(downloadPath, exeTarget, overwrite: true);
                    payloadDir = extractDir;
                }

                if (!File.Exists(Path.Combine(payloadDir, "MDM.exe")) &&
                    Directory.GetFiles(payloadDir, "*.exe").Length == 0)
                {
                    return new UpdateCheckResult
                    {
                        HadError = true,
                        Message = "Pakette MDM.exe bulunamadı."
                    };
                }

                status?.Report("Güncelleme uygulanıyor, uygulama yeniden başlatılacak…");
                ScheduleApplyAndRestart(appDir, payloadDir);

                return new UpdateCheckResult
                {
                    UpdateAvailable = true,
                    Applying = true,
                    RemoteVersion = remoteText,
                    Message = $"v{remoteText} kuruluyor. Uygulama kapanıp yeniden açılacak."
                };
            }
            catch (OperationCanceledException)
            {
                return new UpdateCheckResult { HadError = true, Message = "İşlem iptal edildi." };
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult { HadError = true, Message = ex.Message };
            }
        }

        private static void ScheduleApplyAndRestart(string appDir, string payloadDir)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"MDM-Apply-{Guid.NewGuid():N}.cmd");
            int pid = Environment.ProcessId;
            string mainExe = Path.Combine(appDir, "MDM.exe");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto wait");
            sb.AppendLine(")");
            sb.AppendLine("taskkill /IM MDM.exe /F /T >nul 2>&1");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("set RETRIES=0");
            sb.AppendLine(":copy");
            sb.AppendLine($"xcopy /E /Y /I \"{payloadDir}\\*\" \"{appDir}\\\" >nul");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine("  set /a RETRIES+=1");
            sb.AppendLine("  if %RETRIES% LSS 8 (");
            sb.AppendLine("    taskkill /IM MDM.exe /F /T >nul 2>&1");
            sb.AppendLine("    timeout /t 1 /nobreak >nul");
            sb.AppendLine("    goto copy");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine($"if exist \"{mainExe}\" start \"\" \"{mainExe}\" --from-updater");
            sb.AppendLine("endlocal");
            sb.AppendLine("del \"%~f0\"");

            File.WriteAllText(scriptPath, sb.ToString(), Encoding.ASCII);
            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
        }

        private static Version Normalize(Version v) =>
            new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build, v.Revision < 0 ? 0 : v.Revision);

        private static Version? ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string cleaned = tag.Trim().TrimStart('v', 'V');
            Match match = Regex.Match(cleaned, @"^\d+(\.\d+){1,3}");
            if (!match.Success) return null;
            return Version.TryParse(match.Value, out var version) ? Normalize(version) : null;
        }

        /// <summary>Yayın varlığının nasıl uygulanacağı.</summary>
        internal enum PackageKind
        {
            /// <summary>Uygulama dosyaları; kurulum klasörüne kopyalanır.</summary>
            Payload,
            /// <summary>MDM-Setup*.exe; /silent ile kendi kurulumunu yapar.</summary>
            Installer
        }

        internal static bool TryPickAsset(JsonElement release, out string name, out string url, out PackageKind kind)
        {
            name = "";
            url = "";
            kind = PackageKind.Payload;
            if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return false;

            JsonElement? zip = null;
            JsonElement? payloadExe = null;
            JsonElement? installer = null;
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string assetName = asset.GetProperty("name").GetString() ?? "";
                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    zip ??= asset;
                else if (!assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                else if (assetName.Contains("Updater", StringComparison.OrdinalIgnoreCase))
                    continue;
                else if (assetName.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    installer ??= asset;
                else
                    payloadExe ??= asset;
            }

            // Zip en hızlısı ve ayarlara dokunmaz; setup yalnızca paket yoksa kullanılır
            JsonElement? chosen = zip ?? payloadExe ?? installer;
            if (chosen == null) return false;
            if (zip == null && payloadExe == null)
                kind = PackageKind.Installer;

            name = chosen.Value.GetProperty("name").GetString() ?? "";
            url = chosen.Value.GetProperty("browser_download_url").GetString() ?? "";
            return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url);
        }

        /// <summary>
        /// Setup'ı sessiz kipte başlatır. Kurulum çalışan uygulamayı kendisi kapatır ve
        /// bitince yeniden açar; bu yüzden burada beklemek gerekmez.
        /// </summary>
        private static void RunInstaller(string setupPath)
        {
            Process.Start(new ProcessStartInfo(setupPath, "/silent")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? Path.GetTempPath()
            });
        }

        private static async Task DownloadFileAsync(
            string url, string destinationPath, IProgress<string>? status, CancellationToken cancellationToken)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream output = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;
                if (total is > 0)
                    status?.Report($"İndiriliyor… %{readTotal * 100.0 / total.Value:F0}");
                else
                    status?.Report($"İndiriliyor… {readTotal / (1024.0 * 1024.0):F1} MB");
            }
        }

        private static string FindPayloadDirectory(string extractDir)
        {
            if (Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.TopDirectoryOnly).Any())
                return extractDir;

            string[] subDirs = Directory.GetDirectories(extractDir);
            if (subDirs.Length == 1) return subDirs[0];

            foreach (string dir in subDirs)
            {
                if (File.Exists(Path.Combine(dir, "MDM.exe")))
                    return dir;
            }

            return extractDir;
        }
    }
}
