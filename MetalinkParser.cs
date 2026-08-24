using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace DownloadMuck
{
    public sealed class MetalinkFile
    {
        public string FileName { get; init; } = "download";
        public long Size { get; init; }
        public IReadOnlyList<string> Urls { get; init; } = Array.Empty<string>();
        public string? HashType { get; init; }
        public string? HashValue { get; init; }
    }

    public static class MetalinkParser
    {
        public static MetalinkFile? Parse(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return null;

            try
            {
                var doc = XDocument.Parse(xml);
                XNamespace ns4 = "urn:ietf:params:xml:ns:metalink";
                XNamespace ns3 = "http://www.metalinker.org/";

                var file = doc.Root?.Element(ns4 + "file")
                    ?? doc.Descendants(ns4 + "file").FirstOrDefault()
                    ?? doc.Descendants(ns3 + "file").FirstOrDefault()
                    ?? doc.Descendants("file").FirstOrDefault();
                if (file == null)
                    return null;

                string name = (string?)file.Attribute("name")
                    ?? file.Element(ns4 + "name")?.Value
                    ?? file.Element("name")?.Value
                    ?? "download";
                name = Path.GetFileName(name.Trim());
                if (string.IsNullOrWhiteSpace(name))
                    name = "download";

                long size = 0;
                string? sizeText = file.Element(ns4 + "size")?.Value
                    ?? file.Element(ns3 + "size")?.Value
                    ?? file.Element("size")?.Value;
                _ = long.TryParse(sizeText, out size);

                var urls = new List<string>();
                foreach (var el in file.Descendants())
                {
                    if (!el.Name.LocalName.Equals("url", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string href = (el.Value ?? "").Trim();
                    if (UrlClassifier.Classify(href) == TransferKind.Http)
                        urls.Add(href);
                }

                urls = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (urls.Count == 0)
                    return null;

                string? hashType = null;
                string? hashValue = null;
                foreach (var el in file.Descendants())
                {
                    if (!el.Name.LocalName.Equals("hash", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string value = (el.Value ?? "").Trim();
                    if (value.Length == 0)
                        continue;
                    string type = (string?)el.Attribute("type") ?? "sha-256";
                    hashType = type;
                    hashValue = value;
                    if (ChecksumUtil.NormalizeType(type) == "sha256")
                        break;
                }

                return new MetalinkFile
                {
                    FileName = name,
                    Size = size,
                    Urls = urls,
                    HashType = hashType,
                    HashValue = hashValue
                };
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class MetalinkTransferBackend : ITransferBackend
    {
        private readonly string _metalinkUrl;
        private readonly string _savePath;
        private readonly int _threadCount;
        private ITransferBackend? _inner;
        private MetalinkFile? _parsed;

        public MetalinkTransferBackend(string metalinkUrl, string savePath, int threadCount)
        {
            _metalinkUrl = metalinkUrl;
            _savePath = savePath;
            _threadCount = threadCount;
        }

        public bool IsPaused => _inner?.IsPaused ?? false;
        public bool IsDownloading => _inner?.IsDownloading ?? false;
        public bool IsCancelled => _inner?.IsCancelled ?? false;
        public bool CompletedSuccessfully => _inner?.CompletedSuccessfully ?? false;

        public event Action<double>? ProgressChanged;
        public event Action<string>? StatusChanged;
        public event Action<string, string>? SpeedAndTimeChanged;
        public event Action<long>? TotalSizeKnown;
#pragma warning disable CS0067
        public event Action<string, string>? OutputResolved;
#pragma warning restore CS0067

        public async Task StartOrResumeDownloadAsync()
        {
            if (_inner == null)
            {
                StatusChanged?.Invoke("Metalink çözülüyor...");
                using var client = TransferHttp.CreateClient();
                string xml = await client.GetStringAsync(_metalinkUrl);
                var parsed = MetalinkParser.Parse(xml)
                    ?? throw new InvalidOperationException("Metalink içinde HTTP kaynağı yok.");
                _parsed = parsed;

                _inner = new DownloadEngine(parsed.Urls, _savePath, _threadCount);
                _inner.ProgressChanged += v => ProgressChanged?.Invoke(v);
                _inner.StatusChanged += v => StatusChanged?.Invoke(v);
                _inner.SpeedAndTimeChanged += (s, t) => SpeedAndTimeChanged?.Invoke(s, t);
                _inner.TotalSizeKnown += v => TotalSizeKnown?.Invoke(v);
            }

            await _inner.StartOrResumeDownloadAsync();
            if (_inner.CompletedSuccessfully
                && _parsed != null
                && !ChecksumUtil.TryVerifyFile(_savePath, _parsed.HashType, _parsed.HashValue))
            {
                throw new InvalidOperationException("Metalink özeti eşleşmedi — dosya bozulmuş olabilir.");
            }
        }

        public void Pause() => _inner?.Pause();
        public void Cancel() => _inner?.Cancel();
    }
}
