using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace DownloadMuck
{
    public static class FileNameHelper
    {
        public static string DecodeDisplayName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            string name = raw.Trim().Trim('"');

            // RFC 5987: filename*=UTF-8''...
            Match star = Regex.Match(name, @"^[^']*''(.+)$");
            if (star.Success)
            {
                try { name = Uri.UnescapeDataString(star.Groups[1].Value.Replace('+', ' ')); }
                catch { name = star.Groups[1].Value; }
            }
            else
            {
                try { name = Uri.UnescapeDataString(name.Replace("+", "%20")); }
                catch { /* keep */ }
            }

            name = RepairMojibake(name);

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name.Trim();
        }

        public static string RepairMojibake(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!text.Contains('Ã') && !text.Contains('Â')) return text;

            try
            {
                byte[] bytes = Encoding.Latin1.GetBytes(text);
                string utf8 = Encoding.UTF8.GetString(bytes);
                if (utf8.Count(c => c == '\uFFFD') == 0 && LooksMoreTurkish(utf8, text))
                    return utf8;
            }
            catch { /* ignore */ }

            return text;
        }

        private static bool LooksMoreTurkish(string candidate, string original)
        {
            const string tr = "çğıöşüÇĞİÖŞÜ";
            int score(string s) => s.Count(c => tr.Contains(c));
            return score(candidate) >= score(original) && !candidate.Contains('Ã');
        }

        public static string? ExtractFromContentDisposition(HttpContentHeaders headers)
        {
            if (headers.ContentDisposition?.FileNameStar != null)
            {
                string star = DecodeDisplayName(headers.ContentDisposition.FileNameStar);
                if (!string.IsNullOrWhiteSpace(star)) return star;
            }

            if (headers.ContentDisposition?.FileName != null)
            {
                string fn = DecodeDisplayName(headers.ContentDisposition.FileName);
                if (!string.IsNullOrWhiteSpace(fn)) return fn;
            }

            if (headers.TryGetValues("Content-Disposition", out var values))
            {
                string raw = string.Join(" ", values);
                Match mStar = Regex.Match(raw, @"filename\*\s*=\s*(?:UTF-8''|utf-8'')([^;]+)", RegexOptions.IgnoreCase);
                if (mStar.Success)
                    return DecodeDisplayName(mStar.Groups[1].Value.Trim().Trim('"'));

                Match m = Regex.Match(raw, @"filename\s*=\s*""?([^"";]+)""?", RegexOptions.IgnoreCase);
                if (m.Success)
                    return DecodeDisplayName(m.Groups[1].Value);
            }

            return null;
        }

        public static string GuessExtensionFromContentType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return "";
            string ct = contentType.Split(';')[0].Trim().ToLowerInvariant();

            return ct switch
            {
                "application/zip" or "application/x-zip-compressed" => ".zip",
                "application/x-rar-compressed" or "application/vnd.rar" => ".rar",
                "application/x-7z-compressed" => ".7z",
                "application/gzip" or "application/x-gzip" => ".gz",
                "application/pdf" => ".pdf",
                "text/plain" => ".txt",
                "text/html" => ".html",
                "text/csv" => ".csv",
                "application/json" => ".json",
                "application/xml" or "text/xml" => ".xml",
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "audio/mpeg" => ".mp3",
                "video/mp4" => ".mp4",
                "application/msword" => ".doc",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                "application/vnd.ms-excel" => ".xls",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                _ => ""
            };
        }

        public static string EnsureExtension(string fileName, string? contentType)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "download";

            fileName = DecodeDisplayName(fileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "download";

            if (!string.IsNullOrEmpty(Path.GetExtension(fileName)))
                return fileName;

            string guessed = GuessExtensionFromContentType(contentType);
            return string.IsNullOrEmpty(guessed) ? fileName : fileName + guessed;
        }

        public static string? TryFileNameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                foreach (string key in new[] { "filename", "file", "name", "title", "response-content-disposition" })
                {
                    string? val = GetQueryParam(uri.Query, key);
                    if (string.IsNullOrWhiteSpace(val)) continue;

                    if (key == "response-content-disposition")
                    {
                        Match m = Regex.Match(val, @"filename\*?=(?:UTF-8'')?""?([^"";]+)""?", RegexOptions.IgnoreCase);
                        if (m.Success) return DecodeDisplayName(m.Groups[1].Value);
                        continue;
                    }

                    string decoded = DecodeDisplayName(val);
                    if (!string.IsNullOrWhiteSpace(decoded) &&
                        !decoded.Equals("download", StringComparison.OrdinalIgnoreCase))
                        return decoded;
                }

                string last = Path.GetFileName(uri.LocalPath);
                last = DecodeDisplayName(last);
                if (!string.IsNullOrWhiteSpace(last) &&
                    !last.Equals("download", StringComparison.OrdinalIgnoreCase) &&
                    last != "/")
                    return last;
            }
            catch { /* ignore */ }

            return null;
        }

        private static string? GetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string q = query.TrimStart('?');
            foreach (string part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = Uri.UnescapeDataString(part[..eq]);
                if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            }
            return null;
        }

        public static string FormatTypeLabel(string fileName)
        {
            string ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
            return string.IsNullOrEmpty(ext) ? "DOSYA" : ext;
        }

        public static bool IsPlaceholderName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string n = Path.GetFileNameWithoutExtension(name).Trim();
            return n.Equals("download", StringComparison.OrdinalIgnoreCase)
                || n.Equals("downloaded_file", StringComparison.OrdinalIgnoreCase)
                || n.Equals("dosya", StringComparison.OrdinalIgnoreCase);
        }
    }
}
