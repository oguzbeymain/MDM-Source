using System.Collections.Concurrent;

namespace MDM
{
    /// <summary>
    /// İndirme spam / saldırı koruması.
    /// Gündelik tek tık veya aralıklı tekrar indirmeyi engellemez;
    /// kısa sürede peş peşe gelen isteklerde tek onay + sessiz düşürme.
    /// </summary>
    public static class RepeatDownloadGuard
    {
        private static readonly object Gate = new();

        private static readonly ConcurrentDictionary<string, List<long>> AttemptsByUrl =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly List<long> GlobalAttempts = new();

        private static readonly ConcurrentDictionary<string, long> BlockedUntilUtcTicks =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, long> TrustedUntilUtcTicks =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool _confirmOpen;
        private static long _globalQuietUntilUtcTicks;
        private static bool _globalBurstArmed;

        public static readonly TimeSpan SpamWindow = TimeSpan.FromSeconds(45);
        public static readonly TimeSpan GlobalBurstWindow = TimeSpan.FromSeconds(20);
        public const int SpamThreshold = 4;
        public const int GlobalBurstThreshold = 4;
        public static readonly TimeSpan UrlBlockDuration = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan TrustDuration = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan GlobalQuietAfterDeny = TimeSpan.FromSeconds(45);

        public enum AdmitResult
        {
            Allow,
            DropSilent,
            NeedsConfirm
        }

