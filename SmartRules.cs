using System.IO;
using System.Text.RegularExpressions;

namespace DownloadMuck
{
    public static class SmartRules
    {
        public static string ApplyRename(string fileName, string? url, DateTime now, string? pattern)
        {
            string raw = string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
            string ext = Path.GetExtension(raw);
            string name = Path.GetFileNameWithoutExtension(raw);
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Trim() is "{name}{ext}" or "{orig}")
                return raw;

            string host = "";
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                host = SanitizeToken(uri.Host);

            string result = pattern
                .Replace("{orig}", raw, StringComparison.OrdinalIgnoreCase)
                .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
                .Replace("{ext}", ext, StringComparison.OrdinalIgnoreCase)
                .Replace("{date}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
                .Replace("{time}", now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase)
                .Replace("{host}", host, StringComparison.OrdinalIgnoreCase);

            foreach (char c in Path.GetInvalidFileNameChars())
                result = result.Replace(c, '_');
            result = result.Trim();
            return string.IsNullOrWhiteSpace(result) ? raw : result;
        }

        public static bool ShouldSkip(
            string url,
            string fileName,
            AppSettings settings,
            IEnumerable<string> existingUrls,
            out string reason,
            long? fileSizeBytes = null,
            string? mimeType = null)
        {
            reason = "";
            string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            foreach (string skip in SplitList(settings.SkipExtensions))
            {
                if (ext.Equals(skip, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $".{ext} uzantısı kurallarda atlanıyor.";
                    return true;
                }
            }

            foreach (string needle in SplitList(settings.SkipUrlContains))
            {
                if (url.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "URL kural süzgecine takıldı.";
                    return true;
                }
            }

            if (HostMatches(url, settings.SkipDomains))
            {
                reason = "Alan adı kurallarda atlanıyor.";
                return true;
            }

            if (RegexMatches(url, settings.SkipUrlRegex))
            {
                reason = "URL regex kuralına takıldı.";
                return true;
            }

            string mime = string.IsNullOrWhiteSpace(mimeType) ? GuessMime(fileName) : mimeType;
            foreach (string needle in SplitList(settings.SkipMimeContains))
            {
                if (mime.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "MIME türü kurallarda atlanıyor.";
                    return true;
                }
            }

            if (fileSizeBytes is > 0)
            {
                double mb = fileSizeBytes.Value / (1024.0 * 1024.0);
                if (settings.SkipMinSizeMb > 0 && mb < settings.SkipMinSizeMb)
                {
                    reason = $"Dosya {settings.SkipMinSizeMb} MB altı atlanıyor.";
                    return true;
                }
                if (settings.SkipMaxSizeMb > 0 && mb > settings.SkipMaxSizeMb)
                {
                    reason = $"Dosya {settings.SkipMaxSizeMb} MB üstü atlanıyor.";
                    return true;
                }
            }

            if (settings.SkipDuplicateUrls)
            {
                string key = NormalizeUrl(url);
                foreach (string other in existingUrls)
                {
                    if (string.Equals(NormalizeUrl(other), key, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "Aynı adres zaten listede.";
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool HostMatches(string? url, string? domains)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;
            string host = uri.Host.Trim().Trim('.');
            if (string.IsNullOrEmpty(host))
                return false;
            foreach (string raw in SplitList(domains))
            {
                string d = raw.Trim().Trim('.');
                if (d.Length == 0)
                    continue;
                if (host.Equals(d, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static bool RegexMatches(string? url, string? pattern)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(pattern))
                return false;
            try
            {
                return Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
            }
            catch
            {
                return false;
            }
        }

        public static string GuessMime(string? fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext switch
            {
                ".zip" => "application/zip",
                ".rar" => "application/vnd.rar",
                ".7z" => "application/x-7z-compressed",
                ".exe" or ".msi" or ".bat" or ".scr" => "application/x-msdownload",
                ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" => "video/mp4",
                ".mp3" or ".flac" or ".wav" or ".ogg" => "audio/mpeg",
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" => "image/" + ext.TrimStart('.'),
                ".pdf" => "application/pdf",
                ".torrent" => "application/x-bittorrent",
                _ => "application/octet-stream"
            };
        }

        public static IReadOnlyList<string> SplitList(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();
            return raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.TrimStart('.'))
                .Where(s => s.Length > 0)
                .ToArray();
        }

        public static string NormalizeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "";
            string t = url.Trim();
            int hash = t.IndexOf('#');
            if (hash >= 0)
                t = t[..hash];
            if (t.EndsWith('/'))
                t = t.TrimEnd('/');
            return t;
        }

        private static string SanitizeToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            return Regex.Replace(text, @"[^\w.-]+", "_");
        }
    }
}
