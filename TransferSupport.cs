namespace DownloadMuck
{
    public readonly record struct RemoteEndpoint(
        string Host,
        int Port,
        string User,
        string Password,
        string RemotePath);

    public static class RemoteEndpointParser
    {
        public static bool TryParse(string? url, string defaultScheme, int defaultPort, out RemoteEndpoint endpoint)
        {
            endpoint = default;
            if (string.IsNullOrWhiteSpace(url))
                return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return false;
            if (!uri.Scheme.Equals(defaultScheme, StringComparison.OrdinalIgnoreCase)
                && !(defaultScheme == "ftp" && uri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase)))
                return false;

            string user = "anonymous";
            string pass = "mdm@localhost";
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                string[] parts = uri.UserInfo.Split(':', 2);
                user = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1)
                    pass = Uri.UnescapeDataString(parts[1]);
            }

            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(path) || path == "/")
                return false;

            int port = uri.IsDefaultPort ? defaultPort : uri.Port;
            endpoint = new RemoteEndpoint(uri.Host, port, user, pass, path);
            return !string.IsNullOrWhiteSpace(uri.Host);
        }
    }

    public static class SchedulerGate
    {
        /// <summary>
        /// startHour == endHour means always on. Overnight windows (22→6) are supported.
        /// </summary>
        public static bool IsInsideWindow(DateTime now, int startHour, int endHour)
        {
            startHour = Math.Clamp(startHour, 0, 23);
            endHour = Math.Clamp(endHour, 0, 23);
            if (startHour == endHour)
                return true;

            int h = now.Hour;
            if (startHour < endHour)
                return h >= startHour && h < endHour;
            return h >= startHour || h < endHour;
        }

        public static DateTime NextWindowStart(DateTime now, int startHour, int endHour)
        {
            if (IsInsideWindow(now, startHour, endHour))
                return now;
            var today = now.Date.AddHours(Math.Clamp(startHour, 0, 23));
            return now < today ? today : today.AddDays(1);
        }
    }

    public static class CliArgs
    {
        public static bool TryParseAdd(string[] args, out string url)
        {
            url = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (!IsAddFlag(args[i]))
                    continue;
                if (i + 1 >= args.Length)
                    return false;
                url = args[i + 1].Trim();
                return url.Length > 0;
            }

            foreach (string a in args)
            {
                if (a.StartsWith('-'))
                    continue;
                if (!LooksLikeDownloadTarget(a))
                    continue;
                url = a.Trim();
                return true;
            }
            return false;
        }

        public static bool IsGrab(string[] args)
            => args.Any(a => string.Equals(a, "--grab", StringComparison.OrdinalIgnoreCase));

        public static bool TryParseVerb(string[] args, out string verb, out string value)
        {
            verb = "";
            value = "";
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--list", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "list", StringComparison.OrdinalIgnoreCase))
                {
                    verb = "list";
                    return true;
                }
                if (IsAddFlag(a) || IsGrabFlag(a)
                    || string.Equals(a, "--pause", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "--resume", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "--cancel", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "pause", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "resume", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "cancel", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "download", StringComparison.OrdinalIgnoreCase))
                {
                    verb = NormalizeVerb(a);
                    if (i + 1 >= args.Length)
                        return false;
                    value = args[i + 1].Trim();
                    return value.Length > 0;
                }
            }

            foreach (string a in args)
            {
                if (a.StartsWith('-'))
                    continue;
                if (!LooksLikeDownloadTarget(a))
                    continue;
                verb = "add";
                value = a.Trim();
                return true;
            }
            return false;
        }

        public static bool IsRemoteQuery(string verb)
            => verb is "list" or "pause" or "resume" or "cancel";

        private static bool IsAddFlag(string a)
            => string.Equals(a, "--add", StringComparison.OrdinalIgnoreCase)
               || string.Equals(a, "--download", StringComparison.OrdinalIgnoreCase)
               || string.Equals(a, "download", StringComparison.OrdinalIgnoreCase)
               || string.Equals(a, "add", StringComparison.OrdinalIgnoreCase);

        private static bool IsGrabFlag(string a)
            => string.Equals(a, "--grab", StringComparison.OrdinalIgnoreCase)
               || string.Equals(a, "grab", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeVerb(string raw)
        {
            string t = raw.Trim().TrimStart('-').ToLowerInvariant();
            return t == "download" ? "add" : t;
        }

        public static bool LooksLikeDownloadTarget(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('-'))
                return false;
            return UrlClassifier.CanDownloadNow(UrlClassifier.Classify(raw));
        }
    }
}
