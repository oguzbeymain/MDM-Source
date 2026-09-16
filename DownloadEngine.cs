using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MDM
{
    public class ChunkState
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long CurrentOffset { get; set; }
    }

    public class DownloadEngine : ITransferBackend
    {
        /// <summary>Okuma tamponu: 64 KB yerine 1 MB — sistem çağrısı ve disk yazma sayısı düşer.</summary>
        private const int ChunkBufferBytes = 1024 * 1024;
        /// <summary>Bir okuma bu süre içinde veri getirmezse parça yeniden istenir.</summary>
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(25);
        private const int SpeedTickMs = 500;
        /// <summary>EMA katsayısı: 1'e yakın = daha durağan gösterim.</summary>
        private const double SpeedSmoothing = 0.75;

        private readonly IReadOnlyList<string> _urls;
        private readonly string _url;
        private readonly string _savePath;
        private int _threadCount;
        private readonly CookieContainer _cookieContainer = new CookieContainer();
        private readonly string _statePath;
        private readonly object _chunkLock = new();

        private CancellationTokenSource? _cts;
        private List<ChunkState>? _chunks;
        private long _totalSize;
        private long _totalBytesDownloaded;
        private bool _isInitialized = false;
        private long _lastProgressReportTimestamp;
        private int _lastProgressBucket = -1;
        private long _lastStateSaveTick;
        private bool _singleStreamMode;
        private bool _sourcePrepared;
        private string? _resolvedUrl;
        private GoFileMetadata? _goFileMetadata;

        public bool IsPaused { get; private set; }
        public bool IsDownloading { get; private set; }
        public bool IsCancelled { get; private set; }
        public bool CompletedSuccessfully { get; private set; }

        public event Action<double>? ProgressChanged;
        public event Action<string>? StatusChanged;
        public event Action<string, string>? SpeedAndTimeChanged;
        public event Action<long>? TotalSizeKnown;
#pragma warning disable CS0067
        public event Action<string, string>? OutputResolved;
#pragma warning restore CS0067

        public DownloadEngine(string url, string savePath, int threadCount = 8)
            : this(new[] { url }, savePath, threadCount)
        {
        }

        public DownloadEngine(IEnumerable<string> urls, string savePath, int threadCount = 8)
        {
            _urls = urls.Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_urls.Count == 0)
                throw new ArgumentException("URL gerekli.", nameof(urls));
            _url = _urls[0];
            _savePath = savePath;
            _threadCount = Math.Max(1, threadCount);
            _statePath = savePath + ".mdmstate";
        }

        /// <summary>Eklentiden gelen Cookie / Referer (Google Drive vb.).</summary>
        public void ApplyBrowserCapture(string? cookies, IReadOnlyDictionary<string, string>? headers)
        {
            _captureCookies = cookies ?? "";
            _captureHeaders = headers != null
                ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private string _captureCookies = "";
        private Dictionary<string, string> _captureHeaders = new(StringComparer.OrdinalIgnoreCase);

        private string UrlForAttempt(int attempt) => _resolvedUrl ?? _urls[attempt % _urls.Count];

        private HttpClient CreateClient()
        {
            bool https = _url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            bool h3 = https && AppSettingsStore.Load().PreferHttp3;
            var client = TransferHttp.CreateClient(_cookieContainer, h3);
            if (_goFileMetadata != null)
            {
                client.DefaultRequestHeaders.Remove("User-Agent");
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", _goFileMetadata.UserAgent);
                try
                {
                    _cookieContainer.SetCookies(
                        new Uri("https://gofile.io/"),
                        $"accountToken={_goFileMetadata.AccountToken}; domain=gofile.io; path=/");
                    if (Uri.TryCreate(_goFileMetadata.DownloadUrl, UriKind.Absolute, out Uri? downloadUri))
                    {
                        _cookieContainer.SetCookies(
                            new Uri($"{downloadUri.Scheme}://{downloadUri.Host}/"),
                            $"accountToken={_goFileMetadata.AccountToken}; path=/");
                    }
                }
                catch
                {
                    // The explicit Cookie header below remains the fallback.
                }
            }
            return client;
        }

        private static string HostOf(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";

        public async Task StartOrResumeDownloadAsync()
        {
            _cts = new CancellationTokenSource();
            IsDownloading = true;
            IsPaused = false;
            IsCancelled = false;
            CompletedSuccessfully = false;
            TransferLoad.Enter();
            string host = HostOf(_url);

            try
            {
                while (true)
                {
                    try
                    {
                        await RunTransferAttemptAsync(host).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        IsDownloading = false;
                        CompletedSuccessfully = false;
                        SaveState(force: true);

                        if (IsCancelled)
                        {
                            StatusChanged?.Invoke("İptal Edildi");
                            SpeedAndTimeChanged?.Invoke("", "00:00:00");
                            _isInitialized = false;
                            _totalBytesDownloaded = 0;
                            _chunks = null;
                            ClearStateFile();
                            try
                            {
                                if (File.Exists(_savePath))
                                    File.Delete(_savePath);
                            }
                            catch { }
                        }
                        else
                        {
                            IsPaused = true;
                            StatusChanged?.Invoke("İndirme Duraklatıldı!");
                            SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
                        }
                        return;
                    }
                    catch (Exception) when (_cts?.IsCancellationRequested == true && !IsCancelled)
                    {
                        IsDownloading = false;
                        IsPaused = true;
                        CompletedSuccessfully = false;
                        SaveState(force: true);
                        StatusChanged?.Invoke("İndirme Duraklatıldı!");
                        SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
                        return;
                    }
                    catch (Exception ex) when (!IsCancelled && NetworkWatcher.IsTransient(ex))
                    {
                        if (!await NetworkWatcher.WaitForReconnectAsync(
                                _cts!.Token,
                                s => StatusChanged?.Invoke(s),
                                (sp, t) => SpeedAndTimeChanged?.Invoke(sp, t),
                                host).ConfigureAwait(false))
                        {
                            IsDownloading = false;
                            IsPaused = true;
                            CompletedSuccessfully = false;
                            SaveState(force: true);
                            StatusChanged?.Invoke("Bağlantı kesildi — kaldığı yerden devam edilebilir");
                            SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
                            return;
                        }
                    }
                }
            }
            catch (Exception)
            {
                IsDownloading = false;
                CompletedSuccessfully = false;
                SaveState(force: true);
                throw;
            }
            finally
            {
                TransferLoad.Exit();
            }
        }

        private async Task RunTransferAttemptAsync(string host)
        {
            if (!_sourcePrepared)
            {
                _goFileMetadata = await GoFileResolver.TryResolveAsync(_url, _cts!.Token)
                    .ConfigureAwait(false);
                _resolvedUrl = _goFileMetadata?.DownloadUrl;
                _sourcePrepared = true;
            }

            using HttpClient client = CreateClient();

            if (!_isInitialized)
            {
                if (TryRestoreFromState())
                {
                    TotalSizeKnown?.Invoke(_totalSize);
                    StatusChanged?.Invoke("Kaldığı yerden devam ediliyor...");
                }
                else
                {
                    StatusChanged?.Invoke("Dosya bilgileri alınıyor...");

                    using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
                    headerCts.CancelAfter(TimeSpan.FromSeconds(30));

                    using HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, UrlForAttempt(0));
                    AddSourceHeaders(requestMessage);
                    HttpResponseMessage response;
                    var probeWatch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        response = await client.SendAsync(
                            requestMessage, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
                    }
                    catch (OperationCanceledException) when (!_cts.Token.IsCancellationRequested)
                    {
                        throw new TimeoutException("Dosya bilgileri alınamadı (zaman aşımı).");
                    }
                    probeWatch.Stop();
                    int rttMs = (int)Math.Clamp(probeWatch.ElapsedMilliseconds, 0, 60_000);

                    using (response)
                    {
                        EnsureBinaryResponse(response);
                        long? contentLength = response.Content.Headers.ContentLength;
                        bool supportsRange = response.Headers.AcceptRanges.Contains("bytes");

                        if (_goFileMetadata?.Size > 0 &&
                            contentLength.HasValue &&
                            contentLength.Value != _goFileMetadata.Size)
                        {
                            throw new InvalidDataException(
                                $"GoFile boyut doğrulaması başarısız ({contentLength.Value} / {_goFileMetadata.Size} bayt).");
                        }

                        if (!response.IsSuccessStatusCode || !contentLength.HasValue || contentLength.Value <= 0 || !supportsRange)
                        {
                            if (contentLength.HasValue && contentLength.Value > 0)
                                TotalSizeKnown?.Invoke(contentLength.Value);

                            _singleStreamMode = true;
                            StatusChanged?.Invoke("Tek kanaldan indiriliyor...");
                            await DownloadSingleStreamAsync(response, _cts.Token);
                            if (CompletedSuccessfully)
                                ClearStateFile();
                            return;
                        }

                        _totalSize = contentLength.Value;
                        TotalSizeKnown?.Invoke(_totalSize);
                        _threadCount = Math.Min(
                            _threadCount,
                            ChannelBudget.ForJob(_totalSize, rttMs, TransferLoad.ActiveJobs, HostLoad.Active(host)));
                        InitChunks(_totalSize);

                        using (var fs = new FileStream(_savePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                        {
                            fs.SetLength(_totalSize);
                        }

                        _isInitialized = true;
                        SaveState(force: true);
                    }
                }
            }

            if (_isInitialized && TryCompleteIfAlreadyDownloaded())
            {
                CompletedSuccessfully = true;
                ClearStateFile();
                return;
            }

            int reserved = Math.Max(1, _threadCount);
            HostLoad.Add(host, reserved);
            try
            {
                int pieces = _chunks?.Count ?? _threadCount;
                int channels = Math.Min(_threadCount, Math.Max(1, pieces));
                StatusChanged?.Invoke($"İndiriliyor... ({channels} kanal · {pieces} parça)");
                await DownloadChunksAsync(client, _cts!.Token);
                if (CompletedSuccessfully)
                    ClearStateFile();
            }
            finally
            {
                HostLoad.Remove(host, reserved);
            }
        }

        private bool TryRestoreFromState()
        {
            try
            {
                if (!File.Exists(_statePath) || !File.Exists(_savePath))
                    return false;

                var dto = JsonSerializer.Deserialize<EngineStateDto>(File.ReadAllText(_statePath));
                if (dto == null || dto.TotalSize <= 0 || dto.Chunks == null || dto.Chunks.Count == 0)
                    return false;
                if (!string.Equals(dto.Url, _url, StringComparison.OrdinalIgnoreCase))
                    return false;

                var info = new FileInfo(_savePath);
                if (info.Length != dto.TotalSize && info.Length < Math.Min(dto.TotalSize, 1024))
                    return false;

                _totalSize = dto.TotalSize;
                _singleStreamMode = dto.SingleStream;
                _chunks = dto.Chunks.Select(c => new ChunkState
                {
                    Start = c.Start,
                    End = c.End,
                    CurrentOffset = Math.Clamp(c.CurrentOffset, c.Start, c.End + 1)
                }).ToList();

                _totalBytesDownloaded = 0;
                foreach (var chunk in _chunks)
                    _totalBytesDownloaded += Math.Max(0, chunk.CurrentOffset - chunk.Start);

                // Dosya boyutu eksikse uzat
                using (var fs = new FileStream(_savePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    if (fs.Length < _totalSize)
                        fs.SetLength(_totalSize);
                }

                _isInitialized = true;
                double progress = _totalSize > 0 ? (double)_totalBytesDownloaded / _totalSize * 100 : 0;
                ProgressChanged?.Invoke(Math.Min(99.9, progress));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SaveState(bool force = false)
        {
            try
            {
                if (_chunks == null || _totalSize <= 0 || IsCancelled)
                    return;

                long now = Environment.TickCount64;
                if (!force && now - _lastStateSaveTick < 1500)
                    return;
                _lastStateSaveTick = now;

                var dto = new EngineStateDto
                {
                    Url = _url,
                    TotalSize = _totalSize,
                    SingleStream = _singleStreamMode,
                    Chunks = _chunks.Select(c => new ChunkDto
                    {
                        Start = c.Start,
                        End = c.End,
                        CurrentOffset = c.CurrentOffset
                    }).ToList()
                };

                string dir = Path.GetDirectoryName(_statePath) ?? "";
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_statePath, JsonSerializer.Serialize(dto));
            }
            catch { /* ignore */ }
        }

        private void ClearStateFile()
        {
            try
            {
                if (File.Exists(_statePath))
                    File.Delete(_statePath);
            }
            catch { /* ignore */ }
        }

        private bool TryCompleteIfAlreadyDownloaded()
        {
            if (_chunks == null || _totalSize <= 0) return false;

            long downloaded = 0;
            bool allDone = true;
            foreach (var chunk in _chunks)
            {
                long written = Math.Max(0, chunk.CurrentOffset - chunk.Start);
                long expected = chunk.End - chunk.Start + 1;
                downloaded += Math.Min(written, expected);
                if (chunk.CurrentOffset <= chunk.End)
                    allDone = false;
            }

            _totalBytesDownloaded = downloaded;

            if (!allDone && downloaded < _totalSize)
                return false;

            IsDownloading = false;
            IsPaused = false;
            CompletedSuccessfully = true;
            ProgressChanged?.Invoke(100);
            SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
            StatusChanged?.Invoke("İndirme Tamamlandı!");
            return true;
        }

        private void InitChunks(long totalSize)
        {
            _chunks = SegmentPlanner.BuildChunks(totalSize, _threadCount);
        }

        private async Task DownloadChunksAsync(HttpClient client, CancellationToken token)
        {
            using var fileStream = new FileStream(_savePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
            var fileHandle = fileStream.SafeFileHandle;

            while (true)
            {
                var pending = new ConcurrentQueue<ChunkState>();
                foreach (var chunk in _chunks!)
                {
                    if (chunk.CurrentOffset <= chunk.End)
                        pending.Enqueue(chunk);
                }

                using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var timerTask = Task.Run(() => RunSpeedMonitorAsync(timerCts.Token, saveState: true));

                int workerCount = Math.Min(_threadCount, Math.Max(1, pending.Count));
                var workers = new List<Task>(workerCount);
                for (int i = 0; i < workerCount; i++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        while (!token.IsCancellationRequested)
                        {
                            ChunkState? chunk = null;
                            if (pending.TryDequeue(out var queued))
                                chunk = queued;
                            else
                            {
                                lock (_chunkLock)
                                {
                                    var stolen = SegmentPlanner.TrySplitSlowest(_chunks!, SegmentPlanner.MinChunkBytes);
                                    if (stolen != null)
                                    {
                                        _chunks!.Add(stolen);
                                        chunk = stolen;
                                    }
                                }
                            }

                            if (chunk == null)
                                return;
                            await DownloadOneChunkAsync(client, chunk, fileHandle, token);
                        }
                    }, token));
                }

                try
                {
                    await Task.WhenAll(workers);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                }

                timerCts.Cancel();
                try { await timerTask; } catch { }

                SaveState(force: true);
                token.ThrowIfCancellationRequested();

                if (AreAllChunksComplete())
                    break;

                if (!await NetworkWatcher.WaitForReconnectAsync(
                        token,
                        s => StatusChanged?.Invoke(s),
                        (sp, t) => SpeedAndTimeChanged?.Invoke(sp, t),
                        HostOf(_url)).ConfigureAwait(false))
                {
                    IsDownloading = false;
                    IsPaused = true;
                    CompletedSuccessfully = false;
                    StatusChanged?.Invoke("Bağlantı kesildi — kaldığı yerden devam edilebilir");
                    SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
                    return;
                }

                int pieces = _chunks?.Count ?? _threadCount;
                int channels = Math.Min(_threadCount, Math.Max(1, pieces));
                StatusChanged?.Invoke($"İndiriliyor... ({channels} kanal · {pieces} parça)");
            }

            IsDownloading = false;
            IsPaused = false;
            CompletedSuccessfully = true;
            ProgressChanged?.Invoke(100);
            SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
            StatusChanged?.Invoke("İndirme Tamamlandı!");
        }

        private async Task DownloadOneChunkAsync(HttpClient client, ChunkState chunk, Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle, CancellationToken token)
        {
            const int maxRetries = 4;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    // Paylaşılan istemci: parça başına yeni TCP/TLS el sıkışması yok, bağlantı sıcak kalır
                    var chunkRequest = new HttpRequestMessage(HttpMethod.Get, UrlForAttempt(attempt));
                    // Range parçaları için 1.1: her parça ayrı TCP bağlantısında akar,
                    // tek H2/H3 bağlantısının akış penceresine sıkışmaz
                    chunkRequest.Version = HttpVersion.Version11;
                    chunkRequest.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                    AddSourceHeaders(chunkRequest);
                    chunkRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(chunk.CurrentOffset, chunk.End);

                    using HttpResponseMessage chunkResponse = await client.SendAsync(chunkRequest, HttpCompletionOption.ResponseHeadersRead, token);
                    if (token.IsCancellationRequested) return;
                    chunkResponse.EnsureSuccessStatusCode();
                    if (chunkResponse.StatusCode != HttpStatusCode.PartialContent)
                        throw new IOException("Sunucu aralık indirmesini desteklemedi; parça kaydedilmedi.");
                    EnsureBinaryResponse(chunkResponse);
                    if (chunkResponse.Content.Headers.ContentRange?.From != chunk.CurrentOffset)
                        throw new IOException("Sunucu yanlış dosya aralığı döndürdü; parça kaydedilmedi.");

                    using Stream stream = await chunkResponse.Content.ReadAsStreamAsync(token);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBufferBytes);
                    try
                    {
                        while (chunk.CurrentOffset <= chunk.End)
                        {
                            long remaining = chunk.End - chunk.CurrentOffset + 1;
                            if (remaining <= 0)
                                break;
                            int toRead = (int)Math.Min(buffer.Length, remaining);
                            int bytesRead = await ReadWithTimeoutAsync(stream, buffer.AsMemory(0, toRead), token)
                                .ConfigureAwait(false);
                            if (bytesRead <= 0)
                                break;

                            token.ThrowIfCancellationRequested();
                            remaining = chunk.End - chunk.CurrentOffset + 1;
                            if (remaining <= 0)
                                break;
                            int toWrite = (int)Math.Min(bytesRead, remaining);

                            await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, toWrite), chunk.CurrentOffset, token);
                            await SpeedLimiter.AwaitAsync(toWrite, token).ConfigureAwait(false);
                            chunk.CurrentOffset += toWrite;

                            long currentTotal = Interlocked.Add(ref _totalBytesDownloaded, toWrite);
                            double progress = (double)currentTotal / _totalSize * 100;
                            ReportProgressThrottled(progress);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    if (chunk.CurrentOffset <= chunk.End)
                        throw new IOException($"Chunk incomplete at {chunk.CurrentOffset}/{chunk.End}");

                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) when (token.IsCancellationRequested) { return; }
                catch
                {
                    if (attempt >= maxRetries - 1 || token.IsCancellationRequested)
                        throw;
                    // Takılan parça kaldığı bayttan yeniden istenir; bekleme kısa tutulur
                    await Task.Delay(300 * (attempt + 1), token);
                }
            }
        }

        /// <summary>
        /// Hız/kalan süre göstergesi. Anlık ölçüm TCP dalgalanmasıyla zıpladığı için
        /// EMA ile yumuşatılır; hem parçalı hem tek kanallı indirme bunu kullanır.
        /// </summary>
        private async Task RunSpeedMonitorAsync(CancellationToken token, bool saveState)
        {
            long lastBytes = Interlocked.Read(ref _totalBytesDownloaded);
            long lastTick = Environment.TickCount64;
            double smoothed = 0;   // bayt/sn

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(SpeedTickMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                long total = Volatile.Read(ref _totalSize);
                long currentBytes = Interlocked.Read(ref _totalBytesDownloaded);
                if (total > 0 && currentBytes >= total)
                    return;

                long now = Environment.TickCount64;
                long elapsedMs = Math.Max(1, now - lastTick);
                lastTick = now;

                double instant = Math.Max(0, currentBytes - lastBytes) * 1000.0 / elapsedMs;
                lastBytes = currentBytes;
                smoothed = smoothed <= 0 ? instant : smoothed * SpeedSmoothing + instant * (1 - SpeedSmoothing);

                string speedStr = $"{smoothed / (1024 * 1024):F2} MB/s";
                string timeStr = "Hesaplanıyor...";
                if (smoothed > 1024 && total > 0)
                {
                    TimeSpan left = TimeSpan.FromSeconds(Math.Max(0, total - currentBytes) / smoothed);
                    timeStr = left.TotalHours >= 100 ? "--:--:--" : left.ToString(@"hh\:mm\:ss");
                }

                if (!token.IsCancellationRequested)
                    SpeedAndTimeChanged?.Invoke(speedStr, timeStr);

                if (saveState)
                    SaveState();
            }
        }

        /// <summary>
        /// Sessizce ölen bağlantı hızın dibe vurup toparlanmamasına yol açıyordu; okuma
        /// zaman aşımına düşerse çağıran parçayı kaldığı yerden yeniden ister.
        /// </summary>
        private static async ValueTask<int> ReadWithTimeoutAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            readCts.CancelAfter(ReadTimeout);
            try
            {
                return await stream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException("Parça yanıt vermedi (zaman aşımı).");
            }
        }

        private bool AreAllChunksComplete()
        {
            if (_chunks == null || _chunks.Count == 0) return false;
            foreach (var chunk in _chunks)
            {
                if (chunk.CurrentOffset <= chunk.End)
                    return false;
            }
            return true;
        }

        public void Pause()
        {
            if (IsDownloading && !IsPaused)
            {
                IsPaused = true;
                CompletedSuccessfully = false;
                IsDownloading = false;
                SaveState(force: true);
                _cts?.Cancel();
            }
        }

        public void Cancel()
        {
            if (IsDownloading || IsPaused)
            {
                IsCancelled = true;
                IsPaused = false;
                CompletedSuccessfully = false;
                _cts?.Cancel();
            }
        }

        private void ReportProgressThrottled(double progress)
        {
            int bucket = progress >= 100 ? int.MaxValue : (int)(progress * 2);
            long now = Environment.TickCount64;
            int lastBucket = Volatile.Read(ref _lastProgressBucket);
            long lastTs = Interlocked.Read(ref _lastProgressReportTimestamp);

            if (bucket != int.MaxValue && bucket == lastBucket && now - lastTs < 250)
                return;

            Volatile.Write(ref _lastProgressBucket, bucket);
            Interlocked.Exchange(ref _lastProgressReportTimestamp, now);
            ProgressChanged?.Invoke(progress > 100 ? 100 : progress);
        }

        private async Task DownloadSingleStreamAsync(HttpResponseMessage initialResponse, CancellationToken token)
        {
            long existing = 0;
            if (File.Exists(_savePath))
            {
                try { existing = new FileInfo(_savePath).Length; } catch { existing = 0; }
            }

            // Range ile kaldığı yerden dene
            if (existing > 0)
            {
                try
                {
                    using HttpClient client = CreateClient();
                    var req = new HttpRequestMessage(HttpMethod.Get, UrlForAttempt(0));
                    AddSourceHeaders(req);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
                    using var rangeResp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                    if ((int)rangeResp.StatusCode == 206)
                    {
                        await AppendSingleStreamAsync(rangeResp, existing, token);
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* sifirdan */ }
            }

            if (!initialResponse.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)initialResponse.StatusCode} ({initialResponse.StatusCode}).");
            EnsureBinaryResponse(initialResponse);

            using Stream stream = await initialResponse.Content.ReadAsStreamAsync(token);
            using FileStream fileStream = CreateSequentialFile(_savePath, FileMode.Create);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBufferBytes);
            int bytesRead;
            long totalDownloaded = 0;
            long? totalSize = initialResponse.Content.Headers.ContentLength;

            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Volatile.Write(ref _totalSize, totalSize ?? 0);
            Interlocked.Exchange(ref _totalBytesDownloaded, 0);
            var monitor = Task.Run(() => RunSpeedMonitorAsync(monitorCts.Token, saveState: false));

            try
            {
                while ((bytesRead = await ReadWithTimeoutAsync(stream, buffer, token).ConfigureAwait(false)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
                    await SpeedLimiter.AwaitAsync(bytesRead, token).ConfigureAwait(false);
                    totalDownloaded += bytesRead;
                    Interlocked.Exchange(ref _totalBytesDownloaded, totalDownloaded);

                    if (totalSize.HasValue && totalSize.Value > 0)
                    {
                        double progress = (double)totalDownloaded / totalSize.Value * 100;
                        ReportProgressThrottled(progress);
                    }
                }

                if (totalSize.HasValue && totalDownloaded != totalSize.Value)
                    throw new IOException($"İndirme eksik kaldı ({totalDownloaded}/{totalSize.Value} bayt).");

                ProgressChanged?.Invoke(100);
                SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
                StatusChanged?.Invoke("İndirme Tamamlandı!");
                IsDownloading = false;
                IsPaused = false;
                CompletedSuccessfully = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                monitorCts.Cancel();
                try { await monitor.ConfigureAwait(false); } catch { }
            }
        }

        /// <summary>Sıralı yazma için büyük tamponlu, asenkron dosya akışı.</summary>
        private static FileStream CreateSequentialFile(string path, FileMode mode)
            => new FileStream(path, mode, FileAccess.Write, FileShare.Read, ChunkBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

        private async Task AppendSingleStreamAsync(HttpResponseMessage response, long existing, CancellationToken token)
        {
            long? remaining = response.Content.Headers.ContentLength;
            long? totalSize = remaining.HasValue ? existing + remaining.Value : null;
            if (totalSize.HasValue)
                TotalSizeKnown?.Invoke(totalSize.Value);

            using Stream stream = await response.Content.ReadAsStreamAsync(token);
            using FileStream fileStream = CreateSequentialFile(_savePath, FileMode.Append);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkBufferBytes);
            int bytesRead;
            long totalDownloaded = existing;

            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Volatile.Write(ref _totalSize, totalSize ?? 0);
            Interlocked.Exchange(ref _totalBytesDownloaded, existing);
            var monitor = Task.Run(() => RunSpeedMonitorAsync(monitorCts.Token, saveState: false));

            try
            {
                while ((bytesRead = await ReadWithTimeoutAsync(stream, buffer, token).ConfigureAwait(false)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
                    await SpeedLimiter.AwaitAsync(bytesRead, token).ConfigureAwait(false);
                    totalDownloaded += bytesRead;
                    Interlocked.Exchange(ref _totalBytesDownloaded, totalDownloaded);
                    if (totalSize.HasValue && totalSize.Value > 0)
                        ReportProgressThrottled((double)totalDownloaded / totalSize.Value * 100);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                monitorCts.Cancel();
                try { await monitor.ConfigureAwait(false); } catch { }
            }

            if (totalSize.HasValue && totalDownloaded != totalSize.Value)
                throw new IOException($"İndirme eksik kaldı ({totalDownloaded}/{totalSize.Value} bayt).");

            ProgressChanged?.Invoke(100);
            SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
            StatusChanged?.Invoke("İndirme Tamamlandı!");
            IsDownloading = false;
            IsPaused = false;
            CompletedSuccessfully = true;
        }

        private void AddSourceHeaders(HttpRequestMessage request)
        {
            if (_goFileMetadata != null)
            {
                GoFileResolver.AddWebsiteHeaders(
                    request, _goFileMetadata.AccountToken, _goFileMetadata.WebsiteToken,
                    _goFileMetadata.ContentId);
                return;
            }

            foreach (var kv in _captureHeaders)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                try { request.Headers.TryAddWithoutValidation(kv.Key, kv.Value); } catch { /* ignore */ }
            }

            if (!string.IsNullOrWhiteSpace(_captureCookies))
            {
                try { request.Headers.Remove("Cookie"); } catch { /* ignore */ }
                request.Headers.TryAddWithoutValidation("Cookie", _captureCookies);
            }

            // Google Drive: Referer yoksa ekle
            if (Uri.TryCreate(_url, UriKind.Absolute, out var uri)
                && (uri.Host.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase))
                && !request.Headers.Contains("Referer"))
            {
                request.Headers.TryAddWithoutValidation("Referer", "https://drive.google.com/");
            }
        }

        private static void EnsureBinaryResponse(HttpResponseMessage response)
        {
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType) &&
                (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
                 mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    "Sunucu dosya yerine bir web sayfası döndürdü; dosya kaydedilmedi.");
            }
        }

        private sealed class EngineStateDto
        {
            public string Url { get; set; } = "";
            public long TotalSize { get; set; }
            public bool SingleStream { get; set; }
            public List<ChunkDto>? Chunks { get; set; }
        }

        private sealed class ChunkDto
        {
            public long Start { get; set; }
            public long End { get; set; }
            public long CurrentOffset { get; set; }
        }
    }
}
