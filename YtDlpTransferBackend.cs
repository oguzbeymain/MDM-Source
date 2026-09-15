using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MDM
{
    /// <summary>
    /// YouTube / HLS vb. için yt-dlp tabanlı indirme; mini oturum penceresiyle uyumlu.
    /// </summary>
    public sealed class YtDlpTransferBackend : ITransferBackend
    {
        // Örnek: [download]  45.2% of  123.45MiB at  1.23MiB/s ETA 00:30
        private static readonly Regex ProgressRe = new(
            @"\[download\]\s+(\d+(?:\.\d+)?)%\s+of\s+~?\s*([\d.]+)\s*([KMG]?i?B)\s+at\s+(\S+)\s+ETA\s+(\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ProgressSimpleRe = new(
            @"\[download\]\s+(\d+(?:\.\d+)?)%",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DestinationRe = new(
            @"\[download\]\s+Destination:\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string _pageOrMediaUrl;
        private readonly string _sitePageUrl;
        private readonly string _formatId;
        private readonly string _outputStem;
        private readonly string _cookies;
        private readonly IReadOnlyDictionary<string, string>? _headers;
        private readonly long _expectedBytes;

        private Process? _proc;
        private CancellationTokenSource? _cts;
        private readonly object _gate = new();
        private readonly StringBuilder _errTail = new();

        // Video+ses ayrı: bayt bazlı birleşik ilerleme (parça % sıçramasın)
        private int _expectedParts = 1;
        private int _partIndex;
        private double _lastOverall;
        private long _bytesCompletedParts;
        private long _currentPartTotal;
        private double _currentPartPct;
        private long _sizeAnchor; // probe / bilinen toplam stream boyutu
        private bool _merging;

        public YtDlpTransferBackend(
            string pageOrMediaUrl,
            string formatId,
            string outputPathWithoutExt,
            string cookies,
            IReadOnlyDictionary<string, string>? headers,
            long expectedBytes = 0,
            string? sitePageUrl = null)
        {
            _pageOrMediaUrl = pageOrMediaUrl;
            _sitePageUrl = !string.IsNullOrWhiteSpace(sitePageUrl) ? sitePageUrl! : pageOrMediaUrl;
            _formatId = YtDlpHelper.NormalizeFormatForProbe(
                string.IsNullOrWhiteSpace(formatId) ? "best" : formatId,
                pageOrMediaUrl);
            _outputStem = outputPathWithoutExt;
            _cookies = cookies ?? "";
            _headers = headers;
            _expectedBytes = expectedBytes > 0 ? expectedBytes : 0;
        }

        public bool IsPaused { get; private set; }
        public bool IsDownloading { get; private set; }
        public bool IsCancelled { get; private set; }
        public bool CompletedSuccessfully { get; private set; }

        public event Action<double>? ProgressChanged;
        public event Action<string>? StatusChanged;
        public event Action<string, string>? SpeedAndTimeChanged;
        public event Action<long>? TotalSizeKnown;
        public event Action<string, string>? OutputResolved;

        public async Task StartOrResumeDownloadAsync()
        {
            lock (_gate)
            {
                if (IsDownloading) return;
                IsDownloading = true;
                IsPaused = false;
                IsCancelled = false;
                CompletedSuccessfully = false;
                _cts = new CancellationTokenSource();
            }

            try
            {
                StatusChanged?.Invoke("Hazırlanıyor…");
                await YtDlpHelper.EnsureAvailableAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                if (!YtDlpHelper.IsAvailable())
                    throw new InvalidOperationException("yt-dlp bulunamadı");

                // ffmpeg'i BEKLEME — gyan/github zip takılırsa UI %0'da kalıyordu.
                // Yoksa arka planda kur; indirmeyi muxed tek dosyayla başlat.
                bool hasFf = YtDlpHelper.IsFfmpegAvailable();
                if (!hasFf)
                    YtDlpHelper.TryBeginFfmpegInstall();

                // YouTube: Deno yoksa indirme çoğu videoda "format missing"/403 ile düşer (probe yine çalışır).
                bool needDeno = YtDlpHelper.IsYouTubeUrl(_pageOrMediaUrl) || YtDlpHelper.IsYouTubeUrl(_sitePageUrl);
                bool hasDeno = !needDeno || YtDlpHelper.IsDenoAvailable();
                if (needDeno && !hasDeno)
                    YtDlpHelper.TryBeginDenoInstall();

                // Kısa pencere: kurulum neredeyse bittiyse kullan
                if (!hasFf || (needDeno && !hasDeno))
                {
                    for (int i = 0; i < 24 && ((!hasFf && !YtDlpHelper.IsFfmpegAvailable())
                                                || (needDeno && !YtDlpHelper.IsDenoAvailable())); i++)
                        await Task.Delay(250, _cts!.Token).ConfigureAwait(false);
                    hasFf = YtDlpHelper.IsFfmpegAvailable();
                    hasDeno = !needDeno || YtDlpHelper.IsDenoAvailable();
                }

                if (needDeno && !hasDeno)
                {
                    // Arka plan devam ederken bir kez daha bekle (ilk kurulum ~30–90 sn)
                    StatusChanged?.Invoke("YouTube için Deno kuruluyor…");
                    await YtDlpHelper.EnsureDenoAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
                    hasDeno = YtDlpHelper.IsDenoAvailable();
                    if (!hasDeno)
                        throw new InvalidOperationException(
                            "YouTube indirme için Deno kurulamadı (internet / GitHub erişimi gerekir)");
                }

                string format = ResolveFormat(_formatId, hasFf);
                _expectedParts = NeedsMerge(format) ? 2 : 1;
                _partIndex = 0;
                _lastOverall = 0;
                _bytesCompletedParts = 0;
                _currentPartTotal = 0;
                _currentPartPct = 0;
                _sizeAnchor = _expectedBytes;
                _merging = false;

                if (_sizeAnchor > 0)
                {
                    // UI zaten sizeHint ile doldurulduysa WireEngineEvents yok sayar — sabit kalsın
                    TotalSizeKnown?.Invoke(_sizeAnchor);
                }

                if (!hasFf && NeedsMerge(_formatId))
                    StatusChanged?.Invoke("İndiriliyor (tek dosya)…");
                else
                    StatusChanged?.Invoke("İndiriliyor…");

                string? saved = await Task.Run(() => RunYtDlp(format, useCookies: true, _cts!.Token), _cts.Token)
                    .ConfigureAwait(false);

                // Cookie bozuksa cookiesiz dene
                if ((string.IsNullOrWhiteSpace(saved) || !File.Exists(saved))
                    && !IsCancelled
                    && !string.IsNullOrWhiteSpace(_cookies))
                {
                    StatusChanged?.Invoke("Yeniden deneniyor…");
                    saved = await Task.Run(() => RunYtDlp(format, useCookies: false, _cts!.Token), _cts.Token)
                        .ConfigureAwait(false);
                }

                // Hâlâ birleşemediyse muxed fallback
                if ((string.IsNullOrWhiteSpace(saved) || !File.Exists(saved) || IsPartial(saved))
                    && !IsCancelled
                    && NeedsMerge(format))
                {
                    string fb = MuxedFallback(_formatId);
                    StatusChanged?.Invoke("Tek dosya kalitesi…");
                    saved = await Task.Run(() => RunYtDlp(fb, useCookies: false, _cts!.Token), _cts.Token)
                        .ConfigureAwait(false);
                }

                // Film/HLS: "720p" vb. geçersiz format → plain best
                if ((string.IsNullOrWhiteSpace(saved) || !File.Exists(saved) || IsPartial(saved))
                    && !IsCancelled
                    && !string.Equals(format, "best", StringComparison.Ordinal)
                    && FormatUnavailableError())
                {
                    StatusChanged?.Invoke("Yeniden deneniyor…");
                    saved = await Task.Run(() => RunYtDlp("best", useCookies: false, _cts!.Token), _cts.Token)
                        .ConfigureAwait(false);
                }

                if (IsCancelled)
                {
                    StatusChanged?.Invoke("İptal Edildi");
                    return;
                }

                if (string.IsNullOrWhiteSpace(saved) || !File.Exists(saved) || IsPartial(saved))
                {
                string? err = YtDlpHelper.LastError;
                if (string.IsNullOrWhiteSpace(err) && _errTail.Length > 0)
                    err = Truncate(_errTail.ToString());
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(err) ? "İndirme başarısız" : err);
                }

                long len = new FileInfo(saved).Length;
                // Boyut sabit kalsın: probe varsa bitişte değiştirme
                if (len > 0 && _expectedBytes <= 0)
                    TotalSizeKnown?.Invoke(len);
                OutputResolved?.Invoke(Path.GetFileName(saved), saved);
                ProgressChanged?.Invoke(100);
                CompletedSuccessfully = true;
                StatusChanged?.Invoke("Tamamlandı");
            }
            catch (OperationCanceledException)
            {
                if (IsCancelled)
                    StatusChanged?.Invoke("İptal Edildi");
                else
                {
                    IsPaused = true;
                    StatusChanged?.Invoke("İndirme Duraklatıldı!");
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Hata: " + Truncate(ex.Message));
                throw;
            }
            finally
            {
                IsDownloading = false;
                lock (_gate) { _proc = null; }
            }
        }

        private static bool NeedsMerge(string formatId)
            => formatId.Contains('+')
               || formatId.Contains("bv", StringComparison.OrdinalIgnoreCase);

        private bool FormatUnavailableError()
        {
            string? err = YtDlpHelper.LastError;
            string tail;
            lock (_errTail) { tail = _errTail.ToString(); }
            string blob = $"{err}\n{tail}";
            return blob.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
                   || blob.Contains("format is not available", StringComparison.OrdinalIgnoreCase)
                   || Regex.IsMatch(blob, @"ERROR:\s*\[generic\]", RegexOptions.IgnoreCase);
        }

        private string ResolveFormat(string formatId, bool hasFfmpeg)
        {
            if (hasFfmpeg || !NeedsMerge(formatId))
                return formatId;
            return MuxedFallback(formatId);
        }

        private string MuxedFallback(string formatId)
        {
            // Progressive tek dosya — merge (+ / bv*) yok; ffmpeg'siz veya merge başarısızken.
            var m = Regex.Match(Path.GetFileName(_outputStem) ?? "", @"(\d{3,4})p", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int h) && h is >= 144 and <= 4320)
                return $"b[height<={h}][ext=mp4]/b[height<={h}]/b[ext=mp4]/b";
            _ = formatId;
            return "b[ext=mp4]/b";
        }

        private static bool IsPartial(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            string name = Path.GetFileName(path);
            return Regex.IsMatch(name, @"\.f\d+\.", RegexOptions.IgnoreCase);
        }

        private string? RunYtDlp(string formatId, bool useCookies, CancellationToken token)
        {
            string? exe = YtDlpHelper.FindExecutable();
            if (exe == null) return null;

            string downloadUrl = _pageOrMediaUrl;
            if (YtDlpHelper.IsYouTubeUrl(downloadUrl))
                downloadUrl = YtDlpHelper.NormalizeYouTubeWatchUrl(downloadUrl) ?? downloadUrl;

            CleanupPartials();
            lock (_errTail) { _errTail.Clear(); }

            string? cookieFile = null;
            try
            {
                // Cookie domain = film sayfası (CDN host değil)
                if (useCookies)
                    cookieFile = WriteCookiesForProcess(_cookies, _sitePageUrl);

                var args = new StringBuilder();
                args.Append("--no-warnings --no-playlist --newline --no-check-certificates --progress ");
                args.Append($"-f \"{formatId}\" ");
                if (!string.IsNullOrWhiteSpace(cookieFile))
                    args.Append($"--cookies \"{cookieFile}\" ");

                string referer = _sitePageUrl;
                string ua = TransferHttp.UserAgent;
                if (_headers != null)
                {
                    if (_headers.TryGetValue("Referer", out string? r) && !string.IsNullOrWhiteSpace(r))
                        referer = r;
                    if (_headers.TryGetValue("User-Agent", out string? u) && !string.IsNullOrWhiteSpace(u))
                        ua = u;
                }
                // Referer asla CDN medya URL'si olmasın
                if (string.IsNullOrWhiteSpace(referer)
                    || referer.Equals(downloadUrl, StringComparison.OrdinalIgnoreCase)
                    || MediaFormatService.LooksLikeHlsUrl(referer)
                    || MediaFormatService.LooksLikeDashUrl(referer))
                {
                    if (!string.IsNullOrWhiteSpace(_sitePageUrl)
                        && !_sitePageUrl.Equals(downloadUrl, StringComparison.OrdinalIgnoreCase))
                        referer = _sitePageUrl;
                }
                args.Append($"--referer \"{referer}\" --user-agent \"{ua}\" ");
                string? ff = YtDlpHelper.FindFfmpegDir();
                if (!string.IsNullOrWhiteSpace(ff))
                {
                    ff = ff.TrimEnd('\\', '/');
                    args.Append($"--ffmpeg-location \"{ff}\" ");
                }
                if (NeedsMerge(formatId) && !string.IsNullOrWhiteSpace(ff))
                    args.Append("--merge-output-format mp4 ");
                YtDlpHelper.AppendJsRuntimeArgs(args);
                args.Append($"-o \"{_outputStem}.%(ext)s\" ");
                args.Append($"\"{downloadUrl}\"");

                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args.ToString(),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                lock (_gate) { _proc = proc; }
                if (!proc.Start())
                {
                    YtDlpHelper.LastError = "yt-dlp başlatılamadı";
                    return null;
                }

                void OnLine(string? line)
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    lock (_errTail)
                    {
                        if (_errTail.Length < 4000)
                            _errTail.AppendLine(line);
                    }
                    ParseProgressLine(line);
                }

                proc.OutputDataReceived += (_, e) => OnLine(e.Data);
                proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                while (!proc.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                        token.ThrowIfCancellationRequested();
                    }
                    Thread.Sleep(150);
                }

                proc.WaitForExit(2000);
                if (proc.ExitCode != 0 && !token.IsCancellationRequested)
                {
                    string? tail;
                    lock (_errTail) { tail = Truncate(_errTail.ToString()); }
                    YtDlpHelper.LastError = string.IsNullOrWhiteSpace(tail)
                        ? $"exit {proc.ExitCode}"
                        : tail;
                }

                return FindOutputFile();
            }
            finally
            {
                if (cookieFile != null)
                {
                    try { File.Delete(cookieFile); } catch { /* ignore */ }
                }
            }
        }

        private void ParseProgressLine(string line)
        {
            var dest = DestinationRe.Match(line);
            if (dest.Success)
            {
                if (_partIndex > 0 && _currentPartTotal > 0)
                    _bytesCompletedParts += _currentPartTotal;
                _currentPartTotal = 0;
                _currentPartPct = 0;
                _partIndex++;
                if (_partIndex > _expectedParts)
                    _expectedParts = _partIndex;

                string label = _expectedParts > 1
                    ? (_partIndex <= 1 ? "İndiriliyor (görüntü)…" : "İndiriliyor (ses)…")
                    : "İndiriliyor…";
                StatusChanged?.Invoke(label);
            }

            if (line.Contains("Merging", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Merger", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Post-process", StringComparison.OrdinalIgnoreCase))
            {
                _merging = true;
                if (_currentPartTotal > 0 && _currentPartPct < 99.5)
                {
                    _bytesCompletedParts += _currentPartTotal;
                    _currentPartTotal = 0;
                    _currentPartPct = 100;
                }
                ReportOverall(Math.Max(_lastOverall, 97), forceStatus: "Birleştiriliyor…");
                return;
            }

            var m = ProgressRe.Match(line);
            if (m.Success
                && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            {
                string speed = m.Groups[4].Value;
                string eta = m.Groups[5].Value;
                if (!string.Equals(speed, "Unknown", StringComparison.OrdinalIgnoreCase)
                    && !speed.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
                    SpeedAndTimeChanged?.Invoke(FormatSpeedLikeEngine(speed), FormatEtaLikeEngine(eta));

                string sizeTok = m.Groups[2].Value + m.Groups[3].Value;
                if (TryParseSize(sizeTok, out long partBytes) && partBytes > 0)
                    NotePartSize(partBytes);

                _currentPartPct = Math.Clamp(pct, 0, 100);
                ReportOverall(ComputeOverall());
                return;
            }

            m = ProgressSimpleRe.Match(line);
            if (m.Success
                && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct2))
            {
                // Fragment gürültüsü: parça/anchor boyutu yoksa yoksay
                if (_currentPartTotal <= 0 && _sizeAnchor <= 0)
                    return;
                _currentPartPct = Math.Clamp(pct2, 0, 100);
                ReportOverall(ComputeOverall());
            }
        }

        // İndirme sırasında boyut UI'ye yayınlama — probe sabittir; sadece ilerleme anchor'ı
        private void NotePartSize(long partBytes)
        {
            if (partBytes <= 0) return;

            if (_currentPartTotal > 1_000_000 && partBytes < _currentPartTotal * 0.25)
                return;

            if (partBytes < _currentPartTotal && partBytes > _currentPartTotal * 0.85)
                return;

            _currentPartTotal = Math.Max(_currentPartTotal, partBytes);

            // Probe/sizeHint yanlış küçükse (sadece ses) — ilerleme için gerçek parça boyutuna geç
            if (_expectedBytes > 0 && _expectedBytes < 5_000_000 && _currentPartTotal > _expectedBytes * 2)
            {
                _sizeAnchor = Math.Max(_sizeAnchor, _bytesCompletedParts + _currentPartTotal);
                if (_expectedParts > 1 && _partIndex < _expectedParts)
                    _sizeAnchor = Math.Max(_sizeAnchor, _currentPartTotal + _currentPartTotal / 10);
            }
            else if (_sizeAnchor <= 0)
            {
                long streamsKnown = _bytesCompletedParts + _currentPartTotal;
                if (_partIndex >= _expectedParts || _expectedParts <= 1)
                    _sizeAnchor = streamsKnown;
            }
            else if (_expectedBytes <= 0)
            {
                long streamsKnown = _bytesCompletedParts + _currentPartTotal;
                if (streamsKnown > _sizeAnchor * 1.15)
                    _sizeAnchor = streamsKnown;
            }
        }

        private long DownloadedBytes()
        {
            long cur = 0;
            if (_currentPartTotal > 0)
                cur = (long)(_currentPartPct / 100.0 * _currentPartTotal);
            else if (_currentPartPct > 0 && _sizeAnchor > 0 && _expectedParts > 0)
            {
                long per = _sizeAnchor / _expectedParts;
                cur = (long)(_currentPartPct / 100.0 * per);
            }
            return _bytesCompletedParts + cur;
        }

        private double ComputeOverall()
        {
            if (_merging) return Math.Max(_lastOverall, 97);

            long got = DownloadedBytes();
            long denom = _sizeAnchor;

            if (denom <= 0)
            {
                int parts = Math.Max(_expectedParts, 1);
                int idx = Math.Max(_partIndex, 1);
                double overall = ((idx - 1) * 100.0 + _currentPartPct) / parts;
                if (parts > 1)
                    overall = Math.Min(overall, 97);
                return Math.Clamp(overall, 0, 97);
            }

            double pct = got * 100.0 / denom;
            if (_expectedParts > 1)
                pct = Math.Min(pct, 97);
            return Math.Clamp(pct, 0, 97);
        }

        private void ReportOverall(double overall, string? forceStatus = null)
        {
            if (overall < _lastOverall)
                overall = _lastOverall;
            _lastOverall = overall;

            ProgressChanged?.Invoke(Math.Clamp(_lastOverall, 0, 99.9));
            if (!string.IsNullOrWhiteSpace(forceStatus))
                StatusChanged?.Invoke(forceStatus);
        }

        private static bool TryParseSize(string tok, out long bytes)
        {
            bytes = 0;
            tok = tok.Trim().Replace("i", "", StringComparison.OrdinalIgnoreCase);
            var m = Regex.Match(tok, @"^([\d.]+)\s*([KMG]?B)$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return false;
            string u = m.Groups[2].Value.ToUpperInvariant();
            bytes = u switch
            {
                "KB" => (long)(n * 1024),
                "MB" => (long)(n * 1024 * 1024),
                "GB" => (long)(n * 1024 * 1024 * 1024),
                _ => (long)n
            };
            return bytes > 0;
        }

        /// <summary>yt-dlp "8.31MiB/s" → normal indirme gibi "8,31 MB/s".</summary>
        private static string FormatSpeedLikeEngine(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "0 MB/s";
            var m = Regex.Match(raw.Trim(),
                @"^([\d.]+)\s*([KMG]?i?B)/s$",
                RegexOptions.IgnoreCase);
            if (!m.Success
                || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return raw.Trim();

            string unit = m.Groups[2].Value.Replace("i", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
            double bytesPerSec = unit switch
            {
                "KB" => n * 1024,
                "MB" => n * 1024 * 1024,
                "GB" => n * 1024 * 1024 * 1024,
                _ => n
            };
            double mbps = bytesPerSec / (1024.0 * 1024.0);
            return $"{mbps:F2} MB/s";
        }

        private static string FormatEtaLikeEngine(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)
                || raw.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("NA", StringComparison.OrdinalIgnoreCase))
                return "Hesaplanıyor...";
            // yt-dlp: 00:30 veya 01:02:03
            raw = raw.Trim();
            if (Regex.IsMatch(raw, @"^\d{1,2}:\d{2}$"))
                return "00:" + raw;
            if (Regex.IsMatch(raw, @"^\d{1,2}:\d{2}:\d{2}$"))
                return raw.Length == 7 ? "0" + raw : raw;
            return raw;
        }

        private string? FindOutputFile()
        {
            string dir = Path.GetDirectoryName(_outputStem) ?? "";
            string baseName = Path.GetFileName(_outputStem);
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, baseName + ".*")
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                            && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                            && !IsPartial(f))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        private void CleanupPartials()
        {
            try
            {
                string dir = Path.GetDirectoryName(_outputStem) ?? "";
                string baseName = Path.GetFileName(_outputStem);
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir, baseName + ".*"))
                {
                    if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                        || IsPartial(f))
                    {
                        try { File.Delete(f); } catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }
        }

        private static string? WriteCookiesForProcess(string cookies, string pageUrl)
        {
            if (string.IsNullOrWhiteSpace(cookies)) return null;
            return YtDlpHelper.WriteTempCookiesPublic(cookies, pageUrl);
        }

        public void Pause()
        {
            IsPaused = true;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            KillProc();
        }

        public void Cancel()
        {
            IsCancelled = true;
            IsPaused = false;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            KillProc();
            CleanupPartials();
            try
            {
                string? f = FindOutputFile();
                if (f != null) File.Delete(f);
            }
            catch { /* ignore */ }
        }

        private void KillProc()
        {
            lock (_gate)
            {
                try { _proc?.Kill(entireProcessTree: true); } catch { /* ignore */ }
                _proc = null;
            }
        }

        private static string Truncate(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            // ERROR: satırını öne al (uzun stderr kuyruğunda kaybolmasın)
            int errAt = s.LastIndexOf("ERROR:", StringComparison.OrdinalIgnoreCase);
            if (errAt >= 0)
                s = s[errAt..].Trim();
            return s.Length <= 180 ? s : s[..177] + "...";
        }
    }
}
