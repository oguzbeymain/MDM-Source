using System.Text.Json;

namespace DownloadMuck
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
            IRemoteJobHost? jobs)
        {
            method = (method ?? "GET").ToUpperInvariant();
            string p = NormalizePath(path);

            if (method == "OPTIONS")
                return RemoteApiResult.Text(200, "OK");

            if (lanEnabled && !isLoopback)
            {
                if (string.IsNullOrWhiteSpace(token) || !AuthOk(auth, token))
                    return RemoteApiResult.Text(401, "Unauthorized");
            }

            if (method == "GET" && (p == "/" || p == "/health"))
                return RemoteApiResult.Text(200, "MDM capture ready");

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

                onCapture(url, filename, mime);
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
    }
}
