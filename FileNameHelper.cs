using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace MDM
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
                if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    Match dn = Regex.Match(url, @"[?&]dn=([^&]+)", RegexOptions.IgnoreCase);
                    if (dn.Success)
                    {
                        string decoded = DecodeDisplayName(Uri.UnescapeDataString(dn.Groups[1].Value.Replace('+', ' ')));
                        if (!string.IsNullOrWhiteSpace(decoded))
                            return decoded;
                    }
                    return "torrent";
                }

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
                    !last.Equals("uc", StringComparison.OrdinalIgnoreCase) &&
                    !last.Equals("open", StringComparison.OrdinalIgnoreCase) &&
                    !last.Equals("file", StringComparison.OrdinalIgnoreCase) &&
                    !last.Equals("export", StringComparison.OrdinalIgnoreCase) &&
                    !last.Equals("view", StringComparison.OrdinalIgnoreCase) &&
                    last != "/")
                    return last;

                // Google Drive usercontent — sorgu parametrelerinden dosya adı
                if (uri.Host.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string key in new[] { "filename", "name", "title" })
                    {
                        string? val = GetQueryParam(uri.Query, key);
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            string decoded = DecodeDisplayName(val);
                            if (!IsPlaceholderName(decoded))
                                return decoded;
                        }
                    }
                }
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
            string file = Path.GetFileName(name).Trim();
            string n = Path.GetFileNameWithoutExtension(file).Trim();
            if (n.Equals("download", StringComparison.OrdinalIgnoreCase)
                || n.Equals("downloaded_file", StringComparison.OrdinalIgnoreCase)
                || n.Equals("dosya", StringComparison.OrdinalIgnoreCase)
                || n.Equals("uc", StringComparison.OrdinalIgnoreCase)
                || n.Equals("file", StringComparison.OrdinalIgnoreCase))
                return true;

            // Chrome varsayılanı: download (1), download (2) ...
            if (Regex.IsMatch(n, @"^download\s*\(\d+\)$", RegexOptions.IgnoreCase))
                return true;

            // Chrome/octet-stream varsayılanı — gerçek ad henüz yok
            if (file.Equals("download.bin", StringComparison.OrdinalIgnoreCase)
                || file.Equals("file.bin", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Uzantısız veya geçici isimler sunucudan gerçek ad alınana kadar güncellenmeli.
        /// </summary>
        public static bool NeedsResolution(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (IsPlaceholderName(name)) return true;
            return string.IsNullOrEmpty(Path.GetExtension(name));
        }

        public static bool IsBetterName(string candidate, string current)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)) return false;
            if (IsPlaceholderName(current) && !IsPlaceholderName(candidate)) return true;
            if (string.IsNullOrEmpty(Path.GetExtension(current))
                && !string.IsNullOrEmpty(Path.GetExtension(candidate)))
                return true;
            return !IsPlaceholderName(candidate);
        }

        public static string? GuessExtensionFromMagic(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B)
                return ".zip";
            if (data.Length >= 7 && data[0] == 0x52 && data[1] == 0x61 && data[2] == 0x72 && data[3] == 0x21)
                return ".rar";
            if (data.Length >= 6 && data[0] == 0x37 && data[1] == 0x7A && data[2] == 0xBC && data[3] == 0xAF)
                return ".7z";
            if (data.Length >= 3 && data[0] == 0x1F && data[1] == 0x8B)
                return ".gz";
            if (data.Length >= 4 && data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
                return ".pdf";
            return null;
        }

        /// <summary>
        /// Google Drive virüs uyarısı HTML'inden gerçek dosya adını çekmeyi dener.
        /// </summary>
        public static string? TryFileNameFromHtml(string? html)
        {
            if (string.IsNullOrWhiteSpace(html) || html.Length < 20) return null;
            if (html.IndexOf('<') < 0) return null;

            Match title = Regex.Match(html, @"<title>\s*([^<]+?)\s*</title>", RegexOptions.IgnoreCase);
            if (title.Success)
            {
                string raw = System.Net.WebUtility.HtmlDecode(title.Groups[1].Value);
                raw = Regex.Replace(raw, @"\s*[-–—]\s*Google Drive\s*$", "", RegexOptions.IgnoreCase).Trim();
                string decoded = DecodeDisplayName(raw);
                if (!IsPlaceholderName(decoded) && Path.GetExtension(decoded).Length > 1)
                    return decoded;
            }

            Match named = Regex.Match(
                html,
                @"([^\s<>""'][^<>""']{2,180}?\.(?:rar|zip|7z|gz|tar|iso|exe|msi|apk|pdf|mp4|mkv|mp3|dmg))\s*(?:\(|<|$)",
                RegexOptions.IgnoreCase);
            if (named.Success)
            {
                string raw = System.Net.WebUtility.HtmlDecode(named.Groups[1].Value).Trim();
                string decoded = DecodeDisplayName(raw);
                if (!IsPlaceholderName(decoded) && Path.GetExtension(decoded).Length > 1)
                    return decoded;
            }

            return null;
        }

        public static string ChooseDisplayName(
            string? fromHeader, string? suggested, string? fromUrl, string? contentType)
        {
            string decodedSuggested = DecodeDisplayName(suggested);
            string media = (contentType ?? "").Split(';')[0].Trim();
            if (media.Contains("html", StringComparison.OrdinalIgnoreCase)
                || media.Contains("javascript", StringComparison.OrdinalIgnoreCase))
            {
                contentType = null;
            }

            string? best = null;
            foreach (string? n in new[] { fromHeader, decodedSuggested, fromUrl })
            {
                if (string.IsNullOrWhiteSpace(n) || IsPlaceholderName(n)) continue;
                best = n;
                break;
            }

            if (best == null)
            {
                string ext = GuessExtensionFromContentType(contentType);
                if (!string.IsNullOrEmpty(ext) && ext != ".bin" && ext != ".html" && ext != ".htm")
                    return "download" + ext;
                return "download";
            }

            return EnsureExtension(best, contentType);
        }
    }
}