        public static string NormalizeKey(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            string t = url.Trim();
            if (t.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                return t;
            try
            {
                var uri = new Uri(t);
                string path = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                return path + (string.IsNullOrEmpty(uri.Query) ? "" : uri.Query);
            }
            catch
            {
                return t;
            }
        }

        /// <summary>
        /// Gemini/ChatGPT stream'leri, response.bin vb. — hiç pencere açma.
        /// </summary>
        public static bool IsNoiseCapture(string? url, string? filename = null)
        {
            string name = (filename ?? "").Split('/', '\\').LastOrDefault()?.Trim() ?? "";
            if (name.Length > 0)
            {
                if (name.Equals("response.bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("download.bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("file.bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("f.txt", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (RegexJunkName(name))
                    return true;
            }

            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                var host = new Uri(url).Host.ToLowerInvariant();
                if (host is "gemini.google.com" or "bard.google.com") return true;
                if (host.EndsWith(".gemini.google.com", StringComparison.Ordinal)) return true;
                if (host.Contains("chatgpt.com", StringComparison.Ordinal)
                    || host.Contains("chat.openai.com", StringComparison.Ordinal))
                    return true;
                if (host.Contains("claude.ai", StringComparison.Ordinal)
                    || host.Contains("anthropic.com", StringComparison.Ordinal))
                    return true;
                if (host.Contains("copilot.microsoft.com", StringComparison.Ordinal)) return true;
                if (host.Contains("perplexity.ai", StringComparison.Ordinal)
                    || host.Contains("poe.com", StringComparison.Ordinal)
                    || host.Contains("character.ai", StringComparison.Ordinal))
                    return true;
                if (host.Contains("generativelanguage.googleapis.com", StringComparison.Ordinal))
                    return true;
            }
            catch { /* ignore */ }

            if (url.Contains("/$rpc/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("StreamGenerate", StringComparison.OrdinalIgnoreCase)
                || url.Contains("BardChatUi", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static bool RegexJunkName(string name)
        {
            string n = name.ToLowerInvariant();
            return n is "response.bin" or "download.bin" or "file.bin" or "data.bin" or "blob.bin"
                || n is "response.dat" or "download.dat"
                || (n.EndsWith(".bin", StringComparison.Ordinal) && n.StartsWith("response", StringComparison.Ordinal));
        }

        public static AdmitResult Evaluate(string? url, AppSettings? settings = null, string? filename = null)
        {
            if (IsNoiseCapture(url, filename))
                return AdmitResult.DropSilent;

            var s = settings ?? AppSettingsStore.Load();
            if (!s.ConfirmRepeatDownloads)
                return AdmitResult.Allow;

            string key = NormalizeKey(url);
            long now = DateTime.UtcNow.Ticks;

            lock (Gate)
            {
                if (now < _globalQuietUntilUtcTicks)
                    return AdmitResult.DropSilent;

                if (_confirmOpen)
                    return AdmitResult.DropSilent;

                if (key.Length > 0
                    && BlockedUntilUtcTicks.TryGetValue(key, out long blockUntil)
                    && now < blockUntil)
                    return AdmitResult.DropSilent;

                if (key.Length > 0
                    && TrustedUntilUtcTicks.TryGetValue(key, out long trustUntil)
                    && now < trustUntil)
                    return AdmitResult.Allow;

                int globalRecent = RecordGlobalAndCount(now);
                if (globalRecent >= GlobalBurstThreshold)
                {
                    // Farklı URL'lerle flood (Gemini) — tek onay veya sessiz
                    if (!_globalBurstArmed)
                    {
                        _globalBurstArmed = true;
                        _confirmOpen = true;
                        return AdmitResult.NeedsConfirm;
                    }
                    return AdmitResult.DropSilent;
                }

                if (key.Length == 0)
                    return AdmitResult.Allow;

                int recent = RecordAndCount(key, now);
                if (recent < SpamThreshold)
                    return AdmitResult.Allow;

                _confirmOpen = true;
                return AdmitResult.NeedsConfirm;
            }
        }

        public static void OnConfirmAllowed(string? url)
        {
            string key = NormalizeKey(url);
            long now = DateTime.UtcNow.Ticks;
            lock (Gate)
            {
                _confirmOpen = false;
                _globalBurstArmed = false;
                GlobalAttempts.Clear();
                if (key.Length > 0)
                {
                    AttemptsByUrl.TryRemove(key, out _);
                    BlockedUntilUtcTicks.TryRemove(key, out _);
                    TrustedUntilUtcTicks[key] = now + TrustDuration.Ticks;
                }
            }
        }

        public static void OnConfirmDenied(string? url)
        {
            string key = NormalizeKey(url);
            long now = DateTime.UtcNow.Ticks;
            lock (Gate)
            {
                _confirmOpen = false;
                _globalBurstArmed = false;
                GlobalAttempts.Clear();
                _globalQuietUntilUtcTicks = now + GlobalQuietAfterDeny.Ticks;
                if (key.Length > 0)
                {
                    AttemptsByUrl.TryRemove(key, out _);
                    BlockedUntilUtcTicks[key] = now + UrlBlockDuration.Ticks;
                }
            }
        }

        public static void ReleaseConfirmLock()
        {
            lock (Gate)
            {
                _confirmOpen = false;
                _globalBurstArmed = false;
            }
        }

        public static int PeekRecentCount(string? url)
        {
            string key = NormalizeKey(url);
            if (key.Length == 0) return 0;
            long now = DateTime.UtcNow.Ticks;
            long cutoff = now - SpamWindow.Ticks;
            lock (Gate)
            {
                if (!AttemptsByUrl.TryGetValue(key, out var list) || list.Count == 0)
                    return 0;
                return list.Count(t => t >= cutoff);
            }
        }

        private static int RecordAndCount(string key, long nowTicks)
        {
            long cutoff = nowTicks - SpamWindow.Ticks;
            var list = AttemptsByUrl.GetOrAdd(key, _ => new List<long>());
            list.RemoveAll(t => t < cutoff);
            list.Add(nowTicks);
            if (list.Count > 64)
                list.RemoveRange(0, list.Count - 32);
            return list.Count;
        }

        private static int RecordGlobalAndCount(long nowTicks)
        {
            long cutoff = nowTicks - GlobalBurstWindow.Ticks;
            GlobalAttempts.RemoveAll(t => t < cutoff);
            GlobalAttempts.Add(nowTicks);
            if (GlobalAttempts.Count > 128)
                GlobalAttempts.RemoveRange(0, GlobalAttempts.Count - 64);
            return GlobalAttempts.Count;
        }
    }
}
