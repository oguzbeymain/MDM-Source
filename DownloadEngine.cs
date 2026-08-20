using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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

        private CancellationTokenSource? _cts;
        private List<ChunkState>? _chunks;
        private long _totalSize;
        private long _totalBytesDownloaded;
        private bool _isInitialized = false;

        public bool IsPaused { get; private set; }
        public bool IsDownloading { get; private set; }
        public bool IsCancelled { get; private set; }

        public event Action<double>? ProgressChanged;
        public event Action<string>? StatusChanged;
        public event Action<string, string>? SpeedAndTimeChanged;

        public DownloadEngine(string url, string savePath, int threadCount = 8)
        {
            _url = url;
            _savePath = savePath;
            _threadCount = threadCount;
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

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
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

            try
            {
                using HttpClient client = CreateClient();

                if (!_isInitialized)
                {
                    StatusChanged?.Invoke("Dosya bilgileri alınıyor...");

                    using HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, _url);
                    using HttpResponseMessage response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, _cts.Token);

                    long? contentLength = response.Content.Headers.ContentLength;
                    bool supportsRange = response.Headers.AcceptRanges.Contains("bytes");

                    if (!response.IsSuccessStatusCode || !contentLength.HasValue || contentLength.Value <= 0 || !supportsRange)
                    {
                        StatusChanged?.Invoke("Tek kanaldan indiriliyor...");
                        await DownloadSingleStreamAsync(response, _cts.Token);
                        return;
                    }

                    _totalSize = contentLength.Value;
                    InitChunks(_totalSize);

                    using (var fs = new FileStream(_savePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.SetLength(_totalSize);
                    }

                    _isInitialized = true;
                }

                StatusChanged?.Invoke($"İndiriliyor... ({_threadCount} Paralel Kanal)");
                await DownloadChunksAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                IsDownloading = false;

                if (IsCancelled)
                {
                    StatusChanged?.Invoke("İndirme İptal Edildi!");
                    SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
                    ProgressChanged?.Invoke(0);

                    // Sıfırlama işlemleri
                    _isInitialized = false;
                    _totalBytesDownloaded = 0;
                    _chunks = null;

                    // Yarım kalan dosyayı temizle
                    try
                    {
                        if (File.Exists(_savePath))
                        {
                            File.Delete(_savePath);
                        }
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
            catch (Exception)
            {
                IsDownloading = false;
                throw;
            }
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

            // Hız Takip Görevi
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
                        {
                            SpeedAndTimeChanged?.Invoke(speedStr, timeStr);
                        }
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
                    using HttpClient chunkClient = CreateClient();
                    var chunkRequest = new HttpRequestMessage(HttpMethod.Get, _url);
                    chunkRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(chunk.CurrentOffset, chunk.End);

                    using HttpResponseMessage chunkResponse = await chunkClient.SendAsync(chunkRequest, HttpCompletionOption.ResponseHeadersRead, token);
                    chunkResponse.EnsureSuccessStatusCode();

                    using Stream stream = await chunkResponse.Content.ReadAsStreamAsync(token);
                    byte[] buffer = new byte[8192];
                    int bytesRead;

                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        token.ThrowIfCancellationRequested();

                        await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, bytesRead), chunk.CurrentOffset, token);
                        chunk.CurrentOffset += bytesRead;

                        long currentTotal = Interlocked.Add(ref _totalBytesDownloaded, bytesRead);
                        double progress = (double)currentTotal / _totalSize * 100;
                        ProgressChanged?.Invoke(progress);
                    }
                }, token));
            }

            await Task.WhenAll(downloadTasks);

            // İndirme bittiği an Hız Zamanlayıcısını iptal et ve sonlanmasını bekle
            timerCts.Cancel();
            try { await timerTask; } catch { }

            if (!token.IsCancellationRequested)
            {
                IsDownloading = false;
                ProgressChanged?.Invoke(100);
                SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
                StatusChanged?.Invoke("İndirme Tamamlandı!");
            }
        }

        public void Pause()
        {
            if (IsDownloading && !IsPaused)
            {
                _cts?.Cancel();
            }
        }

        public void Cancel()
        {
            if (IsDownloading || IsPaused)
            {
                IsCancelled = true;
                IsPaused = false;
                _cts?.Cancel();
            }
        }

        private async Task DownloadSingleStreamAsync(HttpResponseMessage initialResponse, CancellationToken token)
        {
            initialResponse.EnsureSuccessStatusCode();
            using Stream stream = await initialResponse.Content.ReadAsStreamAsync(token);
            using FileStream fileStream = new FileStream(_savePath, FileMode.Create, FileAccess.Write);

            byte[] buffer = new byte[8192];
            int bytesRead;
            long totalDownloaded = 0;
            long? totalSize = initialResponse.Content.Headers.ContentLength;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            {
                token.ThrowIfCancellationRequested();
                await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                totalDownloaded += bytesRead;

                if (totalSize.HasValue && totalSize.Value > 0)
                {
                    double progress = (double)totalDownloaded / totalSize.Value * 100;
                    ProgressChanged?.Invoke(progress);
                }
            }

            ProgressChanged?.Invoke(100);
            SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
            StatusChanged?.Invoke("İndirme Tamamlandı!");
            IsDownloading = false;
        }
    }
}