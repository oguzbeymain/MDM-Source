using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MDM
{
    public enum ScanKind
    {
        Image,
        Video,
        Audio,
        Archive,
        Document,
        App,
        Other
    }

    /// <summary>Sayfa taramasında bulunan tek bir dosya adayı.</summary>
    public sealed class ScanItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private long _sizeBytes;
        private ImageSource? _thumbnail;

        public string Url { get; init; } = "";
        public string FileName { get; init; } = "";
        public string Extension { get; init; } = "";
        public ScanKind Kind { get; init; }
        public string Host { get; init; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                Raise(nameof(IsSelected));
            }
        }

        /// <summary>0 = henüz bilinmiyor (HEAD yanıtı beklenir veya sunucu boyut vermiyor).</summary>
        public long SizeBytes
        {
            get => _sizeBytes;
            set
            {
                if (_sizeBytes == value) return;
                _sizeBytes = value;
                Raise(nameof(SizeBytes));
                Raise(nameof(SizeDisplay));
            }
        }

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value)) return;
                _thumbnail = value;
                Raise(nameof(Thumbnail));
                Raise(nameof(HasThumbnail));
            }
        }

        public bool HasThumbnail => _thumbnail != null;

        public string SizeDisplay => _sizeBytes > 0 ? MainWindow.FormatFileSize(_sizeBytes) : "—";

        public string ExtDisplay => string.IsNullOrEmpty(Extension) ? "?" : Extension.ToUpperInvariant();

        public string GroupName => PageScanService.GroupLabel(Kind);

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Dil değişince grup adı ve etiketler yenilenir.</summary>
        public void RefreshLabels()
        {
            Raise(nameof(GroupName));
            Raise(nameof(SizeDisplay));
        }

        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Sayfa taraması: HTML'den görsel/video/ses/arşiv/doküman adaylarını çıkarır,
    /// tür + kategoriye göre sınıflandırır, boyut ve önizleme bilgisini sonradan doldurur.
    /// </summary>
    public static class PageScanService
    {
        private const int MaxItems = 600;
        private const int MaxThumbnails = 90;
        private const int MaxThumbnailBytes = 4 * 1024 * 1024;

        private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        {
            "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "avif", "tif", "tiff", "ico", "heic", "heif", "jfif"
        };

        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        {
            "mp4", "mkv", "webm", "avi", "mov", "flv", "m4v", "wmv", "mpg", "mpeg", "3gp", "ts", "m3u8", "mpd", "ogv"
        };

        private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        {
            "mp3", "flac", "wav", "m4a", "aac", "ogg", "oga", "opus", "wma", "aiff", "mid", "midi"
        };

        private static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase)
        {
            "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab", "tgz", "torrent"
        };

        private static readonly HashSet<string> DocumentExt = new(StringComparer.OrdinalIgnoreCase)
        {
            "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp",
            "epub", "mobi", "csv", "json", "xml", "srt", "vtt"
        };

        private static readonly HashSet<string> AppExt = new(StringComparer.OrdinalIgnoreCase)
        {
            "exe", "msi", "apk", "dmg", "appx", "msix", "deb", "rpm", "pkg", "jar", "bin", "img"
        };

        private static readonly Regex SrcSetRx = new(
            """srcset\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LazyAttrRx = new(
            """(?:data-src|data-original|data-lazy|data-lazy-src|data-url|data-href|data-full|poster)\s*=\s*["']([^"']+)["']""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CssUrlRx = new(
            """url\(\s*['"]?([^)'"]+?)['"]?\s*\)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static ScanKind KindOf(string? extension)
        {
            string ext = (extension ?? "").TrimStart('.');
            if (ext.Length == 0) return ScanKind.Other;
            if (ImageExt.Contains(ext)) return ScanKind.Image;
            if (VideoExt.Contains(ext)) return ScanKind.Video;
            if (AudioExt.Contains(ext)) return ScanKind.Audio;
            if (ArchiveExt.Contains(ext)) return ScanKind.Archive;
            if (DocumentExt.Contains(ext)) return ScanKind.Document;
            if (AppExt.Contains(ext)) return ScanKind.App;
            return ScanKind.Other;
        }

        public static bool IsKnownExtension(string? extension)
            => KindOf(extension) != ScanKind.Other;

        /// <summary>URL yolundan (gerekirse sorgudan) uzantı çıkarır.</summary>
        public static string ExtensionOfUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? ExtensionOf(uri) : "";
        }

        public static string GroupLabel(ScanKind kind) => kind switch
        {
            ScanKind.Image => Loc.T("cat.images", "Resimler"),
            ScanKind.Video => Loc.T("cat.videos", "Videolar"),
            ScanKind.Audio => Loc.T("cat.audio", "Sesler"),
            ScanKind.Archive => Loc.T("cat.archives", "Arşivler"),
            ScanKind.Document => Loc.T("cat.documents", "Dökümanlar"),
            ScanKind.App => Loc.T("cat.apps", "Uygulamalar"),
            _ => Loc.T("scan.kind.other", "Diğer")
        };

        /// <summary>URL'yi tür + ada çevirir; tanınmayan uzantılar atlanır.</summary>
        public static ScanItem? TryCreateItem(string? url, ScanKind? forcedKind = null, long sizeBytes = 0)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

            string ext = ExtensionOf(uri);
            var kind = forcedKind ?? KindOf(ext);
            if (forcedKind == null && kind == ScanKind.Other)
                return null;

            return new ScanItem
            {
                Url = uri.ToString(),
                FileName = FileNameOf(uri, ext),
                Extension = ext.ToLowerInvariant(),
                Kind = kind,
                Host = uri.Host,
                SizeBytes = sizeBytes > 0 ? sizeBytes : 0,
                IsSelected = false
            };
        }

        private static string ExtensionOf(Uri uri)
        {
            string ext = Path.GetExtension(uri.AbsolutePath).TrimStart('.');
            if (ext.Length is > 0 and <= 5)
                return ext;

            // CDN'ler uzantıyı sorguda taşıyabilir: ?format=jpg / &ext=png
            var m = Regex.Match(uri.Query, @"(?:format|ext|type)=([a-z0-9]{2,5})", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string FileNameOf(Uri uri, string ext)
        {
            string raw = Path.GetFileName(uri.AbsolutePath);
            try { raw = Uri.UnescapeDataString(raw); }
            catch { /* ham ad kalsın */ }

            raw = raw.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw == "." || !raw.Contains('.'))
            {
                string stem = string.IsNullOrWhiteSpace(raw) ? uri.Host.Replace("www.", "") : raw;
                raw = ext.Length > 0 ? $"{stem}.{ext}" : stem;
            }

            foreach (char c in Path.GetInvalidFileNameChars())
                raw = raw.Replace(c, '_');
            return raw.Length > 140 ? raw[^140..] : raw;
        }

        /// <summary>Eklentiden gelen aday listesini ScanItem'a çevirir.</summary>
        public static IReadOnlyList<ScanItem> FromCandidates(IEnumerable<ScanCandidate> candidates)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<ScanItem>();
            foreach (var c in candidates)
            {
                if (list.Count >= MaxItems) break;
                var forced = KindFromHint(c.Kind);
                var item = TryCreateItem(c.Url, forced, c.SizeBytes);
                if (item == null || !seen.Add(item.Url)) continue;
                list.Add(item);
            }
            return Sort(list);
        }

        private static ScanKind? KindFromHint(string? hint) => (hint ?? "").ToLowerInvariant() switch
        {
            "image" or "img" => ScanKind.Image,
            "video" => ScanKind.Video,
            "audio" => ScanKind.Audio,
            _ => null
        };

        /// <summary>Sayfayı (ve ayarlı derinlikte alt sayfaları) tarayıp aday dosyaları döndürür.</summary>
        public static async Task<IReadOnlyList<ScanItem>> ScanPageAsync(
            string pageUrl, int maxDepth, int maxPages, IProgress<int>? progress, CancellationToken token)
        {
            maxDepth = Math.Clamp(maxDepth, 0, 3);
            maxPages = Math.Clamp(maxPages, 1, 40);

            var items = new List<ScanItem>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Url, int Depth)>();
            queue.Enqueue((pageUrl, 0));

            using var client = TransferHttp.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);

            while (queue.Count > 0 && seenPages.Count < maxPages && items.Count < MaxItems && !token.IsCancellationRequested)
            {
                var (url, depth) = queue.Dequeue();
                if (!seenPages.Add(url))
                    continue;

                string html;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                    using var resp = await client.SendAsync(req, token);
                    if (!resp.IsSuccessStatusCode)
                        continue;
                    html = await resp.Content.ReadAsStringAsync(token);
                }
                catch
                {
                    continue;
                }

                foreach (string candidate in ExtractCandidates(html, url))
                {
                    var item = TryCreateItem(candidate);
                    if (item != null)
                    {
                        if (seenUrls.Add(item.Url) && items.Count < MaxItems)
                        {
                            items.Add(item);
                            progress?.Report(items.Count);
                        }
                    }
                    else if (depth < maxDepth && SameSite(pageUrl, candidate))
                    {
                        queue.Enqueue((candidate, depth + 1));
                    }
                }

                foreach (string video in VideoProbe.ExtractFromHtml(html, url))
                {
                    var item = TryCreateItem(video, ScanKind.Video);
                    if (item == null || !seenUrls.Add(item.Url) || items.Count >= MaxItems) continue;
                    items.Add(item);
                    progress?.Report(items.Count);
                }
            }

            return Sort(items);
        }

        private static IReadOnlyList<ScanItem> Sort(List<ScanItem> items)
        {
            items.Sort((a, b) =>
            {
                int k = a.Kind.CompareTo(b.Kind);
                return k != 0 ? k : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });
            return items;
        }

        private static bool SameSite(string pageUrl, string candidate)
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var a)) return false;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var b)) return false;
            return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ExtractCandidates(string html, string baseUrl)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var basis);

            IEnumerable<string> Resolve(string raw)
            {
                string v = (raw ?? "").Trim();
                if (v.Length == 0
                    || v.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || v.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                    || v.StartsWith("#"))
                    yield break;

                if (!v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (basis == null || !Uri.TryCreate(basis, v, out Uri? abs))
                        yield break;
                    v = abs.ToString();
                }

                if (seen.Add(v))
                    yield return v;
            }

            foreach (string u in UrlClassifier.ExtractHttpUrls(html, baseUrl))
                foreach (string r in Resolve(u))
                    yield return r;

            foreach (Match m in LazyAttrRx.Matches(html))
                foreach (string r in Resolve(m.Groups[1].Value))
                    yield return r;

            foreach (Match m in SrcSetRx.Matches(html))
            {
                foreach (string part in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string first = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    foreach (string r in Resolve(first))
                        yield return r;
                }
            }

            foreach (Match m in CssUrlRx.Matches(html))
                foreach (string r in Resolve(m.Groups[1].Value))
                    yield return r;
        }

        /// <summary>Boyutu bilinmeyen adaylar için HEAD isteğiyle Content-Length toplar.</summary>
        public static async Task FillSizesAsync(
            IReadOnlyList<ScanItem> items, Action<ScanItem, long> apply, CancellationToken token)
        {
            var pending = items.Where(i => i.SizeBytes <= 0).Take(200).ToList();
            if (pending.Count == 0) return;

            using var client = TransferHttp.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            using var gate = new SemaphoreSlim(6);

            var tasks = pending.Select(async item =>
            {
                try
                {
                    await gate.WaitAsync(token);
                    try
                    {
                        long size = await HeadSizeAsync(client, item.Url, token);
                        if (size > 0)
                            apply(item, size);
                    }
                    finally { gate.Release(); }
                }
                catch { /* boyut isteğe bağlı */ }
            });

            try { await Task.WhenAll(tasks); }
            catch { /* iptal */ }
        }

        private static async Task<long> HeadSizeAsync(HttpClient client, string url, CancellationToken token)
        {
            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token);
                if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is > 0)
                    return resp.Content.Headers.ContentLength.Value;
            }
            catch { /* HEAD desteklenmiyor olabilir */ }

            try
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, url);
                get.Headers.Range = new RangeHeaderValue(0, 0);
                using var resp = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, token);
                if (resp.Content.Headers.ContentRange?.Length is > 0)
                    return resp.Content.Headers.ContentRange.Length!.Value;
            }
            catch { /* boyut yok */ }

            return 0;
        }

        /// <summary>Görsel adayları için küçük önizleme indirir (bant genişliği için sınırlı).</summary>
        public static async Task LoadThumbnailsAsync(
            IReadOnlyList<ScanItem> items, string referrer, Action<ScanItem, ImageSource> apply, CancellationToken token)
        {
            var images = items.Where(i => i.Kind == ScanKind.Image && !i.HasThumbnail).Take(MaxThumbnails).ToList();
            if (images.Count == 0) return;

            using var client = TransferHttp.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            using var gate = new SemaphoreSlim(4);

            var tasks = images.Select(async item =>
            {
                try
                {
                    await gate.WaitAsync(token);
                    try
                    {
                        var bmp = await DownloadThumbnailAsync(client, item.Url, referrer, token);
                        if (bmp != null)
                            apply(item, bmp);
                    }
                    finally { gate.Release(); }
                }
                catch { /* önizleme isteğe bağlı */ }
            });

            try { await Task.WhenAll(tasks); }
            catch { /* iptal */ }
        }

        private static async Task<ImageSource?> DownloadThumbnailAsync(
            HttpClient client, string url, string referrer, CancellationToken token)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(referrer) && Uri.TryCreate(referrer, UriKind.Absolute, out var refUri))
                    req.Headers.Referrer = refUri;
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                if (!resp.IsSuccessStatusCode) return null;
                if (resp.Content.Headers.ContentLength is > MaxThumbnailBytes) return null;

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(token);
                if (bytes.Length == 0 || bytes.Length > MaxThumbnailBytes) return null;

                var bmp = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = 96;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Eklentinin sayfa taramasından gelen ham aday.</summary>
    public sealed class ScanCandidate
    {
        public string Url { get; set; } = "";
        public string Kind { get; set; } = "";
        public long SizeBytes { get; set; }
    }

    /// <summary>/ext/scan gövdesi.</summary>
    public sealed class ScanRequest
    {
        public string PageUrl { get; set; } = "";
        public string Title { get; set; } = "";
        public List<ScanCandidate> Items { get; } = new();
    }
}
