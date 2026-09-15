using System.IO;
using System.Text.RegularExpressions;

namespace MDM
{
    public enum TransferKind
    {
        Unknown,
        Http,
        Ftp,
        Sftp,
        Magnet,
        Torrent,
        Metalink
    }

    /// <summary>
        /// Ingress classifier — HTTP, FTP, SFTP, metalink, torrent and magnet.
    /// </summary>
    public static class UrlClassifier
    {
        private static readonly Regex HttpUrl = new(
            @"https?://[^\s<>""']+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HrefOrSrc = new(
            @"(?:href|src)\s*=\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static TransferKind Classify(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return TransferKind.Unknown;

            string t = raw.Trim().Trim('"');
            if (t.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                return TransferKind.Magnet;
            if (t.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try { return Classify(new Uri(t).LocalPath); }
                catch { return TransferKind.Unknown; }
            }
            if (t.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
                return TransferKind.Sftp;
            if (t.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase))
                return TransferKind.Ftp;

            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(t, UriKind.Absolute, out var uri))
                {
                    string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                    if (ext is ".metalink" or ".meta4")
                        return TransferKind.Metalink;
                    if (ext == ".torrent")
                        return TransferKind.Torrent;
                }
                return TransferKind.Http;
            }

            if (t.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                return TransferKind.Torrent;
            if (t.EndsWith(".metalink", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith(".meta4", StringComparison.OrdinalIgnoreCase))
                return TransferKind.Metalink;

            return TransferKind.Unknown;
        }

        public static bool CanDownloadNow(TransferKind kind) =>
            kind is TransferKind.Http or TransferKind.Ftp or TransferKind.Sftp
                or TransferKind.Metalink or TransferKind.Magnet or TransferKind.Torrent;

        public static IReadOnlyList<string> ExtractDownloadUrls(string? text)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
                return list;

            string trimmed = text.Trim().Trim('"');
            if (CanDownloadNow(Classify(trimmed)) && seen.Add(trimmed))
                list.Add(trimmed);

            foreach (Match m in Regex.Matches(text, @"magnet:\?[^\s<>""']+", RegexOptions.IgnoreCase))
            {
                string url = m.Value.TrimEnd('.', ',', ';', ')', ']');
                if (seen.Add(url))
                    list.Add(url);
            }

            foreach (Match m in Regex.Matches(text, @"(?:https?|ftp|ftps|sftp)://[^\s<>""']+", RegexOptions.IgnoreCase))
            {
                string url = m.Value.TrimEnd('.', ',', ';', ')', ']');
                var kind = Classify(url);
                if (!CanDownloadNow(kind))
                    continue;
                if (seen.Add(url))
                    list.Add(url);
            }

            foreach (string http in ExtractHttpUrls(text))
            {
                if (seen.Add(http))
                    list.Add(http);
            }
            return list;
        }

        public static string UnsupportedMessage(TransferKind kind) => kind switch
        {
            TransferKind.Magnet or TransferKind.Torrent =>
                "Geçerli bir torrent dosyası veya magnet bağlantısı girin.",
            _ => "Geçerli bir HTTP(S), FTP, SFTP, metalink, torrent veya magnet bağlantısı girin."
        };

        public static IReadOnlyList<string> ExtractHttpUrls(string? text, string? baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            Uri? basis = null;
            if (!string.IsNullOrWhiteSpace(baseUrl))
                Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out basis);

            void TryAdd(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return;
                string candidate = raw.Trim().TrimEnd('.', ',', ';', ')', ']', '"', '\'');
                if (candidate.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith("#"))
                    return;

                if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (basis == null || !Uri.TryCreate(basis, candidate, out Uri? abs))
                        return;
                    candidate = abs.ToString();
                }

                if (Classify(candidate) != TransferKind.Http)
                    return;
                if (seen.Add(candidate))
                    list.Add(candidate);
            }

            foreach (Match m in HttpUrl.Matches(text))
                TryAdd(m.Value);
            foreach (Match m in HrefOrSrc.Matches(text))
                TryAdd(m.Groups[1].Value);

            return list;
        }
    }
}
