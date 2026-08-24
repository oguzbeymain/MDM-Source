using System.IO;
using System.Net.Http;
using MonoTorrent;

namespace DownloadMuck
{
    public sealed record TorrentInfoPreview(string Name, long Size, int FileCount);

    public static class TorrentPeek
    {
        private const int MaxTorrentBytes = 4 * 1024 * 1024;

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

                byte[]? bytes = await ReadTorrentBytesAsync(src, token).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0)
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

        private static async Task<byte[]?> ReadTorrentBytesAsync(string src, CancellationToken token)
        {
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var client = TransferHttp.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, src);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                long? len = resp.Content.Headers.ContentLength;
                if (len > MaxTorrentBytes)
                    return null;
                byte[] data = await resp.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                return data.Length > MaxTorrentBytes ? null : data;
            }

            string path = src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? new Uri(src).LocalPath
                : src;
            if (!File.Exists(path))
                return null;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxTorrentBytes)
                return null;
            return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        }
    }
}
