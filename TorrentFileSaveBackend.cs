using System.IO;
using System.Net.Http;
using MonoTorrent;

namespace MDM
{
    /// <summary>
    /// Yalnızca .torrent meta dosyasını kaydeder; içeriği P2P ile indirmez.
    /// DownloadEngine HTML/range yolunu kullanmaz — tracker PHP sayfaları da bencode ise geçer.
    /// </summary>
    public sealed class TorrentFileSaveBackend : ITransferBackend
    {
        private readonly string _source;
        private readonly string _savePath;
        private readonly string _cookies;
        private readonly IReadOnlyDictionary<string, string>? _headers;
        private CancellationTokenSource? _cts;

        public TorrentFileSaveBackend(
            string source,
            string savePath,
            string? cookies = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _savePath = savePath ?? throw new ArgumentNullException(nameof(savePath));
            _cookies = cookies ?? "";
            _headers = headers;
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
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            IsDownloading = true;
            IsPaused = false;
            IsCancelled = false;
            CompletedSuccessfully = false;

            try
            {
                StatusChanged?.Invoke("İndiriliyor");
                SpeedAndTimeChanged?.Invoke("—", "--:--:--");
                ProgressChanged?.Invoke(5);

                byte[] bytes = await TorrentPeek.ReadTorrentFileBytesAsync(
                    _source, token, _cookies, _headers).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                ProgressChanged?.Invoke(55);
                await Torrent.LoadAsync(bytes).ConfigureAwait(false);

                TotalSizeKnown?.Invoke(bytes.Length);
                string dir = Path.GetDirectoryName(_savePath) ?? ".";
                Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(_savePath, bytes, token).ConfigureAwait(false);

                OutputResolved?.Invoke(_savePath, Path.GetFileName(_savePath));
                ProgressChanged?.Invoke(100);
                SpeedAndTimeChanged?.Invoke("0 MB/s", "00:00:00");
                StatusChanged?.Invoke("Tamamlandı");
                CompletedSuccessfully = true;
            }
            catch (OperationCanceledException)
            {
                CompletedSuccessfully = false;
                if (IsCancelled)
                    StatusChanged?.Invoke("İptal Edildi");
            }
            catch (Exception ex)
            {
                CompletedSuccessfully = false;
                StatusChanged?.Invoke("Hata: " + Describe(ex));
                throw;
            }
            finally
            {
                IsDownloading = false;
            }
        }

        public void Pause()
        {
            IsPaused = true;
            _cts?.Cancel();
        }

        public void Cancel()
        {
            IsCancelled = true;
            IsPaused = false;
            _cts?.Cancel();
            try
            {
                if (File.Exists(_savePath))
                    File.Delete(_savePath);
            }
            catch { /* ignore */ }
        }

        private static string Describe(Exception ex)
        {
            if (ex is HttpRequestException http)
                return string.IsNullOrWhiteSpace(http.Message) ? "Torrent dosyası alınamadı." : http.Message;
            if (ex is InvalidDataException)
                return ex.Message;
            if (ex is FileNotFoundException)
                return "Torrent dosyası bulunamadı.";
            return "Geçerli bir torrent dosyası değil veya sunucu dosya döndürmedi.";
        }
    }
}
