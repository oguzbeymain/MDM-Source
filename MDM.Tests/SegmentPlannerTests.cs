using Xunit;
using MDM;

namespace MDM.Tests;

public class SegmentPlannerTests
{
    [Theory]
    [InlineData(100, 8, 1)]
    [InlineData(256 * 1024, 8, 1)]
    [InlineData(256 * 1024 + 1, 8, 1)]
    public void Tiny_files_use_single_chunk(long size, int threads, int expected)
    {
        Assert.Equal(expected, SegmentPlanner.PlanChunkCount(size, threads));
    }

    [Fact]
    public void Eight_meg_file_uses_multiple_chunks_capped_by_size()
    {
        int count = SegmentPlanner.PlanChunkCount(8L * 1024 * 1024, 8);
        Assert.InRange(count, 2, 8);
    }

    [Fact]
    public void Hundred_meg_file_uses_worker_pool_not_one_chunk_per_thread()
    {
        int count = SegmentPlanner.PlanChunkCount(100L * 1024 * 1024, 8);
        Assert.True(count > 8, $"expected more pieces than threads, got {count}");
        Assert.True(count <= SegmentPlanner.MaxChunks);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(1023, 4)]
    [InlineData(1024 * 1024, 8)]
    [InlineData(2 * 1024 * 1024 + 17, 8)]
    [InlineData(50L * 1024 * 1024, 4)]
    [InlineData(50L * 1024 * 1024, 8)]
    [InlineData(200L * 1024 * 1024, 8)]
    public void Chunks_cover_every_byte_without_overlap(long size, int threads)
    {
        var chunks = SegmentPlanner.BuildChunks(size, threads);
        Assert.True(SegmentPlanner.CoversExactly(chunks, size),
            $"coverage failed size={size} threads={threads} pieces={chunks.Count}");
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(size - 1, chunks[^1].End);
        Assert.All(chunks, c => Assert.Equal(c.Start, c.CurrentOffset));
    }

    [Fact]
    public void Zero_size_is_empty()
    {
        Assert.Empty(SegmentPlanner.BuildChunks(0, 8));
        Assert.True(SegmentPlanner.CoversExactly(Array.Empty<ChunkState>(), 0));
    }
}
