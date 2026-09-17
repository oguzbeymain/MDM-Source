namespace MDM
{
    /// <summary>
    /// Global download throttle. 0 KB/s = unlimited.
    /// Sürekli dolan jeton kovası: sabit saniye penceresi, bütçe bitince tüm kanalları
    /// pencere dönene kadar durdurduğu için hız saniyede bir coşup düşüyordu.
    /// HTTP parçaları, FTP ve SFTP aynı kovayı paylaşır.
    /// </summary>
    public static class SpeedLimiter
    {
        private static readonly object Gate = new();
        private static long _lastRefillMs = Environment.TickCount64;
        private static double _tokens;

        internal static int? OverrideBytesPerSecond { get; set; }

        public static int LimitBytesPerSecond
        {
            get
            {
                if (OverrideBytesPerSecond.HasValue)
                    return Math.Max(0, OverrideBytesPerSecond.Value);
                int kb = AppSettingsStore.Load().SpeedLimitKBps;
                return kb <= 0 ? 0 : kb * 1024;
            }
        }

        public static async Task AwaitAsync(int bytes, CancellationToken token)
        {
            int limit = LimitBytesPerSecond;
            if (limit <= 0 || bytes <= 0)
                return;

            while (!token.IsCancellationRequested)
            {
                int waitMs;
                lock (Gate)
                {
                    long now = Environment.TickCount64;
                    double elapsedSec = Math.Max(0, now - _lastRefillMs) / 1000.0;
                    _lastRefillMs = now;

                    // Kova yarım saniyelik kredi tutar; tek okuma bundan büyükse ona göre büyür
                    double cap = Math.Max(limit * 0.5, bytes);
                    _tokens = Math.Min(_tokens + elapsedSec * limit, cap);

                    if (_tokens >= bytes)
                    {
                        _tokens -= bytes;
                        return;
                    }

                    waitMs = (int)Math.Clamp((bytes - _tokens) / limit * 1000.0, 5, 250);
                }

                await Task.Delay(waitMs, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Hız sınırı varken 1 MB'lık okuma tek seferde saniyelerce bekleme borcu yaratır;
        /// akışın düzgün kalması için okuma parçası sınıra göre küçültülür.
        /// </summary>
        public static int SuggestReadSize(int max)
        {
            int limit = LimitBytesPerSecond;
            if (limit <= 0)
                return max;
            return Math.Clamp(limit / 8, 32 * 1024, max);
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _lastRefillMs = Environment.TickCount64;
                _tokens = 0;
            }
            OverrideBytesPerSecond = null;
        }
    }

    public static class DownloadQueue
    {
        public static bool IsFull(int active, int maxConcurrent)
            => maxConcurrent > 0 && active >= maxConcurrent;

        public static int HttpChannels(AppSettings? settings = null)
        {
            var s = settings ?? AppSettingsStore.Load();
            if (s.HttpMaxChannels <= 0)
                return ChannelBudget.MaxPerJob;
            return Math.Clamp(s.HttpMaxChannels, 1, ChannelBudget.MaxPerJob);
        }
    }
}
