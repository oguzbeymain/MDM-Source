using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace DownloadMuck
{
    public static class LinkGrabberService
    {
        private static readonly HashSet<string> FileExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".rar", ".7z", ".tar", ".gz", ".xz", ".iso",
            ".exe", ".msi", ".apk", ".dmg", ".pdf", ".torrent",
            ".mp4", ".mkv", ".webm", ".mp3", ".flac", ".wav",
            ".jpg", ".png", ".gif", ".webp", ".bin", ".img"
        };

        public static bool LooksLikeFileUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;
            string ext = Path.GetExtension(uri.AbsolutePath);
            return FileExt.Contains(ext);
        }

        public static async Task<IReadOnlyList<string>> FetchLinksAsync(
            string pageUrl, int maxDepth, int maxPages, CancellationToken token)
        {
            maxDepth = Math.Clamp(maxDepth, 0, 3);
            maxPages = Math.Clamp(maxPages, 1, 40);

            var found = new List<string>();
            var seenPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Url, int Depth)>();
            queue.Enqueue((pageUrl, 0));
            var robotsCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var client = TransferHttp.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);

            while (queue.Count > 0 && seenPages.Count < maxPages && !token.IsCancellationRequested)
            {
                var (url, depth) = queue.Dequeue();
                if (!seenPages.Add(url))
                    continue;

                if (depth > 0 && !await IsRobotsAllowedAsync(client, robotsCache, url, token).ConfigureAwait(false))
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

                foreach (string link in UrlClassifier.ExtractHttpUrls(html, url))
                {
                    if (LooksLikeFileUrl(link))
                    {
                        if (!await IsRobotsAllowedAsync(client, robotsCache, link, token).ConfigureAwait(false))
                            continue;
                        if (!found.Contains(link, StringComparer.OrdinalIgnoreCase))
                            found.Add(link);
                    }
                    else if (depth < maxDepth)
                    {
                        queue.Enqueue((link, depth + 1));
                    }
                }

                foreach (string video in VideoProbe.ExtractFromHtml(html, url))
                {
                    if (!await IsRobotsAllowedAsync(client, robotsCache, video, token).ConfigureAwait(false))
                        continue;
                    if (!found.Contains(video, StringComparer.OrdinalIgnoreCase))
                        found.Add(video);
                }
            }

            return found;
        }

        private static async Task<bool> IsRobotsAllowedAsync(
            HttpClient client, Dictionary<string, string> cache, string url, CancellationToken token)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return true;
            string origin = uri.GetLeftPart(UriPartial.Authority);
            if (!cache.TryGetValue(origin, out string? body))
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, origin + "/robots.txt");
                    using var resp = await client.SendAsync(req, token);
                    body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(token) : "";
                }
                catch
                {
                    body = "";
                }
                cache[origin] = body;
            }

            return RobotsTxt.IsAllowed(body, "MDM", RobotsTxt.PathOf(url));
        }
    }

    public static class VideoProbe
    {
        public static IReadOnlyList<string> ExtractFromHtml(string? html, string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(html))
                return Array.Empty<string>();

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return;
                string v = System.Net.WebUtility.HtmlDecode(raw.Trim());
                if (!v.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(baseUrl)
                    && Uri.TryCreate(new Uri(baseUrl), v, out var abs))
                    v = abs.ToString();
                if (UrlClassifier.Classify(v) != TransferKind.Http)
                    return;
                if (seen.Add(v))
                    list.Add(v);
            }

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         html,
                         """(?:og:video|twitter:player:stream|property="og:video:url")[^>]*content=["']([^"']+)["']""",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                Add(m.Groups[1].Value);

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         html,
                         """<(?:video|source)[^>]+src=["']([^"']+)["']""",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                Add(m.Groups[1].Value);

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         html,
                         """content=["']([^"']+\.(?:mp4|webm|m3u8|mkv)[^"']*)["']""",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                Add(m.Groups[1].Value);

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         html,
                         """application/ld\+json["'][^>]*>([\s\S]*?)</script>""",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string json = m.Groups[1].Value;
                foreach (System.Text.RegularExpressions.Match u in System.Text.RegularExpressions.Regex.Matches(
                             json,
                             """"(?:contentUrl|embedUrl|url)"\s*:\s*"(https?://[^"]+\.(?:mp4|webm|m3u8|mkv)[^"]*)"""",
                             System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    Add(u.Groups[1].Value);
            }

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         html,
                         """["']file["']\s*:\s*["'](https?://[^"']+\.(?:mp4|webm|m3u8|mkv)[^"']*)["']""",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                Add(m.Groups[1].Value);

            foreach (string u in UrlClassifier.ExtractHttpUrls(html, baseUrl))
            {
                if (u.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                    || u.Contains(".webm", StringComparison.OrdinalIgnoreCase)
                    || u.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                    || u.Contains(".mkv", StringComparison.OrdinalIgnoreCase))
                    Add(u);
            }

            return list;
        }

        public static bool IsVideoPage(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;
            string h = uri.Host;
            return h.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                   || h.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
                   || h.Contains("vimeo.com", StringComparison.OrdinalIgnoreCase)
                   || Path.GetExtension(uri.AbsolutePath) is ".mp4" or ".webm" or ".mkv" or ".m3u8";
        }
    }
}
