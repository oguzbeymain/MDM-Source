using System.IO;
using System.Net;
using System.Net.Http;
using MonoTorrent;

namespace MDM
{
    public sealed record TorrentInfoPreview(string Name, long Size, int FileCount);

    public static class TorrentPeek
    {
        private const int MaxTorrentBytes = 32 * 1024 * 1024;

        public static async Task<TorrentInfoPreview?> TryDescribeAsync(string source, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(source))
                return null;

            string src = source.Trim().Trim('"');
            try
            {
                if (src.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    string magnetName = FileNameHelper.TryFileNameFromUrl(src) ?? "torrent";
                    return new TorrentInfoPreview(magnetName, 0, 0);
                }

                byte[] bytes = await ReadTorrentFileBytesAsync(src, token).ConfigureAwait(false);
                if (bytes.Length == 0)
                    return null;

                var torrent = await Torrent.LoadAsync(bytes).ConfigureAwait(false);
                string torrentName = string.IsNullOrWhiteSpace(torrent.Name) ? "torrent" : torrent.Name;
                return new TorrentInfoPreview(torrentName, torrent.Size, torrent.Files.Count);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>HTTP/dosyadan .torrent gövdesini okur; HTML sayfa gelirse hata verir.</summary>
        public static async Task<byte[]> ReadTorrentFileBytesAsync(
            string src,
            CancellationToken token,
            string? cookies = null,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            if (string.IsNullOrWhiteSpace(src))
                throw new InvalidDataException("Torrent adresi boş.");

            src = src.Trim().Trim('"');
            if (src.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Magnet bağlantısından .torrent dosyası kaydedilemez.");

            byte[] data;
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                CookieContainer? jar = null;
                if (!string.IsNullOrWhiteSpace(cookies) && Uri.TryCreate(src, UriKind.Absolute, out Uri? uri))
                {
                    jar = new CookieContainer();
                    try { jar.SetCookies(uri, cookies); } catch { /* ignore */ }
                }

                using var client = TransferHttp.CreateClient(jar);
                using var req = new HttpRequestMessage(HttpMethod.Get, src);
                if (headers != null)
                {
                    foreach (var kv in headers)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                            continue;
                        req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                }

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                long? len = resp.Content.Headers.ContentLength;
                if (len > MaxTorrentBytes)
                    throw new InvalidDataException("Torrent dosyası beklenenden büyük.");
                data = await resp.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            }
            else
            {
                string path = src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(src).LocalPath
                    : src;
                if (!File.Exists(path))
                    throw new FileNotFoundException("Torrent dosyası bulunamadı.", path);
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaxTorrentBytes)
                    throw new InvalidDataException("Torrent dosyası geçersiz veya çok büyük.");
                data = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            }

            if (data.Length == 0)
                throw new InvalidDataException("Torrent dosyası boş.");
            if (data.Length > MaxTorrentBytes)
                throw new InvalidDataException("Torrent dosyası beklenenden büyük.");
            if (LooksLikeWebPage(data))
                throw new InvalidDataException("Sunucu torrent dosyası yerine bir web sayfası döndürdü.");
            return data;
        }

        private static bool LooksLikeWebPage(byte[] data)
        {
            int i = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                i = 3;
            while (i < data.Length && (data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                i++;
            if (i >= data.Length)
                return false;
            byte b = data[i];
            return b is (byte)'<' or (byte)'{';
        }
    }
}
