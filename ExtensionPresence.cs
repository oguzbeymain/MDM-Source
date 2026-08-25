namespace DownloadMuck
{
    /// <summary>
    /// Eklentinin periyodik /ext/ping sinyali (tarayıcı başına).
    /// </summary>
    public static class ExtensionPresence
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, DateTime> LastPingUtc = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _anyPingUtc = DateTime.MinValue;
        private static bool _everSeen;

        public static void NotifyPing(string? browserId = null)
        {
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                _anyPingUtc = now;
                _everSeen = true;
                string key = NormalizeBrowser(browserId);
                if (!string.IsNullOrEmpty(key))
                    LastPingUtc[key] = now;
            }
        }

        public static bool HasEverSeen
        {
            get { lock (Gate) return _everSeen; }
        }

        public static bool SeenRecently(TimeSpan maxAge, string? browserId = null)
        {
            lock (Gate)
            {
                if (!_everSeen) return false;
                string key = NormalizeBrowser(browserId);
                if (!string.IsNullOrEmpty(key) && LastPingUtc.TryGetValue(key, out var t))
                    return DateTime.UtcNow - t <= maxAge;
                // Tarayıcı bilinmiyorsa genel ping
                return DateTime.UtcNow - _anyPingUtc <= maxAge;
            }
        }

        public static bool SeenRecentlyForBrowser(string browserId, TimeSpan maxAge)
        {
            lock (Gate)
            {
                if (!LastPingUtc.TryGetValue(NormalizeBrowser(browserId), out var t))
                    return false;
                return DateTime.UtcNow - t <= maxAge;
            }
        }

        public static bool HasEverSeenBrowser(string browserId)
        {
            lock (Gate)
                return LastPingUtc.ContainsKey(NormalizeBrowser(browserId));
        }

        private static string NormalizeBrowser(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            id = id.Trim().ToLowerInvariant();
            if (id is "edge" or "msedge" or "microsoft edge") return "edge";
            if (id is "chrome" or "google chrome") return "chrome";
            if (id is "brave") return "brave";
            return id;
        }
    }
}
