using System.Collections.Concurrent;

namespace DownloadMuck
{
    /// <summary>
    /// Dosya boyutu, RTT ve eşzamanlı iş yüküne göre kanal sayısı.
    /// </summary>
    public static class ChannelBudget
    {
        public const int MaxPerJob = 16;
        public const int MaxPerHost = 8;
        public const int GlobalMax = 32;

        public static int ForJob(long bytes, int rttMs, int activeJobs, int hostChannels)
        {
            int bySize;
            if (bytes <= 0)
                bySize = 8;
            else if (bytes < 256 * 1024)
                bySize = 1;
            else if (bytes < 2L * 1024 * 1024)
                bySize = 2;
            else if (bytes < 16L * 1024 * 1024)
                bySize = 4;
            else if (bytes < 64L * 1024 * 1024)
                bySize = 8;
            else
                bySize = MaxPerJob;

            int byRtt = rttMs <= 50
                ? bySize
                : rttMs <= 150
                    ? Math.Max(1, (bySize + 1) / 2)
                    : Math.Max(1, bySize / 4);

            int jobShare = Math.Max(1, GlobalMax / Math.Max(1, activeJobs));
            int hostRoom = Math.Max(1, MaxPerHost - Math.Max(0, hostChannels));
            return Math.Clamp(Math.Min(byRtt, Math.Min(jobShare, hostRoom)), 1, MaxPerJob);
        }
    }

    public static class TransferLoad
    {
        private static int _jobs;
        public static int ActiveJobs => Volatile.Read(ref _jobs);
        public static void Enter() => Interlocked.Increment(ref _jobs);
        public static void Exit()
        {
            if (Interlocked.Decrement(ref _jobs) < 0)
                Interlocked.Exchange(ref _jobs, 0);
        }
    }

    public static class HostLoad
    {
        private static readonly ConcurrentDictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase);

        public static int Active(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return 0;
            return Map.TryGetValue(host, out int n) ? n : 0;
        }

        public static void Add(string? host, int channels)
        {
            if (string.IsNullOrWhiteSpace(host) || channels <= 0)
                return;
            Map.AddOrUpdate(host, channels, (_, old) => old + channels);
        }

        public static void Remove(string? host, int channels)
        {
            if (string.IsNullOrWhiteSpace(host) || channels <= 0)
                return;
            Map.AddOrUpdate(host, 0, (_, old) => Math.Max(0, old - channels));
        }
    }
}
