using System.Text.Json;

namespace MDM
{
    public sealed class RemoteJobDto
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "";
        public double Progress { get; set; }
        public string Speed { get; set; } = "";
    }

    public interface IRemoteJobHost
    {
        IReadOnlyList<RemoteJobDto> ListJobs();
        RemoteJobDto? GetJob(string id);
        string? AddJob(string url, string? filename);
        bool Pause(string id);
        bool Resume(string id);
        bool Cancel(string id);
    }

    public readonly record struct RemoteApiResult(int Status, string ContentType, string Body)
    {
        public static RemoteApiResult Text(int status, string body)
            => new(status, "text/plain; charset=utf-8", body);

        public static RemoteApiResult Json(int status, string body)
            => new(status, "application/json; charset=utf-8", body);
    }

    public static class RemoteApiRouter
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public static RemoteApiResult Route(
            string method,
            string path,
            string body,
            bool isLoopback,
            string auth,
            bool lanEnabled,
            string token,
            Action<string, string, string> onCapture,
            IRemoteJobHost? jobs,
            Action<ExtCaptureRequest>? onExtCapture = null)
        {
            method = (method ?? "GET").ToUpperInvariant();
            string rawPath = path ?? "/";
            string query = "";
            int qMark = rawPath.IndexOf('?');
            if (qMark >= 0)
            {
                query = rawPath[(qMark + 1)..];
                rawPath = rawPath[..qMark];
            }
            string p = NormalizePath(rawPath);

            if (method == "OPTIONS")
                return RemoteApiResult.Text(200, "OK");

            if (lanEnabled && !isLoopback)
            {
                if (string.IsNullOrWhiteSpace(token) || !AuthOk(auth, token))
                    return RemoteApiResult.Text(401, "Unauthorized");
            }

            if (method == "GET" && (p == "/" || p == "/health"))
                return RemoteApiResult.Text(200, "MDM capture ready");

            // Eklenti canlılık — /ext/ping?browser=edge|chrome|brave|firefox
            if ((method == "GET" || method == "POST") && (p == "/ext/ping" || p == "/ping"))
            {
                string? browser = QueryValue(query, "browser");
                try
                {
                    if (browser == null && !string.IsNullOrWhiteSpace(body))
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("browser", out var b))
                            browser = b.GetString();
                    }
                }
                catch { /* ignore */ }

                ExtensionPresence.NotifyPing(browser);
                string lang = Loc.Code;
                bool rtl = Loc.IsRtl;
                return RemoteApiResult.Json(200,
                    JsonSerializer.Serialize(new { ok = true, language = lang, rtl }, JsonOpts));
            }

            if (method == "GET" && p == "/jobs")
            {
                var list = jobs?.ListJobs() ?? Array.Empty<RemoteJobDto>();
                return RemoteApiResult.Json(200, JsonSerializer.Serialize(new { jobs = list }, JsonOpts));
            }

            if (method == "GET" && p.StartsWith("/jobs/", StringComparison.OrdinalIgnoreCase))
            {
                string id = p["/jobs/".Length..].Trim('/');
                if (id.Contains('/'))
                    return RemoteApiResult.Text(404, "Not found");
                var job = jobs?.GetJob(id);
                return job == null
                    ? RemoteApiResult.Text(404, "Not found")
                    : RemoteApiResult.Json(200, JsonSerializer.Serialize(job, JsonOpts));
            }

            if (method == "POST" && p == "/jobs")
            {
                if (!TryReadUrl(body, out string url, out string filename) || jobs == null)
                    return RemoteApiResult.Text(400, "Bad Request");
                string? id = jobs.AddJob(url, filename);
                if (string.IsNullOrWhiteSpace(id))
                    return RemoteApiResult.Text(400, "Rejected");
                return RemoteApiResult.Json(202, JsonSerializer.Serialize(new { id }, JsonOpts));
            }

            if (method == "POST" && p.StartsWith("/jobs/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || jobs == null)
                    return RemoteApiResult.Text(404, "Not found");
                string id = parts[1];
                string action = parts[2].ToLowerInvariant();
                bool ok = action switch
                {
                    "pause" => jobs.Pause(id),
                    "resume" => jobs.Resume(id),
                    "cancel" => jobs.Cancel(id),
                    _ => false
                };
                return ok
                    ? RemoteApiResult.Json(200, JsonSerializer.Serialize(new { ok = true, id, action }, JsonOpts))
                    : RemoteApiResult.Text(404, "Not found");
            }

            if (method == "POST" && (p == "/" || p == "/capture"))
            {
                if (!TryReadUrl(body, out string url, out string filename))
                    return RemoteApiResult.Text(400, "Bad Request");
                string mime = "";
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (doc.RootElement.TryGetProperty("mime", out var mimeEl))
                        mime = mimeEl.GetString() ?? "";
                }
                catch { /* capture may be plain */ }

                ExtensionPresence.NotifyPing();
                onCapture(url, filename, mime);
                return RemoteApiResult.Text(200, "OK");
            }

            if ((method == "GET" || method == "POST") && p == "/ext/formats")
            {
                var req = TryReadFormatsRequest(body);
                if (req == null)
                    return RemoteApiResult.Text(400, "Bad Request");
                ExtensionPresence.NotifyPing();
                var formats = MediaFormatService.GetFormats(req);
                return RemoteApiResult.Json(200, MediaFormatService.SerializeFormats(formats));
            }

            if (method == "POST" && p == "/ext/capture")
            {
                var ext = TryReadExtCapture(body);
                if (ext == null || string.IsNullOrWhiteSpace(ext.Url) && string.IsNullOrWhiteSpace(ext.PageUrl))
                    return RemoteApiResult.Text(400, "Bad Request");
                ExtensionPresence.NotifyPing();
                ext = MediaFormatService.ResolveCapture(ext);
                if (onExtCapture != null)
                    onExtCapture(ext);
                else
                    onCapture(ext.Url, ext.Filename, ext.Mime);
                return RemoteApiResult.Text(200, "OK");
            }

            if (method != "GET" && method != "POST" && method != "OPTIONS")
                return RemoteApiResult.Text(405, "Method Not Allowed");

            return RemoteApiResult.Text(404, "Not found");
        }

        public static string NormalizePath(string? path)
        {
            string p = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            int q = p.IndexOf('?');
            if (q >= 0)
                p = p[..q];
            if (p.Length == 0)
                p = "/";
            if (p.Length > 1)
                p = p.TrimEnd('/');
            return p;
        }

        private static string? QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part[..eq];
                if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
            }
            return null;
        }

        private static bool AuthOk(string auth, string token)
            => auth.Equals("Bearer " + token, StringComparison.Ordinal)
               || auth.Equals(token, StringComparison.Ordinal);

        private static bool TryReadUrl(string? json, out string url, out string filename)
        {
            url = "";
            filename = "";
            if (string.IsNullOrWhiteSpace(json))
                return false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("url", out var urlEl))
                    url = urlEl.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("filename", out var nameEl))
                    filename = FileNameHelper.DecodeDisplayName(nameEl.GetString() ?? "");
            }
            catch
            {
                return false;
            }
            return !string.IsNullOrWhiteSpace(url);
        }

        private static ExtCaptureRequest? TryReadExtCapture(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var req = new ExtCaptureRequest();
                if (root.TryGetProperty("url", out var urlEl))
                    req.Url = urlEl.GetString() ?? "";
                if (root.TryGetProperty("filename", out var nameEl))
                    req.Filename = FileNameHelper.DecodeDisplayName(nameEl.GetString() ?? "");
                if (root.TryGetProperty("mime", out var mimeEl))
                    req.Mime = mimeEl.GetString() ?? "";
                if (root.TryGetProperty("pageUrl", out var pageEl))
                    req.PageUrl = pageEl.GetString() ?? "";
                if (root.TryGetProperty("referrer", out var refEl))
                    req.Referrer = refEl.GetString() ?? "";
                if (root.TryGetProperty("kind", out var kindEl))
                    req.Kind = kindEl.GetString() ?? "";
                if (root.TryGetProperty("formatId", out var fmtEl))
                    req.FormatId = fmtEl.GetString() ?? "";
                if (root.TryGetProperty("title", out var titleEl))
                    req.Title = titleEl.GetString() ?? "";
                if (root.TryGetProperty("filesize", out var sizeEl))
                {
                    if (sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetInt64(out long fs))
                        req.Filesize = fs;
                    else if (sizeEl.ValueKind == JsonValueKind.String
                             && long.TryParse(sizeEl.GetString(), out long fs2))
                        req.Filesize = fs2;
                }
                if (root.TryGetProperty("cookies", out var cookieEl))
                    req.Cookies = cookieEl.GetString() ?? "";
                if (root.TryGetProperty("headers", out var hdrEl) && hdrEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in hdrEl.EnumerateObject())
                        req.Headers[prop.Name] = prop.Value.GetString() ?? "";
                }
                return req;
            }
            catch
            {
                return null;
            }
        }

        private static FormatsRequest? TryReadFormatsRequest(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var req = new FormatsRequest();
                if (root.TryGetProperty("pageUrl", out var pageEl))
                    req.PageUrl = pageEl.GetString() ?? "";
                if (root.TryGetProperty("mediaUrl", out var mediaEl))
                    req.MediaUrl = mediaEl.GetString() ?? "";
                if (root.TryGetProperty("playlistBody", out var bodyEl))
                    req.PlaylistBody = bodyEl.GetString() ?? "";
                if (root.TryGetProperty("cookies", out var cookieEl))
                    req.Cookies = cookieEl.GetString() ?? "";
                if (root.TryGetProperty("referrer", out var refEl))
                    req.Referrer = refEl.GetString() ?? "";
                if (root.TryGetProperty("site", out var siteEl))
                    req.Site = siteEl.GetString() ?? "";
                if (root.TryGetProperty("title", out var titleEl))
                    req.Title = titleEl.GetString() ?? "";
                if (root.TryGetProperty("candidates", out var candEl) && candEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in candEl.EnumerateArray())
                    {
                        string? s = c.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            req.Candidates.Add(s);
                    }
                }
                if (root.TryGetProperty("videoMeta", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object)
                {
                    if (metaEl.TryGetProperty("videoWidth", out var vw) && vw.TryGetInt32(out int w))
                        req.VideoWidth = w;
                    if (metaEl.TryGetProperty("videoHeight", out var vh) && vh.TryGetInt32(out int h))
                        req.VideoHeight = h;
                }
                if (root.TryGetProperty("headers", out var hdrEl) && hdrEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in hdrEl.EnumerateObject())
                        req.Headers[prop.Name] = prop.Value.GetString() ?? "";
                }
                return req;
            }
            catch
            {
                return null;
            }
        }
    }
}
