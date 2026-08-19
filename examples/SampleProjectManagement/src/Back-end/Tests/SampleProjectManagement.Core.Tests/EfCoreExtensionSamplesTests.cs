using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.Common.Extensions;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public class EfCoreExtensionSamplesTests
{
    [Fact]
    public async Task ChunkAsyncReadsAnOrderedQueryInBoundedBatches()
    {
        var options = new DbContextOptionsBuilder<ChunkSampleDbContext>()
            .UseInMemoryDatabase($"chunk-sample-{Guid.NewGuid()}")
            .Options;

        await using var dbContext = new ChunkSampleDbContext(options);
        dbContext.Rows.AddRange(
            new ChunkSampleRow { Id = 5, Name = "Deploy" },
            new ChunkSampleRow { Id = 2, Name = "Design" },
            new ChunkSampleRow { Id = 4, Name = "Review" },
            new ChunkSampleRow { Id = 1, Name = "Intake" },
            new ChunkSampleRow { Id = 3, Name = "Build" });
        await dbContext.SaveChangesAsync();

        var chunks = new List<List<ChunkSampleRow>>();
        var query = dbContext.Rows
            .AsNoTracking()
            .OrderBy(row => row.Id);

        await foreach (var chunk in query.ChunkAsync(2))
        {
            chunks.Add(chunk);
        }

        Assert.Equal([2, 2, 1], chunks.Select(chunk => chunk.Count));
        Assert.Equal([1, 2, 3, 4, 5], chunks.SelectMany(chunk => chunk).Select(row => row.Id));
    }

    [Fact]
    public async Task ChunkAsyncRejectsANonPositiveBatchSizeBeforeReading()
    {
        var options = new DbContextOptionsBuilder<ChunkSampleDbContext>()
            .UseInMemoryDatabase($"chunk-guard-{Guid.NewGuid()}")
            .Options;
        await using var dbContext = new ChunkSampleDbContext(options);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in dbContext.Rows.ChunkAsync(0))
            {
            }
        });

        Assert.Equal("chunkSize", exception.ParamName);
    }

    [Fact]
    public async Task ChunkAsyncPassesCancellationToTheQueryExecution()
    {
        var options = new DbContextOptionsBuilder<ChunkSampleDbContext>()
            .UseInMemoryDatabase($"chunk-cancellation-{Guid.NewGuid()}")
            .Options;
        await using var dbContext = new ChunkSampleDbContext(options);
        dbContext.Rows.Add(new ChunkSampleRow { Id = 1, Name = "Cancelled" });
        await dbContext.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in dbContext.Rows.OrderBy(row => row.Id).ChunkAsync(1, cancellation.Token))
            {
            }
        });
    }

    private sealed class ChunkSampleDbContext(DbContextOptions<ChunkSampleDbContext> options)
        : DbContext(options)
    {
        public DbSet<ChunkSampleRow> Rows => Set<ChunkSampleRow>();
    }

    private sealed class ChunkSampleRow
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }
}
