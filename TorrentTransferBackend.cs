using System.IO;
using System.Net.Http;
using MonoTorrent;
using MonoTorrent.Client;

namespace MDM
{
    public sealed class TorrentTransferBackend : ITransferBackend
    {
        private readonly string _source;
        private readonly string _savePath;
        private readonly ClientEngine? _injected;
        private CancellationTokenSource? _cts;
        private TorrentManager? _manager;

        public TorrentTransferBackend(string source, string savePath, ClientEngine? engine = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _savePath = savePath ?? throw new ArgumentNullException(nameof(savePath));
            _injected = engine;
        }

        private ClientEngine Engine => _injected ?? TorrentEngineHost.Shared;
        private bool UseAppPolicy => _injected == null;
        private int _recoverDelayMs = 400;

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
            _cts = new CancellationTokenSource();
            IsDownloading = true;
            IsPaused = false;
            IsCancelled = false;
            CompletedSuccessfully = false;
            var token = _cts.Token;

            string saveDir = Path.GetDirectoryName(_savePath) ?? ".";
            Directory.CreateDirectory(saveDir);

            double seedRatio = UseAppPolicy ? Math.Max(0, AppSettingsStore.Load().TorrentSeedRatio) : 0;
            bool sequential = UseAppPolicy && AppSettingsStore.Load().TorrentSequential;

