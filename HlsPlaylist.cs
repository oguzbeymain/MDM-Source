namespace DownloadMuck
{
    public static class HlsPlaylist
    {
        public static string? PickBestVariant(string? playlist, string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(playlist) || !playlist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                return null;

            int bestBw = -1;
            string? best = null;
            int pendingBw = -1;

            foreach (string raw in playlist.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    pendingBw = 0;
                    int i = line.IndexOf("BANDWIDTH=", StringComparison.OrdinalIgnoreCase);
                    if (i >= 0)
                    {
                        string rest = line[(i + "BANDWIDTH=".Length)..];
                        int end = 0;
                        while (end < rest.Length && char.IsDigit(rest[end]))
                            end++;
                        _ = int.TryParse(rest[..end], out pendingBw);
                    }
                    continue;
                }

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    pendingBw = -1;
                    continue;
                }

                if (pendingBw >= 0 && pendingBw >= bestBw)
                {
                    bestBw = pendingBw;
                    best = Resolve(line, baseUrl);
                }
                pendingBw = -1;
            }

            return best;
        }

        public static string Resolve(string maybeRelative, string? baseUrl)
        {
            if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out var abs))
                return abs.ToString();
            if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)
                && Uri.TryCreate(b, maybeRelative, out var joined))
                return joined.ToString();
            return maybeRelative;
        }
    }
}
