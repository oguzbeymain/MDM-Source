using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MDM
{
    public static class YtDlpHelper
    {
        private static string? _cachedPath;
        private static string? _cachedFfmpegDir;
        private static string? _cachedDenoPath;
        private static readonly object EnsureGate = new();
        private static readonly object FfmpegGate = new();
        private static readonly object DenoGate = new();
        private static Task<bool>? _ensureTask;
        private static Task<bool>? _ffmpegTask;
        private static Task<bool>? _denoTask;

        public static bool IsAvailable() => !string.IsNullOrWhiteSpace(FindExecutable());

        public static bool IsFfmpegAvailable() => !string.IsNullOrWhiteSpace(FindFfmpegDir());

        /// <summary>YouTube n-sig / JS challenge için Deno (yt-dlp EJS).</summary>
        public static bool IsDenoAvailable() => !string.IsNullOrWhiteSpace(FindDenoExecutable());

        /// <summary>Son yt-dlp stderr (UI için).</summary>
        public static string? LastError { get; set; }

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        public static bool IsYouTubeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                var u = new Uri(url);
                string h = u.Host.ToLowerInvariant();
                return h.Contains("youtube.com") || h.Contains("youtu.be")
                    || h.Contains("youtube-nocookie.com") || h.Contains("googlevideo.com");
            }
            catch { return false; }
        }

        public static string? NormalizeYouTubeWatchUrl(string pageUrl)
        {
            if (string.IsNullOrWhiteSpace(pageUrl)) return pageUrl;
            try
            {
                var u = new Uri(pageUrl);
                if (u.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                {
                    string id = u.AbsolutePath.Trim('/');
                    if (!string.IsNullOrWhiteSpace(id))
                        return "https://www.youtube.com/watch?v=" + id;
                }
                // query v=
                string query = u.Query.TrimStart('?');
                foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOf('=');
                    string k = eq < 0 ? part : part[..eq];
                    if (!k.Equals("v", StringComparison.OrdinalIgnoreCase)) continue;
                    string id = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
                    if (!string.IsNullOrWhiteSpace(id))
                        return "https://www.youtube.com/watch?v=" + id;
                }
                // /shorts/ID /embed/ID
                var m = Regex.Match(u.AbsolutePath, @"/(?:shorts|embed|live)/([A-Za-z0-9_-]{6,})");
                if (m.Success)
                    return "https://www.youtube.com/watch?v=" + m.Groups[1].Value;
            }
            catch { /* ignore */ }
            return pageUrl;
        }

        public static string? FindExecutable()
        {
            if (!string.IsNullOrWhiteSpace(_cachedPath) && File.Exists(_cachedPath))
                return _cachedPath;

            string appDir = AppContext.BaseDirectory;
            foreach (string name in new[] { "yt-dlp.exe", "yt-dlp" })
            {
                string local = Path.Combine(appDir, name);
                if (File.Exists(local))
                {
                    _cachedPath = local;
                    return local;
                }
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), "yt-dlp.exe");
                        if (File.Exists(candidate))
                        {
                            _cachedPath = candidate;
                            return candidate;
                        }
                    }
                    catch { /* ignore */ }
                }
            }

            return null;
        }

        public static string? FindDenoExecutable()
        {
            if (!string.IsNullOrWhiteSpace(_cachedDenoPath) && File.Exists(_cachedDenoPath))
                return _cachedDenoPath;

            string appDir = AppContext.BaseDirectory;
            string local = Path.Combine(appDir, "deno.exe");
            if (File.Exists(local) && IsRunnableDeno(local))
            {
                _cachedDenoPath = local;
                return local;
            }

            // yt-dlp: PATH veya yt-dlp.exe yanındaki deno
            string? ytdlp = FindExecutable();
            if (!string.IsNullOrWhiteSpace(ytdlp))
            {
                string beside = Path.Combine(Path.GetDirectoryName(ytdlp)!, "deno.exe");
                if (File.Exists(beside) && IsRunnableDeno(beside))
                {
                    _cachedDenoPath = beside;
                    return beside;
                }
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), "deno.exe");
                        if (File.Exists(candidate) && IsRunnableDeno(candidate))
                        {
                            _cachedDenoPath = candidate;
                            return candidate;
                        }
                    }
                    catch { /* ignore */ }
                }
            }

            return null;
        }

        private static bool IsRunnableDeno(string exePath)
        {
            try
            {
                var fi = new FileInfo(exePath);
                if (!fi.Exists || fi.Length < 5_000_000) return false;
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                if (!proc.Start()) return false;
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(8000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return false;
                }
                try { stdout.GetAwaiter().GetResult(); } catch { /* ignore */ }
                try { stderr.GetAwaiter().GetResult(); } catch { /* ignore */ }
                return proc.ExitCode == 0;
            }
            catch { return false; }
        }

        public static string? FindFfmpegDir()
        {
            // IsRunnableFfmpeg her çağrıda pahalı — bir kez doğrula, cache'le
            if (!string.IsNullOrWhiteSpace(_cachedFfmpegDir)
                && File.Exists(Path.Combine(_cachedFfmpegDir, "ffmpeg.exe")))
                return _cachedFfmpegDir;

            string appDir = AppContext.BaseDirectory;
            string local = Path.Combine(appDir, "ffmpeg.exe");
            if (File.Exists(local) && IsRunnableFfmpeg(local))
            {
                _cachedFfmpegDir = TrimDir(appDir);
                return _cachedFfmpegDir;
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                        if (File.Exists(candidate) && IsRunnableFfmpeg(candidate))
                        {
                            _cachedFfmpegDir = TrimDir(dir.Trim());
                            return _cachedFfmpegDir;
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            return null;
        }

        /// <summary>Shared build DLL eksikse exit 0xC0000135 verir — çalışmıyorsa yok say.</summary>
        private static bool IsRunnableFfmpeg(string exePath)
        {
            try
            {
                var fi = new FileInfo(exePath);
                if (!fi.Exists || fi.Length < 1_000_000) return false;
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                if (!proc.Start()) return false;
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(8000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return false;
                }
                try { stdout.GetAwaiter().GetResult(); } catch { /* ignore */ }
                try { stderr.GetAwaiter().GetResult(); } catch { /* ignore */ }
                return proc.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>yt-dlp yoksa GitHub'dan uygulamam klasörüne indirir (IDM masaüstü rolü).</summary>
        public static Task<bool> EnsureAvailableAsync(TimeSpan? timeout = null)
        {
            if (IsAvailable()) return Task.FromResult(true);
            lock (EnsureGate)
            {
                _ensureTask ??= DownloadYtDlpAsync(timeout ?? TimeSpan.FromSeconds(60));
                return _ensureTask;
            }
        }

        /// <summary>Ses+görüntü birleştirme için ffmpeg; yoksa zip indirir (arka plan / kısa bekleme).</summary>
        public static Task<bool> EnsureFfmpegAsync(TimeSpan? timeout = null)
        {
            if (IsFfmpegAvailable()) return Task.FromResult(true);
            lock (FfmpegGate)
            {
                // Takılı / başarısız görev yeniden denensin
                if (_ffmpegTask != null && _ffmpegTask.IsCompleted && !_ffmpegTask.Result)
                    _ffmpegTask = null;
                _ffmpegTask ??= DownloadFfmpegAsync(timeout ?? TimeSpan.FromMinutes(3));
                return _ffmpegTask;
            }
        }

        /// <summary>ffmpeg varsa true; yoksa indirmeyi arka planda başlatır, beklemez.</summary>
        public static bool TryBeginFfmpegInstall()
        {
            if (IsFfmpegAvailable()) return true;
            _ = EnsureFfmpegAsync(TimeSpan.FromMinutes(4));
            return false;
        }

        /// <summary>YouTube JS challenge için Deno; yoksa zip indirir.</summary>
        public static Task<bool> EnsureDenoAsync(TimeSpan? timeout = null)
        {
            if (IsDenoAvailable()) return Task.FromResult(true);
            lock (DenoGate)
            {
                if (_denoTask != null && _denoTask.IsCompleted && !_denoTask.Result)
                    _denoTask = null;
                _denoTask ??= DownloadDenoAsync(timeout ?? TimeSpan.FromMinutes(3));
                return _denoTask;
            }
        }

        /// <summary>Deno varsa true; yoksa arka planda indir, beklemez.</summary>
        public static bool TryBeginDenoInstall()
        {
            if (IsDenoAvailable()) return true;
            _ = EnsureDenoAsync(TimeSpan.FromMinutes(4));
            return false;
        }

        private static async Task<bool> DownloadDenoAsync(TimeSpan timeout)
        {
            string? zipPath = null;
            string? extractDir = null;
            try
            {
                string appDir = AppContext.BaseDirectory;
                string destExe = Path.Combine(appDir, "deno.exe");
                if (File.Exists(destExe) && IsRunnableDeno(destExe))
                {
                    _cachedDenoPath = destExe;
                    return true;
                }

                using var cts = new CancellationTokenSource(timeout);
                using var client = new HttpClient { Timeout = timeout };
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "MDM/" + (typeof(YtDlpHelper).Assembly.GetName().Version?.ToString() ?? "1"));

                const string url =
                    "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
                zipPath = Path.Combine(Path.GetTempPath(), $"mdm-deno-{Guid.NewGuid():N}.zip");
                extractDir = Path.Combine(Path.GetTempPath(), $"mdm-deno-ex-{Guid.NewGuid():N}");

                await using (var fs = File.Create(zipPath))
                {
                    using var resp = await client
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    await resp.Content.CopyToAsync(fs, cts.Token).ConfigureAwait(false);
                }

                var zi = new FileInfo(zipPath);
                if (zi.Length < 10_000_000)
                    throw new InvalidOperationException($"deno zip too small ({zi.Length})");

                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

                string? found = Directory.GetFiles(extractDir, "deno.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found == null)
                    throw new InvalidOperationException("deno.exe not in zip");

                File.Copy(found, destExe, overwrite: true);
                if (!IsRunnableDeno(destExe))
                    throw new InvalidOperationException("deno not runnable after install");

                _cachedDenoPath = destExe;
                Debug.WriteLine($"deno installed: {destExe}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"deno download failed: {ex.Message}");
                lock (DenoGate) { _denoTask = null; }
                return false;
            }
            finally
            {
                try
                {
                    if (zipPath != null && File.Exists(zipPath)) File.Delete(zipPath);
                    if (extractDir != null && Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                }
                catch { /* ignore */ }
            }
        }

        private static readonly string[] FfmpegZipUrls =
        {
            // Statik build (shared DLL’lere ihtiyaç yok)
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
        };

        private static async Task<bool> DownloadFfmpegAsync(TimeSpan timeout)
        {
            string? zipPath = null;
            string? extractDir = null;
            try
            {
                string appDir = AppContext.BaseDirectory;
                string destExe = Path.Combine(appDir, "ffmpeg.exe");
                if (File.Exists(destExe))
                {
                    _cachedFfmpegDir = appDir;
                    return true;
                }

                using var cts = new CancellationTokenSource(timeout);
                using var client = new HttpClient { Timeout = timeout };
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "MDM/" + (typeof(YtDlpHelper).Assembly.GetName().Version?.ToString() ?? "1"));

                Exception? last = null;
                foreach (string url in FfmpegZipUrls)
                {
                    zipPath = Path.Combine(Path.GetTempPath(), $"mdm-ffmpeg-{Guid.NewGuid():N}.zip");
                    extractDir = Path.Combine(Path.GetTempPath(), $"mdm-ffmpeg-ex-{Guid.NewGuid():N}");
                    try
                    {
                        await using (var fs = File.Create(zipPath))
                        {
                            using var resp = await client
                                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                .ConfigureAwait(false);
                            resp.EnsureSuccessStatusCode();
                            await resp.Content.CopyToAsync(fs, cts.Token).ConfigureAwait(false);
                        }

                        var zi = new FileInfo(zipPath);
                        if (zi.Length < 5_000_000)
                            throw new InvalidOperationException($"ffmpeg zip too small ({zi.Length})");

                        Directory.CreateDirectory(extractDir);
                        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

                        string? found = Directory.GetFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                            .FirstOrDefault();
                        string? probe = Directory.GetFiles(extractDir, "ffprobe.exe", SearchOption.AllDirectories)
                            .FirstOrDefault();
                        if (found == null)
                            throw new InvalidOperationException("ffmpeg.exe not in zip");

                        File.Copy(found, destExe, overwrite: true);
                        if (probe != null)
                            File.Copy(probe, Path.Combine(appDir, "ffprobe.exe"), overwrite: true);

                        // Shared build ise yanındaki DLL'leri de kopyala
                        string foundDir = Path.GetDirectoryName(found) ?? "";
                        foreach (string dll in Directory.GetFiles(foundDir, "*.dll"))
                        {
                            try
                            {
                                File.Copy(dll, Path.Combine(appDir, Path.GetFileName(dll)), overwrite: true);
                            }
                            catch { /* ignore */ }
                        }

                        if (!IsRunnableFfmpeg(destExe))
                            throw new InvalidOperationException("ffmpeg not runnable after install");

                        _cachedFfmpegDir = appDir;
                        Debug.WriteLine($"ffmpeg installed: {destExe} from {url}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Debug.WriteLine($"ffmpeg url fail ({url}): {ex.Message}");
                        try { if (zipPath != null && File.Exists(zipPath)) File.Delete(zipPath); } catch { /* ignore */ }
                        try { if (extractDir != null && Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { /* ignore */ }
                        zipPath = null;
                        extractDir = null;
                    }
                }

                throw last ?? new InvalidOperationException("ffmpeg download failed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ffmpeg download failed: {ex.Message}");
                lock (FfmpegGate) { _ffmpegTask = null; }
                return false;
            }
            finally
            {
                try
                {
                    if (zipPath != null && File.Exists(zipPath)) File.Delete(zipPath);
                    if (extractDir != null && Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                    foreach (string p in Directory.GetFiles(Path.GetTempPath(), "mdm-ffmpeg-*.zip"))
                    {
                        try
                        {
                            var fi = new FileInfo(p);
                            if (fi.Length < 5_000_000 || fi.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-2))
                                File.Delete(p);
                        }
                        catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }
            }
        }

        private static async Task<bool> DownloadYtDlpAsync(TimeSpan timeout)
        {
            try
            {
                string dest = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
                if (File.Exists(dest))
                {
                    _cachedPath = dest;
                    return true;
                }

                using var client = new HttpClient { Timeout = timeout };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "MDM/" + (typeof(YtDlpHelper).Assembly.GetName().Version?.ToString() ?? "1"));
                string url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
                byte[] bytes = await client.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes.Length < 100_000) return false;
                string tmp = dest + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                if (File.Exists(dest))
                {
                    try { File.Delete(dest); } catch { /* ignore */ }
                }
                File.Move(tmp, dest);
                _cachedPath = dest;
                Debug.WriteLine($"yt-dlp downloaded: {dest} ({bytes.Length} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"yt-dlp download failed: {ex.Message}");
                lock (EnsureGate) { _ensureTask = null; }
                return false;
            }
        }

        public static JsonDocument? ExtractInfoJson(
            string pageUrl,
            string cookies,
            IReadOnlyDictionary<string, string>? headers,
            TimeSpan timeout)
        {
            string? exe = FindExecutable();
            if (exe == null) return null;

            if (IsYouTubeUrl(pageUrl))
                pageUrl = NormalizeYouTubeWatchUrl(pageUrl) ?? pageUrl;

            // Önce cookie ile dene; başarısızsa cookiesiz (bozuk/BOM cookie yt-dlp'yi öldürürdü)
            var doc = ExtractInfoJsonCore(exe, pageUrl, cookies, headers, timeout);
            if (doc != null) return doc;
            if (!string.IsNullOrWhiteSpace(cookies))
                return ExtractInfoJsonCore(exe, pageUrl, "", headers, timeout);
            return null;
        }

        /// <summary>Başlatmadan önce yaklaşık toplam boyut (video+ses; NA ise tbr×süre).</summary>
        /// <param name="mediaOrWatchUrl">yt-dlp hedef URL (HLS medya veya YouTube watch).</param>
        /// <param name="sitePageUrl">Cookie/Referer için film sayfası (CDN host değil).</param>
        public static long? ProbeApproxBytes(
            string mediaOrWatchUrl,
            string formatId,
            string cookies,
            IReadOnlyDictionary<string, string>? headers,
            TimeSpan timeout,
            string? sitePageUrl = null)
        {
            string? exe = FindExecutable();
            if (exe == null) return null;
            string pageUrl = mediaOrWatchUrl;
            if (IsYouTubeUrl(pageUrl))
                pageUrl = NormalizeYouTubeWatchUrl(pageUrl) ?? pageUrl;

            string cookieUrl = !string.IsNullOrWhiteSpace(sitePageUrl) ? sitePageUrl! : pageUrl;
            formatId = NormalizeFormatForProbe(formatId, pageUrl);

            long? Probe(string cookieStr)
            {
                string? cookieFile = WriteTempCookies(cookieStr, cookieUrl);
                try
                {
                    var args = new StringBuilder();
                    args.Append("--no-warnings --skip-download --no-playlist --no-check-certificates ");
                    args.Append($"-f \"{formatId}\" ");
                    // duration | v_size|v_approx|v_tbr | a_size|a_approx|a_tbr
                    args.Append("--print \"%(duration)s\" ");
                    args.Append("--print \"%(requested_formats.0.filesize)s|%(requested_formats.0.filesize_approx)s|%(requested_formats.0.tbr)s\" ");
                    args.Append("--print \"%(requested_formats.1.filesize)s|%(requested_formats.1.filesize_approx)s|%(requested_formats.1.tbr)s\" ");
                    // Tek akış HLS: kök alanlar
                    args.Append("--print \"%(filesize)s|%(filesize_approx)s|%(tbr)s\" ");
                    if (!string.IsNullOrWhiteSpace(cookieFile))
                        args.Append($"--cookies \"{cookieFile}\" ");
                    ApplyHeaderArgs(args, headers, cookieUrl);
                    args.Append($"\"{pageUrl}\"");

                    string? output = RunProcess(exe, args.ToString(), timeout, out _);
                    if (string.IsNullOrWhiteSpace(output)) return null;

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .ToList();
                    if (lines.Count == 0) return null;

                    double duration = 0;
                    if (lines.Count > 0)
                        double.TryParse(lines[0], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

                    long SumPart(string? line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) return 0;
                        string[] p = line.Split('|');
                        long ParseLong(string? s)
                        {
                            if (string.IsNullOrWhiteSpace(s) || s is "NA" or "None" or "null") return 0;
                            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
                        }
                        double ParseDbl(string? s)
                        {
                            if (string.IsNullOrWhiteSpace(s) || s is "NA" or "None" or "null") return 0;
                            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
                        }

                        long fs = p.Length > 0 ? ParseLong(p[0]) : 0;
                        if (fs <= 0 && p.Length > 1) fs = ParseLong(p[1]);
                        if (fs > 0) return fs;
                        double tbr = p.Length > 2 ? ParseDbl(p[2]) : 0;
                        if (tbr > 0 && duration > 0)
                            return (long)(tbr * 1000.0 / 8.0 * duration);
                        return 0;
                    }

                    long video = lines.Count > 1 ? SumPart(lines[1]) : 0;
                    long audio = lines.Count > 2 ? SumPart(lines[2]) : 0;
                    long root = lines.Count > 3 ? SumPart(lines[3]) : 0;
                    long total = video + audio;
                    if (total > 0) return total;
                    if (root > 0) return root;

                    // Tek satır fallback (eski print)
                    foreach (string line in lines)
                    {
                        if (long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out long b) && b > 1_000_000)
                            return b;
                    }
                    return null;
                }
                finally
                {
                    if (cookieFile != null)
                    {
                        try { File.Delete(cookieFile); } catch { /* ignore */ }
                    }
                }
            }

            try
            {
                var a = Probe(cookies);
                if (a is > 1_000_000) return a;
                if (!string.IsNullOrWhiteSpace(cookies))
                {
                    var b = Probe("");
                    if (b is > 1_000_000) return b;
                }
                if (IsYouTubeUrl(pageUrl))
                {
                    string forced = "bv*[protocol^=http][vcodec^=avc1]+ba[protocol^=http]/b";
                    if (!string.Equals(formatId, forced, StringComparison.Ordinal))
                    {
                        formatId = forced;
                        var c = Probe("");
                        if (c is > 1_000_000) return c;
                    }
                }
                else if (a is null or <= 0)
                {
                    // HLS tek akış: kök filesize / tbr
                    formatId = "best";
                    var d = Probe("");
                    if (d is > 0) return d;
                }
                return a is > 0 ? a : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProbeApproxBytes: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// YouTube için https/avc1 tercih; diğer sitelerde (HLS/film) "best" —
        /// eklenti "720p" / "h-720-…" etiketlerini yt-dlp format id sanmasın.
        /// </summary>
        public static string NormalizeFormatForProbe(string? formatId, string? mediaOrPageUrl = null)
        {
            bool youtube = IsYouTubeUrl(mediaOrPageUrl);
            if (!youtube)
            {
                if (string.IsNullOrWhiteSpace(formatId) || formatId is "best" or "playing" or "progressive")
                    return "best";
                // Eski YouTube seçicisi yanlışlıkla gelmişse
                if (formatId.Contains("bv*", StringComparison.OrdinalIgnoreCase)
                    && formatId.Contains("protocol", StringComparison.OrdinalIgnoreCase))
                    return "best";
                if (formatId.Contains("vcodec^=avc1", StringComparison.OrdinalIgnoreCase))
                    return "best";
                if (formatId.Contains('+') || formatId.Contains("bv*", StringComparison.OrdinalIgnoreCase))
                    return "best";

                // Eklenti UI: "720p", "1080p60", "prog-720", "h-720-avc1-…", "hls-media-720"
                if (TryParseHeightHint(formatId, out int h))
                    return h > 0 ? $"best[height<={h}]/best" : "best";

                // Gerçek yt-dlp id (sayısal / dash itag) — koru; aksi halde best
                if (Regex.IsMatch(formatId, @"^\d{1,4}(?:\+\d{1,4})?$"))
                    return formatId;
                if (formatId.StartsWith("dash-", StringComparison.OrdinalIgnoreCase)
                    && formatId.Length > 5
                    && !formatId.Contains("dash-media", StringComparison.OrdinalIgnoreCase))
                {
                    // dash-{realId} → realId
                    string dashId = formatId["dash-".Length..];
                    if (!string.IsNullOrWhiteSpace(dashId) && !dashId.Contains(' '))
                        return dashId;
                }

                return "best";
            }

            if (string.IsNullOrWhiteSpace(formatId) || formatId is "best" or "playing")
                return "bv*[protocol^=http][vcodec^=avc1]+ba[protocol^=http]/bv*[protocol^=http]+ba/b";
            if (formatId.Contains("bv*", StringComparison.OrdinalIgnoreCase)
                && !formatId.Contains("protocol", StringComparison.OrdinalIgnoreCase))
                return "bv*[protocol^=http][vcodec^=avc1]+ba[protocol^=http]/bv*[protocol^=http]+ba/b";
            // YouTube'da da yanlışlıkla salt "720p" gelirse
            if (TryParseHeightHint(formatId, out int yh)
                && !formatId.Contains('+')
                && !Regex.IsMatch(formatId, @"^\d+$"))
                return yh > 0
                    ? $"bv*[height<={yh}][protocol^=http]+ba[protocol^=http]/b[height<={yh}]/b"
                    : "bv*[protocol^=http][vcodec^=avc1]+ba[protocol^=http]/bv*[protocol^=http]+ba/b";
            return formatId;
        }

        /// <summary>"720p", "prog-720", "h-1080-avc1-1", "hls-media-480" → yükseklik.</summary>
        public static bool TryParseHeightHint(string? formatId, out int height)
        {
            height = 0;
            if (string.IsNullOrWhiteSpace(formatId)) return false;
            var m = Regex.Match(formatId.Trim(),
                @"^(?:prog-|hls-media-|h-|hls-)?(\d{3,4})p?(?:\d{2})?(?:[-_/].*)?$",
                RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int h) && h is >= 144 and <= 4320)
            {
                height = h;
                return true;
            }
            m = Regex.Match(formatId, @"(\d{3,4})p\b", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out h) && h is >= 144 and <= 4320)
            {
                height = h;
                return true;
            }
            return false;
        }

        private static JsonDocument? ExtractInfoJsonCore(
            string exe,
            string pageUrl,
            string cookies,
            IReadOnlyDictionary<string, string>? headers,
            TimeSpan timeout)
        {
            string? cookieFile = WriteTempCookies(cookies, pageUrl);
            try
            {
                var args = new StringBuilder();
                args.Append("-J --no-warnings --no-download --no-playlist --no-check-certificates ");
                if (!string.IsNullOrWhiteSpace(cookieFile))
                    args.Append($"--cookies \"{cookieFile}\" ");
                ApplyHeaderArgs(args, headers, pageUrl);
                args.Append($"\"{pageUrl}\"");

                string? json = RunProcess(exe, args.ToString(), timeout, out string? err);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.WriteLine($"yt-dlp -J failed: {err}");
                    return null;
                }
                return JsonDocument.Parse(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"yt-dlp ExtractInfoJson: {ex.Message}");
                return null;
            }
            finally
            {
                if (cookieFile != null)
                {
                    try { File.Delete(cookieFile); } catch { /* ignore */ }
                }
            }
        }

        public static string? ResolveDownloadUrl(
            string pageUrl,
            string formatId,
            string cookies,
            IReadOnlyDictionary<string, string>? headers,
            TimeSpan timeout)
        {
            string? exe = FindExecutable();
            if (exe == null) return null;

            if (IsYouTubeUrl(pageUrl))
                pageUrl = NormalizeYouTubeWatchUrl(pageUrl) ?? pageUrl;

            string? cookieFile = WriteTempCookies(cookies, pageUrl);
            try
            {
                var args = new StringBuilder();
                args.Append("--no-warnings --no-playlist --print url --no-check-certificates ");
                if (!string.IsNullOrWhiteSpace(formatId))
                    args.Append($"-f \"{formatId}\" ");
                if (!string.IsNullOrWhiteSpace(cookieFile))
                    args.Append($"--cookies \"{cookieFile}\" ");
                ApplyHeaderArgs(args, headers, pageUrl);
                args.Append($"\"{pageUrl}\"");

                return RunProcess(exe, args.ToString(), timeout, out _)?.Trim();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (cookieFile != null)
                {
                    try { File.Delete(cookieFile); } catch { /* ignore */ }
                }
            }
        }

        public static string? DownloadToFile(
            string pageOrMediaUrl,
            string formatId,
            string outputPathWithoutExt,
            string cookies,
            IReadOnlyDictionary<string, string>? headers,
            TimeSpan timeout)
        {
            string? exe = FindExecutable();
            if (exe == null) return null;

            if (IsYouTubeUrl(pageOrMediaUrl))
                pageOrMediaUrl = NormalizeYouTubeWatchUrl(pageOrMediaUrl) ?? pageOrMediaUrl;

            // ffmpeg'i uzun süre bekleme — yoksa arka planda kur, muxed'e düş
            bool needsMerge = string.IsNullOrWhiteSpace(formatId)
                              || formatId.Contains('+')
                              || formatId.Contains("bv", StringComparison.OrdinalIgnoreCase);
            if (needsMerge && !IsFfmpegAvailable())
            {
                TryBeginFfmpegInstall();
                // en fazla ~2 sn bekle; gelmezse tek dosya
                for (int i = 0; i < 8 && !IsFfmpegAvailable(); i++)
                    Thread.Sleep(250);
                if (!IsFfmpegAvailable())
                {
                    var hm = Regex.Match(outputPathWithoutExt, @"(\d{3,4})p", RegexOptions.IgnoreCase);
                    formatId = hm.Success
                        ? $"b[height<={hm.Groups[1].Value}]/bv*[height<={hm.Groups[1].Value}]+ba/b"
                        : "b/bv*+ba/b";
                    needsMerge = formatId.Contains('+') || formatId.Contains("bv", StringComparison.OrdinalIgnoreCase);
                }
            }

            string? saved = DownloadToFileCore(exe, pageOrMediaUrl, formatId, outputPathWithoutExt, cookies, headers, timeout);
            if (saved != null && !IsPartialFormatFile(saved)) return saved;

            if (!string.IsNullOrWhiteSpace(cookies))
            {
                saved = DownloadToFileCore(exe, pageOrMediaUrl, formatId, outputPathWithoutExt, "", headers, timeout);
                if (saved != null && !IsPartialFormatFile(saved)) return saved;
            }

            // Hâlâ birleşmediyse (ffmpeg yok): en iyi tek muxed akış
            if (needsMerge)
            {
                saved = DownloadToFileCore(exe, pageOrMediaUrl, "b", outputPathWithoutExt, "", headers, timeout);
                if (saved != null && !IsPartialFormatFile(saved)) return saved;
            }

            return null;
        }

        private static bool IsPartialFormatFile(string path)
        {
            // yt-dlp ffmpeg yokken bıraktığı ara dosyalar: name.f242.webm
            string name = Path.GetFileName(path);
            return Regex.IsMatch(name, @"\.f\d+\.", RegexOptions.IgnoreCase);
        }

        private static string? DownloadToFileCore(
            string exe,
            string pageOrMediaUrl,
            string formatId,
            string outputPathWithoutExt,
            string cookies,
            IReadOnlyDictionary<string, string>? headers,
            TimeSpan timeout)
        {
            string? cookieFile = WriteTempCookies(cookies, pageOrMediaUrl);
            try
            {
                // Eski ara dosyaları temizle
                try
                {
                    string dir0 = Path.GetDirectoryName(outputPathWithoutExt) ?? "";
                    string base0 = Path.GetFileName(outputPathWithoutExt);
                    if (Directory.Exists(dir0))
                    {
                        foreach (string f in Directory.GetFiles(dir0, base0 + ".*"))
                        {
                            if (IsPartialFormatFile(f) || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                || f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Delete(f); } catch { /* ignore */ }
                            }
                        }
                    }
                }
                catch { /* ignore */ }

                var args = new StringBuilder();
                args.Append("--no-warnings --no-playlist --newline --no-check-certificates ");
                if (!string.IsNullOrWhiteSpace(formatId))
                    args.Append($"-f \"{formatId}\" ");
                else
                    args.Append("-f \"bv*+ba/b\" ");
                if (!string.IsNullOrWhiteSpace(cookieFile))
                    args.Append($"--cookies \"{cookieFile}\" ");
                ApplyHeaderArgs(args, headers, pageOrMediaUrl);
                args.Append("--merge-output-format mp4 ");
                args.Append($"-o \"{outputPathWithoutExt}.%(ext)s\" ");
                args.Append($"\"{pageOrMediaUrl}\"");

                string? output = RunProcess(exe, args.ToString(), timeout, out string? err);
                if (output == null)
                    Debug.WriteLine($"yt-dlp download failed: {err}");

                string dir = Path.GetDirectoryName(outputPathWithoutExt) ?? "";
                string baseName = Path.GetFileName(outputPathWithoutExt);
                if (Directory.Exists(dir))
                {
                    var match = Directory.GetFiles(dir, baseName + ".*")
                        .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                    && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                                    && !IsPartialFormatFile(f))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (match != null) return match;
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"yt-dlp DownloadToFile: {ex.Message}");
                LastError = ex.Message;
                return null;
            }
            finally
            {
                if (cookieFile != null)
                {
                    try { File.Delete(cookieFile); } catch { /* ignore */ }
                }
            }
        }

        private static void ApplyHeaderArgs(StringBuilder args, IReadOnlyDictionary<string, string>? headers, string pageUrl)
        {
            string referer = pageUrl;
            string ua = TransferHttp.UserAgent;
            if (headers != null)
            {
                if (headers.TryGetValue("Referer", out string? r) && !string.IsNullOrWhiteSpace(r))
                    referer = r;
                if (headers.TryGetValue("User-Agent", out string? u) && !string.IsNullOrWhiteSpace(u))
                    ua = u;
            }
            args.Append($"--referer \"{EscapeArg(referer)}\" --user-agent \"{EscapeArg(ua)}\" ");
            string? ff = FindFfmpegDir();
            if (!string.IsNullOrWhiteSpace(ff))
            {
                // Trailing '\' quoted path'te " kaçırır → URL kaybolur (yt-dlp: must provide URL)
                args.Append($"--ffmpeg-location \"{EscapeArg(TrimDir(ff))}\" ");
            }
            AppendJsRuntimeArgs(args);
        }

        /// <summary>YouTube n-sig için Deno yolunu yt-dlp'ye ver.</summary>
        public static void AppendJsRuntimeArgs(StringBuilder args)
        {
            string? deno = FindDenoExecutable();
            if (string.IsNullOrWhiteSpace(deno)) return;
            args.Append($"--js-runtimes \"deno:{EscapeArg(deno)}\" ");
        }

        private static string TrimDir(string path)
            => path.TrimEnd('\\', '/');

        /// <summary>ProcessStartInfo Arguments için " ve trailing-\ güvenli.</summary>
        private static string EscapeArg(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            // Yol sonundaki \ " ile birleşince kaçış olur
            value = value.TrimEnd('\\', '/');
            return value.Replace("\"", "\\\"");
        }

        /// <summary>Netscape cookie dosyası (BOM yok). Caller silmeli.</summary>
        public static string? WriteTempCookiesPublic(string cookies, string? pageUrl)
            => WriteTempCookies(cookies, pageUrl);

        private static string? WriteTempCookies(string cookies, string? pageUrl)
        {
            if (string.IsNullOrWhiteSpace(cookies)) return null;
            string domain = ".youtube.com";
            try
            {
                if (!string.IsNullOrWhiteSpace(pageUrl) && Uri.TryCreate(pageUrl, UriKind.Absolute, out var u))
                {
                    string host = u.Host.TrimStart('.');
                    if (host.Contains("youtube", StringComparison.OrdinalIgnoreCase)
                        || host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
                        || host.Contains("google", StringComparison.OrdinalIgnoreCase))
                        domain = ".youtube.com";
                    else
                        domain = "." + host;
                }
            }
            catch { /* ignore */ }

            var lines = new List<string>
            {
                "# Netscape HTTP Cookie File",
                "# Generated by MDM"
            };
            int count = 0;
            foreach (string part in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                int eq = p.IndexOf('=');
                if (eq <= 0) continue;
                string name = p[..eq].Trim();
                string value = p[(eq + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Tab-separated Netscape; BOM YOK (yt-dlp BOM'lu dosyayı reddeder)
                lines.Add($"{domain}\tTRUE\t/\tFALSE\t2147483647\t{name}\t{value}");
                count++;
            }
            if (count == 0) return null;

            string path = Path.Combine(Path.GetTempPath(), $"mdm-cookies-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, string.Join("\n", lines) + "\n", Utf8NoBom);
            return path;
        }

        private static string? RunProcess(string exe, string arguments, TimeSpan timeout, out string? stderr)
        {
            stderr = null;
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            proc.Start();
            var errTask = proc.StandardError.ReadToEndAsync();
            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                stderr = "timeout";
                LastError = "timeout";
                return null;
            }
            try { stderr = errTask.GetAwaiter().GetResult(); } catch { /* ignore */ }
            if (proc.ExitCode != 0)
            {
                LastError = TruncateErr(stderr) ?? $"exit {proc.ExitCode}";
                // Bazen JSON stdout'ta, exit != 0 (uyarı) — yine de dene
                if (!string.IsNullOrWhiteSpace(output) && output.TrimStart().StartsWith('{'))
                    return output;
                return null;
            }
            LastError = null;
            return output;
        }

        private static string? TruncateErr(string? err)
        {
            if (string.IsNullOrWhiteSpace(err)) return null;
            string one = err.Replace("\r", " ").Replace("\n", " ").Trim();
            if (one.Length > 160) one = one[..157] + "...";
            return one;
        }
    }
}
