using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DownloadMuck
{
    public class ChunkState
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long CurrentOffset { get; set; }
    }

    public class DownloadEngine
    {
        private readonly string _url;
        private readonly string _savePath;
        private readonly int _threadCount;
        private readonly CookieContainer _cookieContainer = new CookieContainer();
        private readonly string _statePath;

        private CancellationTokenSource? _cts;
        private List<ChunkState>? _chunks;
        private long _totalSize;
        private long _totalBytesDownloaded;
        private bool _isInitialized = false;
        private long _lastProgressReportTimestamp;
        private int _lastProgressBucket = -1;
        private long _lastStateSaveTick;
        private bool _singleStreamMode;

        public bool IsPaused { get; private set; }
        public bool IsDownloading { get; private set; }
        public bool IsCancelled { get; private set; }
        public bool CompletedSuccessfully { get; private set; }

        public event Action<double>? ProgressChanged;
        public event Action<string>? StatusChanged;
        public event Action<string, string>? SpeedAndTimeChanged;
        public event Action<long>? TotalSizeKnown;

        public DownloadEngine(string url, string savePath, int threadCount = 8)
        {
            _url = url;
            _savePath = savePath;
            _threadCount = threadCount;
            _statePath = savePath + ".mdmstate";
        }

        private HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                UseCookies = true,
                CookieContainer = _cookieContainer
            };

            // Gövde indirmesi uzun sürebilir; iptal için CancellationToken kullanılır.
            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return client;
        }

        public async Task StartOrResumeDownloadAsync()
        {
            _cts = new CancellationTokenSource();
            IsDownloading = true;
            IsPaused = false;
            IsCancelled = false;
            CompletedSuccessfully = false;

            try
            {
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

                        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        headerCts.CancelAfter(TimeSpan.FromSeconds(30));

                        using HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, _url);
                        HttpResponseMessage response;
                        try
                        {
                            response = await client.SendAsync(
                                requestMessage, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
                        }
                        catch (OperationCanceledException) when (!_cts.Token.IsCancellationRequested)
                        {
                            throw new TimeoutException("Dosya bilgileri alınamadı (zaman aşımı).");
                        }

                        using (response)
                        {
                            long? contentLength = response.Content.Headers.ContentLength;
                            bool supportsRange = response.Headers.AcceptRanges.Contains("bytes");

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

                StatusChanged?.Invoke($"İndiriliyor... ({_threadCount} Paralel Kanal)");
                await DownloadChunksAsync(_cts.Token);
                if (CompletedSuccessfully)
                    ClearStateFile();
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
            }
            catch (TimeoutException)
            {
                IsDownloading = false;
                IsPaused = true;
                CompletedSuccessfully = false;
                SaveState(force: true);
                StatusChanged?.Invoke("Bağlantı zaman aşımı — kaldığı yerden devam edilebilir");
                SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
            }
            catch (Exception) when (_cts?.IsCancellationRequested == true && !IsCancelled)
            {
                IsDownloading = false;
                IsPaused = true;
                CompletedSuccessfully = false;
                SaveState(force: true);
                StatusChanged?.Invoke("İndirme Duraklatıldı!");
                SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
            }
            catch (HttpRequestException)
            {
                // Ağ kesintisi — kaldığı yerden devam için duraklat
                IsDownloading = false;
                IsPaused = true;
                CompletedSuccessfully = false;
                SaveState(force: true);
                StatusChanged?.Invoke("Bağlantı kesildi — kaldığı yerden devam edilebilir");
                SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
            }
            catch (IOException)
            {
                IsDownloading = false;
                IsPaused = true;
                CompletedSuccessfully = false;
                SaveState(force: true);
                StatusChanged?.Invoke("Yazma hatası — kaldığı yerden devam edilebilir");
                SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
            }
            catch (Exception)
            {
                IsDownloading = false;
                CompletedSuccessfully = false;
                SaveState(force: true);
                throw;
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
                if (!force && now - _lastStateSaveTick < 800)
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
            _chunks = new List<ChunkState>();
            long chunkSize = totalSize / _threadCount;

            for (int i = 0; i < _threadCount; i++)
            {
                long start = i * chunkSize;
                long end = (i == _threadCount - 1) ? totalSize - 1 : (start + chunkSize - 1);
                _chunks.Add(new ChunkState
                {
                    Start = start,
                    End = end,
                    CurrentOffset = start
                });
            }
        }

        private async Task DownloadChunksAsync(CancellationToken token)
        {
            List<Task> downloadTasks = new List<Task>();

            using var fileStream = new FileStream(_savePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
            var fileHandle = fileStream.SafeFileHandle;

            using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            long lastBytes = _totalBytesDownloaded;

            var timerTask = Task.Run(async () =>
            {
                while (!timerCts.Token.IsCancellationRequested && _totalBytesDownloaded < _totalSize)
                {
                    try
                    {
                        await Task.Delay(1000, timerCts.Token);
                        long currentBytes = _totalBytesDownloaded;
                        long bytesInLastSecond = currentBytes - lastBytes;
                        lastBytes = currentBytes;

                        double speedMBps = (double)bytesInLastSecond / (1024 * 1024);
                        long bytesRemaining = _totalSize - currentBytes;

                        string speedStr = $"{speedMBps:F2} MB/s";
                        string timeStr = "Hesaplanıyor...";

                        if (bytesInLastSecond > 0)
                        {
                            double secondsRemaining = (double)bytesRemaining / bytesInLastSecond;
                            TimeSpan t = TimeSpan.FromSeconds(secondsRemaining);
                            timeStr = t.ToString(@"hh\:mm\:ss");
                        }

                        if (!timerCts.Token.IsCancellationRequested && _totalBytesDownloaded < _totalSize)
                            SpeedAndTimeChanged?.Invoke(speedStr, timeStr);

                        SaveState();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });

            foreach (var chunk in _chunks!)
            {
                if (chunk.CurrentOffset > chunk.End) continue;

                downloadTasks.Add(Task.Run(async () =>
                {
                    const int maxRetries = 4;
                    for (int attempt = 0; attempt < maxRetries; attempt++)
                    {
                        try
                        {
                            using HttpClient chunkClient = CreateClient();
                            var chunkRequest = new HttpRequestMessage(HttpMethod.Get, _url);
                            chunkRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(chunk.CurrentOffset, chunk.End);

                            using HttpResponseMessage chunkResponse = await chunkClient.SendAsync(chunkRequest, HttpCompletionOption.ResponseHeadersRead, token);
                            if (token.IsCancellationRequested) return;
                            chunkResponse.EnsureSuccessStatusCode();

                            using Stream stream = await chunkResponse.Content.ReadAsStreamAsync(token);
                            byte[] buffer = new byte[65536];
                            int bytesRead;

                            while (chunk.CurrentOffset <= chunk.End)
                            {
                                long remaining = chunk.End - chunk.CurrentOffset + 1;
                                int toRead = (int)Math.Min(buffer.Length, remaining);
                                bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), token);
                                if (bytesRead <= 0)
                                    break;

                                token.ThrowIfCancellationRequested();

                                await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, bytesRead), chunk.CurrentOffset, token);
                                chunk.CurrentOffset += bytesRead;

                                long currentTotal = Interlocked.Add(ref _totalBytesDownloaded, bytesRead);
                                double progress = (double)currentTotal / _totalSize * 100;
                                ReportProgressThrottled(progress);
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
                            await Task.Delay(600 * (attempt + 1), token);
                        }
                    }
                }, token));
            }

            try
            {
                await Task.WhenAll(downloadTasks);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
            }

            timerCts.Cancel();
            try { await timerTask; } catch { }

            SaveState(force: true);
            token.ThrowIfCancellationRequested();

            if (!AreAllChunksComplete())
            {
                IsDownloading = false;
                IsPaused = true;
                CompletedSuccessfully = false;
                StatusChanged?.Invoke("Bağlantı kesildi — kaldığı yerden devam edilebilir");
                SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
                return;
            }

            IsDownloading = false;
            IsPaused = false;
            CompletedSuccessfully = true;
            ProgressChanged?.Invoke(100);
            SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
            StatusChanged?.Invoke("İndirme Tamamlandı!");
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
                    var req = new HttpRequestMessage(HttpMethod.Get, _url);
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

            using Stream stream = await initialResponse.Content.ReadAsStreamAsync(token);
            using FileStream fileStream = new FileStream(_savePath, FileMode.Create, FileAccess.Write);

            byte[] buffer = new byte[65536];
            int bytesRead;
            long totalDownloaded = 0;
            long? totalSize = initialResponse.Content.Headers.ContentLength;

            try
            {
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                    totalDownloaded += bytesRead;

                    if (totalSize.HasValue && totalSize.Value > 0)
                    {
                        double progress = (double)totalDownloaded / totalSize.Value * 100;
                        ReportProgressThrottled(progress);
                    }
                }

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
        }

        private async Task AppendSingleStreamAsync(HttpResponseMessage response, long existing, CancellationToken token)
        {
            long? remaining = response.Content.Headers.ContentLength;
            long? totalSize = remaining.HasValue ? existing + remaining.Value : null;
            if (totalSize.HasValue)
                TotalSizeKnown?.Invoke(totalSize.Value);

            using Stream stream = await response.Content.ReadAsStreamAsync(token);
            using FileStream fileStream = new FileStream(_savePath, FileMode.Append, FileAccess.Write);

            byte[] buffer = new byte[65536];
            int bytesRead;
            long totalDownloaded = existing;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            {
                token.ThrowIfCancellationRequested();
                await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                totalDownloaded += bytesRead;
                if (totalSize.HasValue && totalSize.Value > 0)
                    ReportProgressThrottled((double)totalDownloaded / totalSize.Value * 100);
            }

            ProgressChanged?.Invoke(100);
            SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
            StatusChanged?.Invoke("İndirme Tamamlandı!");
            IsDownloading = false;
            IsPaused = false;
            CompletedSuccessfully = true;
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
