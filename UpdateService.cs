using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownloadMuck
{
    public sealed class UpdateCheckResult
    {
        public bool UpdateAvailable { get; init; }
        public bool RestartingForUpdate { get; init; }
        public bool Skipped { get; init; }
        public string? Message { get; init; }
        public Version? CurrentVersion { get; init; }
        public Version? RemoteVersion { get; init; }
    }

    public sealed class UpdateService
    {
        public const string GitHubOwner = "oguzbeymain";
        public const string GitHubAppRepo = "MDM-App";
        public const string ReleasesLatestUrl = "https://api.github.com/repos/oguzbeymain/MDM-App/releases/latest";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MDM", GetCurrentVersion().ToString()));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static Version GetCurrentVersion()
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                string cleaned = informational.Split('+')[0].Trim().TrimStart('v', 'V');
                if (Version.TryParse(cleaned, out var fromInfo))
                    return NormalizeVersion(fromInfo);
            }

            return NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0));
        }

        private static Version NormalizeVersion(Version v)
        {
            return new Version(
                v.Major,
                v.Minor,
                v.Build < 0 ? 0 : v.Build,
                v.Revision < 0 ? 0 : v.Revision);
        }

        public async Task<UpdateCheckResult> CheckAndApplyUpdateAsync(
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            Version current = GetCurrentVersion();

            // Debug klasöründen çalışırken otomatik güncellemeyi atla
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (baseDir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || Debugger.IsAttached)
            {
                return new UpdateCheckResult
                {
                    Skipped = true,
                    CurrentVersion = current,
                    Message = "Geliştirme derlemesinde güncelleme atlandı."
                };
            }

            status?.Report("Güncellemeler kontrol ediliyor...");

            try
            {
                using var response = await Http.GetAsync(ReleasesLatestUrl, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new UpdateCheckResult
                    {
                        Skipped = true,
                        CurrentVersion = current,
                        Message = "Henüz yayınlanmış bir sürüm yok."
                    };
                }

                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                Version? remoteVersion = ParseVersion(tagName);
                if (remoteVersion == null)
                {
                    return new UpdateCheckResult
                    {
                        Skipped = true,
                        CurrentVersion = current,
                        Message = $"Sürüm etiketi okunamadı: {tagName}"
                    };
                }

                if (remoteVersion <= current)
                {
                    return new UpdateCheckResult
                    {
                        UpdateAvailable = false,
                        CurrentVersion = current,
                        RemoteVersion = remoteVersion,
                        Message = "Uygulama güncel."
                    };
                }

                if (!TryPickAsset(doc.RootElement, out string assetName, out string downloadUrl))
                {
                    return new UpdateCheckResult
                    {
                        Skipped = true,
                        CurrentVersion = current,
                        RemoteVersion = remoteVersion,
                        Message = "Release içinde indirilebilir paket (.zip/.exe) bulunamadı."
                    };
                }

                status?.Report($"Yeni sürüm bulundu: v{remoteVersion} — indiriliyor...");

                string tempRoot = Path.Combine(Path.GetTempPath(), "MDM-Update", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);
                string downloadPath = Path.Combine(tempRoot, assetName);

                await DownloadFileAsync(downloadUrl, downloadPath, status, cancellationToken);

                string extractDir = Path.Combine(tempRoot, "extracted");
                Directory.CreateDirectory(extractDir);

                string payloadDir;
                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    status?.Report("Paket açılıyor...");
                    ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
                    payloadDir = FindPayloadDirectory(extractDir);
                }
                else
                {
                    // Tek exe release: doğrudan kopyalanacak
                    string exeTarget = Path.Combine(extractDir, assetName);
                    File.Copy(downloadPath, exeTarget, overwrite: true);
                    payloadDir = extractDir;
                }

                string? exeName = FindMainExecutable(payloadDir);
                if (exeName == null)
                {
                    return new UpdateCheckResult
                    {
                        Skipped = true,
                        CurrentVersion = current,
                        RemoteVersion = remoteVersion,
                        Message = "Güncelleme paketinde çalıştırılabilir dosya bulunamadı."
                    };
                }

                status?.Report("Güncelleme uygulanıyor, uygulama yeniden başlatılacak...");
                LaunchUpdaterAndExit(baseDir, payloadDir, exeName);

                return new UpdateCheckResult
                {
                    UpdateAvailable = true,
                    RestartingForUpdate = true,
                    CurrentVersion = current,
                    RemoteVersion = remoteVersion,
                    Message = $"v{remoteVersion} kuruluyor..."
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult
                {
                    Skipped = true,
                    CurrentVersion = current,
                    Message = $"Güncelleme kontrolü başarısız: {ex.Message}"
                };
            }
        }

        private static Version? ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string cleaned = tag.Trim();
            if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[1..];

            // 1.0.0 veya 1.0.0-beta gibi etiketlerden sayısal kısmı al
            Match match = Regex.Match(cleaned, @"^\d+(\.\d+){1,3}");
            if (!match.Success) return null;
            return Version.TryParse(match.Value, out var version) ? NormalizeVersion(version) : null;
        }

        private static bool TryPickAsset(JsonElement release, out string name, out string url)
        {
            name = "";
            url = "";

            if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return false;

            JsonElement? zip = null;
            JsonElement? exe = null;

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string assetName = asset.GetProperty("name").GetString() ?? "";
                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    zip ??= asset;
                else if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    exe ??= asset;
            }

            JsonElement? chosen = zip ?? exe;
            if (chosen == null) return false;

            name = chosen.Value.GetProperty("name").GetString() ?? "";
            url = chosen.Value.GetProperty("browser_download_url").GetString() ?? "";
            return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url);
        }

        private static async Task DownloadFileAsync(
            string url,
            string destinationPath,
            IProgress<string>? status,
            CancellationToken cancellationToken)
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;

                if (total.HasValue && total.Value > 0)
                {
                    double pct = readTotal * 100.0 / total.Value;
                    status?.Report($"İndiriliyor... %{pct:F0}");
                }
                else
                {
                    status?.Report($"İndiriliyor... {readTotal / (1024.0 * 1024.0):F1} MB");
                }
            }
        }

        private static string FindPayloadDirectory(string extractDir)
        {
            // Zip kökünde exe varsa o klasör; yoksa tek alt klasörü kullan
            if (Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.TopDirectoryOnly).Any())
                return extractDir;

            string[] subDirs = Directory.GetDirectories(extractDir);
            if (subDirs.Length == 1)
                return subDirs[0];

            foreach (string dir in subDirs)
            {
                if (Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Any())
                    return dir;
            }

            return extractDir;
        }

        private static string? FindMainExecutable(string payloadDir)
        {
            string[] preferred = { "DownloadMuck.exe", "MDM.exe" };
            foreach (string name in preferred)
            {
                string path = Path.Combine(payloadDir, name);
                if (File.Exists(path))
                    return name;
            }

            string[] exes = Directory.GetFiles(payloadDir, "*.exe", SearchOption.TopDirectoryOnly);
            return exes.Length > 0 ? Path.GetFileName(exes[0]) : null;
        }

        private static void LaunchUpdaterAndExit(string appDir, string payloadDir, string exeName)
        {
            string updaterPath = Path.Combine(Path.GetTempPath(), $"MDM-ApplyUpdate-{Guid.NewGuid():N}.cmd");
            int pid = Environment.ProcessId;

            var script = new StringBuilder();
            script.AppendLine("@echo off");
            script.AppendLine("setlocal");
            script.AppendLine($":waitloop");
            script.AppendLine($"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul");
            script.AppendLine("if not errorlevel 1 (");
            script.AppendLine("  timeout /t 1 /nobreak >nul");
            script.AppendLine("  goto waitloop");
            script.AppendLine(")");
            script.AppendLine($"xcopy /E /Y /I \"{payloadDir}\\*\" \"{appDir}\\\" >nul");
            script.AppendLine($"start \"\" \"{Path.Combine(appDir, exeName)}\"");
            script.AppendLine("endlocal");
            script.AppendLine($"del \"%~f0\"");

            File.WriteAllText(updaterPath, script.ToString(), Encoding.ASCII);

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
        }
    }
}