            try
            {
                while (true)
                {
                    try
                    {
                        if (_manager == null)
                        {
                            StatusChanged?.Invoke("Torrent hazırlanıyor...");
                            _manager = await CreateManagerAsync(saveDir, token).ConfigureAwait(false);
                        }

                        await EnsureRunningAsync(token).ConfigureAwait(false);

                        while (!token.IsCancellationRequested)
                        {
                            var state = _manager.State;
                            if (state == TorrentState.Error)
                            {
                                await RecoverFromErrorAsync(token).ConfigureAwait(false);
                                continue;
                            }

                            if (UseAppPolicy
                                && NetworkWatcher.AutoReconnectEnabled
                                && !NetworkWatcher.Shared.IsOnline)
                            {
                                StatusChanged?.Invoke("Ağ bekleniyor...");
                                SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
                                await NetworkWatcher.Shared.WaitUntilOnlineAsync(token).ConfigureAwait(false);
                                await EnsureRunningAsync(token).ConfigureAwait(false);
                                continue;
                            }

                    if (_manager.Torrent != null)
                    {
                        TotalSizeKnown?.Invoke(_manager.Torrent.Size);
                        string name = string.IsNullOrWhiteSpace(_manager.Torrent.Name)
                            ? Path.GetFileName(_savePath)
                            : _manager.Torrent.Name;
                        string path = _manager.Files.Count == 1
                            ? _manager.Files[0].FullPath
                            : Path.Combine(saveDir, name);
                        OutputResolved?.Invoke(FileNameHelper.DecodeDisplayName(name), path);
                    }

                    if (sequential)
                        await ApplySequentialAsync().ConfigureAwait(false);

                    if (IsDownloadComplete(state))
                    {
                        ProgressChanged?.Invoke(100);
                        if (seedRatio <= 0.0001)
                            break;

                        double down = Math.Max(1, DownloadedBytes(_manager));
                        double up = UploadedBytes(_manager);
                        double ratio = up / down;
                        StatusChanged?.Invoke($"Torrent paylaşılıyor... ({ratio:0.00}x)");
                        SpeedAndTimeChanged?.Invoke(FormatRate(_manager.Monitor.UploadRate), Eta(_manager));
                        if (ratio >= seedRatio)
                            break;

                        await Task.Delay(500, token).ConfigureAwait(false);
                        continue;
                    }

                    if (state == TorrentState.Downloading && _manager.Monitor.DownloadRate > 0)
                        _recoverDelayMs = 400;

                    if (state == TorrentState.Metadata)
                        StatusChanged?.Invoke("Magnet çözülüyor...");
                    else if (state == TorrentState.Hashing)
                        StatusChanged?.Invoke("Torrent doğrulanıyor...");
                    else
                        StatusChanged?.Invoke($"Torrent indiriliyor... ({_manager.Peers.Available} eş)");

                    double progress = Math.Clamp(_manager.Progress, 0, 99.9);
                    ProgressChanged?.Invoke(progress);
                    SpeedAndTimeChanged?.Invoke(FormatRate(_manager.Monitor.DownloadRate), Eta(_manager));

                    await Task.Delay(500, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                try { await _manager.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }

                IsDownloading = false;
                IsPaused = false;
                CompletedSuccessfully = true;
                ProgressChanged?.Invoke(100);
                SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
                StatusChanged?.Invoke("İndirme Tamamlandı!");
                return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (!IsCancelled && _manager != null)
                    {
                        if (UseAppPolicy
                            && NetworkWatcher.IsTransient(ex)
                            && !NetworkWatcher.Shared.IsOnline)
                        {
                            if (!await NetworkWatcher.WaitForReconnectAsync(
                                    token,
                                    s => StatusChanged?.Invoke(s),
                                    (sp, t) => SpeedAndTimeChanged?.Invoke(sp, t)).ConfigureAwait(false))
                                throw;
                        }

                        StatusChanged?.Invoke("Torrent toparlanıyor...");
                        try
                        {
                            await RecoverFromErrorAsync(token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            await Task.Delay(800, token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                IsDownloading = false;
                CompletedSuccessfully = false;
                try { if (_manager != null) await _manager.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }

                if (IsCancelled)
                {
                    StatusChanged?.Invoke("İptal Edildi");
                    SpeedAndTimeChanged?.Invoke("", "00:00:00");
                    await TryRemoveAsync(RemoveMode.CacheDataAndDownloadedData).ConfigureAwait(false);
                    _manager = null;
                }
                else
                {
                    IsPaused = true;
                    StatusChanged?.Invoke("İndirme Duraklatıldı!");
                    SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
                }
            }
            catch (Exception ex)
            {
                IsDownloading = false;
                IsPaused = true;
                CompletedSuccessfully = false;
                StatusChanged?.Invoke(FormatFatalStatus(ex));
                SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
                System.Diagnostics.Debug.WriteLine(ex);
            }
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

        internal TorrentManager? ManagerForTests => _manager;

        internal async Task AddPeerForTestsAsync(Uri peer)
        {
            if (_manager == null)
                throw new InvalidOperationException("Torrent henüz eklenmedi.");
            await _manager.AddPeerAsync(new PeerInfo(peer)).ConfigureAwait(false);
        }

        private async Task<TorrentManager> CreateManagerAsync(string saveDir, CancellationToken token)
        {
            string src = _source.Trim().Trim('"');
            var engine = Engine;

            if (src.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                if (!MagnetLink.TryParse(src, out var magnet) || magnet == null)
                    throw new InvalidOperationException("Geçersiz magnet bağlantısı.");
                var existing = FindExisting(engine, magnet.InfoHashes);
                if (existing != null)
                    return existing;
                try
                {
                    return await engine.AddAsync(magnet, saveDir).ConfigureAwait(false);
                }
                catch
                {
                    return FindExisting(engine, magnet.InfoHashes)
                        ?? throw new InvalidOperationException("Torrent zaten ekli, yeniden bağlanılamadı.");
                }
            }

            byte[] torrentBytes = await ReadTorrentBytesAsync(src, token).ConfigureAwait(false);
            var torrent = await Torrent.LoadAsync(torrentBytes).ConfigureAwait(false);
            var reused = FindExisting(engine, torrent.InfoHashes);
            if (reused != null)
                return reused;
            try
            {
                return await engine.AddAsync(torrent, saveDir).ConfigureAwait(false);
            }
            catch
            {
                return FindExisting(engine, torrent.InfoHashes)
                    ?? throw new InvalidOperationException("Torrent zaten ekli, yeniden bağlanılamadı.");
            }
        }

        private static TorrentManager? FindExisting(ClientEngine engine, InfoHashes hashes)
        {
            foreach (var manager in engine.Torrents)
            {
                if (SameHash(manager.InfoHashes, hashes))
                    return manager;
            }
            return null;
        }

        private static bool SameHash(InfoHashes a, InfoHashes b)
        {
            if (a.V1 != null && b.V1 != null && a.V1.Equals(b.V1))
                return true;
            if (a.V2 != null && b.V2 != null && a.V2.Equals(b.V2))
                return true;
            return false;
        }

        private static async Task<byte[]> ReadTorrentBytesAsync(string src, CancellationToken token)
        {
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var client = TransferHttp.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, src);
                using var resp = await client.SendAsync(req, token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            }

            string path = src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(src).LocalPath
                : src;
            if (!File.Exists(path))
                throw new FileNotFoundException("Torrent dosyası bulunamadı.", path);
            return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        }

        private async Task ApplySequentialAsync()
        {
            if (_manager == null || _manager.Files.Count == 0)
                return;
            try
            {
                bool found = false;
                foreach (var file in _manager.Files)
                {
                    if (file.Priority == Priority.DoNotDownload)
                        continue;
                    double pct = file.BitField.PercentComplete;
                    var want = !found && pct < 99.9 ? Priority.Immediate : Priority.Low;
                    if (!found && pct < 99.9)
                        found = true;
                    if (file.Priority != want)
                        await _manager.SetFilePriorityAsync(file, want).ConfigureAwait(false);
                }
            }
            catch { /* ignore */ }
        }

        private static bool IsDownloadComplete(TorrentState state) =>
            state == TorrentState.Seeding;

        private async Task EnsureRunningAsync(CancellationToken token)
        {
            if (_manager == null)
                return;
            var state = _manager.State;
            if (state is TorrentState.Downloading
                or TorrentState.Seeding
                or TorrentState.Hashing
                or TorrentState.Metadata
                or TorrentState.Starting)
                return;

            if (state == TorrentState.Error)
            {
                await RecoverFromErrorAsync(token).ConfigureAwait(false);
                return;
            }

            await _manager.StartAsync().ConfigureAwait(false);
        }

        private async Task RecoverFromErrorAsync(CancellationToken token)
        {
            if (_manager == null)
                return;

            string detail = MapErrorReason(_manager.Error?.Reason.ToString());
            StatusChanged?.Invoke(string.IsNullOrEmpty(detail)
                ? "Torrent toparlanıyor..."
                : $"Torrent toparlanıyor ({detail})...");
            SpeedAndTimeChanged?.Invoke("0 MB/s", "--:--:--");
            System.Diagnostics.Debug.WriteLine(
                $"torrent-error reason={_manager.Error?.Reason} ex={_manager.Error?.Exception}");

            if (UseAppPolicy
                && NetworkWatcher.AutoReconnectEnabled
                && !NetworkWatcher.Shared.IsOnline)
            {
                StatusChanged?.Invoke("Ağ bekleniyor...");
                await NetworkWatcher.Shared.WaitUntilOnlineAsync(token).ConfigureAwait(false);
            }

            try { await _manager.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }

            for (int i = 0; i < 50 && _manager.State == TorrentState.Stopping; i++)
                await Task.Delay(100, token).ConfigureAwait(false);

            int delay = _recoverDelayMs;
            _recoverDelayMs = Math.Min(10_000, _recoverDelayMs * 2);
            await Task.Delay(delay, token).ConfigureAwait(false);

            await _manager.StartAsync().ConfigureAwait(false);
        }

        internal static string MapErrorReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "";
            return reason switch
            {
                "WriteFailure" => "yazma hatası",
                "ReadFailure" => "okuma hatası",
                "HashFailed" => "parça doğrulama",
                _ => reason
            };
        }

        internal static bool IsNetworkReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;
            return reason.Contains("Connection", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Tracker", StringComparison.OrdinalIgnoreCase);
        }

        internal static string FormatFatalStatus(Exception ex)
        {
            string msg = ex.Message;
            if (string.IsNullOrWhiteSpace(msg))
                return "Torrent hatası — kaldığı yerden devam edilebilir";
            if (msg.Length > 80)
                msg = msg[..80] + "…";
            return $"Torrent hatası — {msg}";
        }

        private static long DownloadedBytes(TorrentManager manager)
        {
            try { return manager.Monitor.DataBytesReceived; }
            catch { return Math.Max(0, (long)(manager.Torrent?.Size * (manager.Progress / 100.0) ?? 0)); }
        }

        private static long UploadedBytes(TorrentManager manager)
        {
            try { return manager.Monitor.DataBytesSent; }
            catch { return 0; }
        }

        private async Task TryRemoveAsync(RemoveMode mode)
        {
            if (_manager == null)
                return;
            try
            {
                await Engine.RemoveAsync(_manager, mode).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }

        private static string FormatRate(long bytesPerSecond)
        {
            double mb = bytesPerSecond / (1024.0 * 1024.0);
            return $"{mb:F2} MB/s";
        }

        private static string Eta(TorrentManager manager)
        {
            long rate = manager.Monitor.DownloadRate;
            if (rate <= 0 || manager.Torrent == null)
                return "Hesaplanıyor...";
            long remaining = Math.Max(0, manager.Torrent.Size - (long)(manager.Torrent.Size * (manager.Progress / 100.0)));
            var t = TimeSpan.FromSeconds(remaining / (double)rate);
            if (t.TotalHours > 99)
                return "--:--:--";
            return t.ToString(@"hh\:mm\:ss");
        }
    }
}
