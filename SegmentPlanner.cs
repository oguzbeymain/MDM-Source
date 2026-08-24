namespace DownloadMuck
{
    /// <summary>
    /// HTTP Range iş parçası planı — sabit eşit dilimler yerine boyuta göre parça sayısı.
    /// </summary>
    public static class SegmentPlanner
    {
        public const long TargetChunkBytes = 2L * 1024 * 1024;
        public const long MinChunkBytes = 256L * 1024;
        public const int MaxChunks = 64;

        public static int PlanChunkCount(long totalSize, int threadCount)
        {
            if (totalSize <= MinChunkBytes)
                return 1;

            int threads = Math.Max(1, threadCount);
            int bySize = (int)Math.Max(1, totalSize / TargetChunkBytes);
            int maxChunks = Math.Min(MaxChunks, Math.Max(threads * 8, threads));
            int count = Math.Clamp(bySize, 1, maxChunks);
            if (totalSize / count < MinChunkBytes)
                count = Math.Max(1, (int)(totalSize / MinChunkBytes));
            return Math.Max(1, count);
        }

        public static List<ChunkState> BuildChunks(long totalSize, int threadCount)
        {
            if (totalSize <= 0)
                return new List<ChunkState>();

            int count = PlanChunkCount(totalSize, threadCount);
            long chunkSize = Math.Max(1, totalSize / count);
            var list = new List<ChunkState>(count);

            for (int i = 0; i < count; i++)
            {
                long start = i * chunkSize;
                if (start >= totalSize)
                    break;
                long end = (i == count - 1) ? totalSize - 1 : Math.Min(totalSize - 1, start + chunkSize - 1);
                list.Add(new ChunkState
                {
                    Start = start,
                    End = end,
                    CurrentOffset = start
                });
            }

            return list;
        }

        public static bool CoversExactly(IReadOnlyList<ChunkState> chunks, long totalSize)
        {
            if (totalSize <= 0)
                return chunks.Count == 0;
            if (chunks.Count == 0)
                return false;

            long expected = 0;
            foreach (var c in chunks.OrderBy(x => x.Start))
            {
                if (c.Start != expected || c.End < c.Start)
                    return false;
                expected = c.End + 1;
            }

            return expected == totalSize;
        }

        public static ChunkState? TrySplitSlowest(IList<ChunkState> chunks, long minRemainingBytes)
        {
            ChunkState? victim = null;
            long best = minRemainingBytes * 2 - 1;
            foreach (var c in chunks)
            {
                long rem = c.End - c.CurrentOffset + 1;
                if (rem > best)
                {
                    best = rem;
                    victim = c;
                }
            }
            if (victim == null)
                return null;

            long remaining = victim.End - victim.CurrentOffset + 1;
            long mid = victim.CurrentOffset + remaining / 2;
            if (mid <= victim.CurrentOffset || mid > victim.End)
                return null;

            long oldEnd = victim.End;
            victim.End = mid - 1;
            return new ChunkState
            {
                Start = mid,
                End = oldEnd,
                CurrentOffset = mid
            };
        }
    }
}
