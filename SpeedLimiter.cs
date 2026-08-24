namespace DownloadMuck
{
    /// <summary>
    /// Global download throttle. 0 KB/s = unlimited.
    /// Shared 1-second window so HTTP chunks, FTP and SFTP share the same cap.
    /// </summary>
    public static class SpeedLimiter
    {
        private static readonly object Gate = new();
        private static long _windowStartMs = Environment.TickCount64;
        private static long _windowBytes;

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
                    if (now - _windowStartMs >= 1000)
                    {
                        _windowStartMs = now;
                        _windowBytes = 0;
                    }

                    if (_windowBytes + bytes <= limit)
                    {
                        _windowBytes += bytes;
                        return;
                    }

                    waitMs = (int)Math.Clamp(1000 - (now - _windowStartMs), 15, 1000);
                }

                await Task.Delay(waitMs, token).ConfigureAwait(false);
            }
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _windowStartMs = Environment.TickCount64;
                _windowBytes = 0;
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
