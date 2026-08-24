using System.IO;

namespace DownloadMuck
{
    /// <summary>
    /// Tek akışlı protokoller (FTP/SFTP) için ortak kopyalama, pause ve resume.
    /// </summary>
    public abstract class StreamCopyBackend : ITransferBackend, IDisposable
    {
        private readonly string _savePath;
        private CancellationTokenSource? _cts;
        private IDisposable? _held;

        protected StreamCopyBackend(string savePath) => _savePath = savePath;

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

        protected void Status(string text) => StatusChanged?.Invoke(text);
        protected void NotifySize(long size) => TotalSizeKnown?.Invoke(size);
        protected void Hold(IDisposable client) => _held = client;
        protected virtual string? ProbeHost => null;

        protected abstract Task<(Stream Stream, long Total)> OpenReadAsync(long offset, CancellationToken token);

        public async Task StartOrResumeDownloadAsync()
        {
            _cts = new CancellationTokenSource();
            IsDownloading = true;
            IsPaused = false;
            IsCancelled = false;
            CompletedSuccessfully = false;
            var token = _cts.Token;

            try
            {
                while (true)
                {
                    try
                    {
                        await CopyOnceAsync(token).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        IsDownloading = false;
                        CompletedSuccessfully = false;
                        if (IsCancelled)
                        {
                            StatusChanged?.Invoke("İptal Edildi");
                            try { if (File.Exists(_savePath)) File.Delete(_savePath); } catch { /* ignore */ }
                        }
                        else
                        {
                            IsPaused = true;
                            StatusChanged?.Invoke("İndirme Duraklatıldı!");
                        }
                        return;
                    }
                    catch (Exception ex) when (!IsCancelled && NetworkWatcher.IsTransient(ex))
                    {
                        ReleaseHeld();
                        if (!await NetworkWatcher.WaitForReconnectAsync(
                                token,
                                s => StatusChanged?.Invoke(s),
                                (sp, t) => SpeedAndTimeChanged?.Invoke(sp, t),
                                ProbeHost).ConfigureAwait(false))
                        {
                            IsDownloading = false;
                            IsPaused = true;
                            CompletedSuccessfully = false;
                            StatusChanged?.Invoke("Bağlantı kesildi — kaldığı yerden devam edilebilir");
                            SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
                            return;
                        }
                    }
                }
            }
            finally
            {
                ReleaseHeld();
            }
        }

        private async Task CopyOnceAsync(CancellationToken token)
        {
            long existing = 0;
            if (File.Exists(_savePath))
            {
                try { existing = new FileInfo(_savePath).Length; } catch { existing = 0; }
            }

            var (stream, total) = await OpenReadAsync(existing, token);
            await using (stream)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_savePath) ?? ".");
                await using var fs = new FileStream(
                    _savePath,
                    existing > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read);

                byte[] buffer = new byte[65536];
                long copied = existing;
                long lastTick = Environment.TickCount64;
                long lastBytes = copied;

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int n = await stream.ReadAsync(buffer, token);
                    if (n <= 0)
                        break;
                    await fs.WriteAsync(buffer.AsMemory(0, n), token);
                    await SpeedLimiter.AwaitAsync(n, token).ConfigureAwait(false);
                    copied += n;
                    if (total > 0)
                        ProgressChanged?.Invoke(Math.Min(99.9, copied * 100.0 / total));

                    long now = Environment.TickCount64;
                    if (now - lastTick >= 1000)
                    {
                        double mb = (copied - lastBytes) / (1024.0 * 1024.0);
                        SpeedAndTimeChanged?.Invoke($"{mb:F2} MB/s", "--:--:--");
                        lastTick = now;
                        lastBytes = copied;
                    }
                }
            }

            IsDownloading = false;
            IsPaused = false;
            CompletedSuccessfully = true;
            ProgressChanged?.Invoke(100);
            SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
            StatusChanged?.Invoke("İndirme Tamamlandı!");
        }

        private void ReleaseHeld()
        {
            try { _held?.Dispose(); } catch { /* ignore */ }
            _held = null;
        }

        public void Pause()
        {
            if (IsDownloading && !IsPaused)
            {
                IsPaused = true;
                IsDownloading = false;
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

        public void Dispose()
        {
            ReleaseHeld();
            _cts?.Dispose();
        }
    }
}
