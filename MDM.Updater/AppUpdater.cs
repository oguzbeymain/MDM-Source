using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace MDM.Updater
{
    public sealed class UpdateResult
    {
        public bool ApplyingUpdate { get; init; }
        public bool HadError { get; init; }
        public string? Message { get; init; }
    }

    public static class AppUpdater
    {
        public const string ReleasesLatestUrl = "https://api.github.com/repos/oguzbeymain/MDM-App/releases/latest";
        public const string MainExeName = "MDM.exe";
        public const string UpdaterExeName = "MDM.Updater.exe";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MDM-Updater", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static string GetInstallDirectory()
        {
            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static async Task<UpdateResult> CheckAndApplyAsync(
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            string appDir = GetInstallDirectory();
            string mainExe = Path.Combine(appDir, MainExeName);

            Version current = GetInstalledMainVersion(mainExe);
            status?.Report($"Mevcut surum: v{current}");

            try
            {
                status?.Report("GitHub uzerinden guncelleme kontrol ediliyor...");

                using var response = await Http.GetAsync(ReleasesLatestUrl, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new UpdateResult { Message = "Henuz yayinlanmis surum yok." };
                }

                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                Version? remote = ParseVersion(tagName);
                if (remote == null)
                {
                    return new UpdateResult
                    {
                        HadError = true,
                        Message = $"Surum etiketi okunamadi: {tagName}"
                    };
                }

                if (remote <= current)
                {
                    return new UpdateResult { Message = $"Guncel (v{current}). Uygulama aciliyor..." };
                }

                if (!TryPickAsset(doc.RootElement, out string assetName, out string downloadUrl))
                {
                    return new UpdateResult
                    {
                        HadError = true,
                        Message = "Release paketinde .zip/.exe bulunamadi."
                    };
                }

                status?.Report($"Yeni surum: v{remote} — indiriliyor...");

                string tempRoot = Path.Combine(Path.GetTempPath(), "MDM-Update", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);
                string downloadPath = Path.Combine(tempRoot, assetName);
                await DownloadFileAsync(downloadUrl, downloadPath, status, cancellationToken);

                string extractDir = Path.Combine(tempRoot, "extracted");
                Directory.CreateDirectory(extractDir);

                string payloadDir;
                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    status?.Report("Paket aciliyor...");
                    ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
                    payloadDir = FindPayloadDirectory(extractDir);
                }
                else
                {
                    string exeTarget = Path.Combine(extractDir, assetName);
                    File.Copy(downloadPath, exeTarget, overwrite: true);
                    payloadDir = extractDir;
                }

                if (!File.Exists(Path.Combine(payloadDir, MainExeName)) &&
                    Directory.GetFiles(payloadDir, "*.exe").Length == 0)
                {
                    return new UpdateResult
                    {
                        HadError = true,
                        Message = "Pakette MDM.exe bulunamadi."
                    };
                }

                // Ana uygulama calisiyorsa kapat
                status?.Report("Eski surum kapatiliyor...");
                KillMainAppProcesses(appDir);

                status?.Report("Dosyalar guncelleniyor...");
                ScheduleApplyAndRestart(appDir, payloadDir);

                return new UpdateResult
                {
                    ApplyingUpdate = true,
                    Message = $"v{remote} kuruluyor, yeniden baslatilacak..."
                };
            }
            catch (Exception ex)
            {
                return new UpdateResult
                {
                    HadError = true,
                    Message = $"Guncelleme basarisiz: {ex.Message}"
                };
            }
        }

        private static Version GetInstalledMainVersion(string mainExePath)
        {
            try
            {
                if (File.Exists(mainExePath))
                {
                    var info = FileVersionInfo.GetVersionInfo(mainExePath);
                    string? raw = info.ProductVersion ?? info.FileVersion;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        string cleaned = raw.Split('+')[0].Split(' ')[0].Trim().TrimStart('v', 'V');
                        Match match = Regex.Match(cleaned, @"^\d+(\.\d+){1,3}");
                        if (match.Success && Version.TryParse(match.Value, out var parsed))
                            return Normalize(parsed);
                    }
                }
            }
            catch { }

            return new Version(0, 0, 0, 0);
        }

        private static void KillMainAppProcesses(string appDir)
        {
            // Yol kontrolu olmadan tum MDM (ve eski DownloadMuck) sureclerini kapat
            foreach (string name in new[] { "MDM", "DownloadMuck" })
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(8000);
                    }
                    catch { /* ignore */ }
                }
            }

            try
            {
                foreach (string im in new[] { "MDM.exe", "DownloadMuck.exe" })
                {
                    using var kill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/IM {im} /F /T",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    kill?.WaitForExit(5000);
                }
            }
            catch { /* ignore */ }

            // Dosya kilitlerinin dusmesi icin kisa bekle
            Thread.Sleep(800);
        }

        private static void ScheduleApplyAndRestart(string appDir, string payloadDir)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"MDM-Apply-{Guid.NewGuid():N}.cmd");
            int updaterPid = Environment.ProcessId;
            string mainExe = Path.Combine(appDir, MainExeName);
            string updaterExe = Path.Combine(appDir, UpdaterExeName);

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /FI \"PID eq {updaterPid}\" | find \"{updaterPid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto wait");
            sb.AppendLine(")");
            // Kalan MDM sureclerini zorla kapat
            sb.AppendLine("taskkill /IM MDM.exe /F /T >nul 2>&1");
            sb.AppendLine("taskkill /IM DownloadMuck.exe /F /T >nul 2>&1");
            sb.AppendLine("timeout /t 2 /nobreak >nul");
            sb.AppendLine("set RETRIES=0");
            sb.AppendLine(":copy");
            sb.AppendLine($"xcopy /E /Y /I \"{payloadDir}\\*\" \"{appDir}\\\" >nul");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine("  set /a RETRIES+=1");
            sb.AppendLine("  if %RETRIES% LSS 8 (");
            sb.AppendLine("    taskkill /IM MDM.exe /F /T >nul 2>&1");
            sb.AppendLine("    taskkill /IM DownloadMuck.exe /F /T >nul 2>&1");
            sb.AppendLine("    timeout /t 1 /nobreak >nul");
            sb.AppendLine("    goto copy");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine($"if exist \"{mainExe}\" (");
            sb.AppendLine($"  start \"\" \"{mainExe}\" --from-updater");
            sb.AppendLine(") else if exist \"" + updaterExe + "\" (");
            sb.AppendLine($"  start \"\" \"{updaterExe}\"");
            sb.AppendLine(")");
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

        /// <summary>
        /// Yayında birden fazla mimari paketi olabiliyor; yanlış olanı kopyalamak uygulamayı
        /// açılmaz hale getirir. Mimari etiketi olmayan adlar (eski yayınlar) kabul edilir.
        /// </summary>
        private static bool MatchesProcessArchitecture(string assetName)
        {
            string[] tags = { "win-x64", "win-x86", "win-arm64", "x64", "x86", "arm64" };
            string? found = Array.Find(
                tags, t => assetName.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (found == null) return true;

            string wanted = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => "x64"
            };
            return found.EndsWith(wanted, StringComparison.OrdinalIgnoreCase);
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
                if (!MatchesProcessArchitecture(assetName)) continue;
                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    zip ??= asset;
                else if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                         !assetName.Contains("Updater", StringComparison.OrdinalIgnoreCase))
                    exe ??= asset;
            }

            JsonElement? chosen = zip ?? exe;
            if (chosen == null) return false;
            name = chosen.Value.GetProperty("name").GetString() ?? "";
            url = chosen.Value.GetProperty("browser_download_url").GetString() ?? "";
            return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url);
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
                    status?.Report($"Indiriliyor... %{readTotal * 100.0 / total.Value:F0}");
                else
                    status?.Report($"Indiriliyor... {readTotal / (1024.0 * 1024.0):F1} MB");
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
                if (Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Any())
                    return dir;
            }

            return extractDir;
        }
    }
}
