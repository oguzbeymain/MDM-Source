namespace MDM
{
    public static class RobotsTxt
    {
        public static bool IsAllowed(string? robotsBody, string userAgent, string path)
        {
            if (string.IsNullOrWhiteSpace(robotsBody))
                return true;
            if (string.IsNullOrWhiteSpace(path))
                path = "/";
            if (!path.StartsWith('/'))
                path = "/" + path;

            string ua = (userAgent ?? "*").Trim();
            var rules = new List<(bool Allow, string Prefix)>();
            bool inStar = false;
            bool inUs = false;

            foreach (string rawLine in robotsBody.Split('\n'))
            {
                string line = rawLine.Trim();
                int comment = line.IndexOf('#');
                if (comment >= 0)
                    line = line[..comment].Trim();
                if (line.Length == 0)
                    continue;

                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();

                if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                {
                    inStar = value == "*";
                    inUs = value.Equals(ua, StringComparison.OrdinalIgnoreCase)
                           || value.Equals("MDM", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inStar && !inUs)
                    continue;

                if (key.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Length == 0)
                        continue;
                    rules.Add((false, value));
                }
                else if (key.Equals("Allow", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Length == 0)
                        continue;
                    rules.Add((true, value));
                }
            }

            bool allowed = true;
            int best = -1;
            foreach (var (allow, prefix) in rules)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                if (prefix.Length < best)
                    continue;
                best = prefix.Length;
                allowed = allow;
            }
            return allowed;
        }

        public static string PathOf(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "/";
            string p = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
            return string.IsNullOrEmpty(uri.Query) ? p : p + uri.Query;
        }
    }
}
