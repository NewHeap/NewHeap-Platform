using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-053: an import of project budget lines from a finance export through
/// <see cref="RepositoryBulkExtensions.ExecuteUpsertAsync{TEntity, TMatch}(IRepository{TEntity}, IEnumerable{TEntity}, System.Linq.Expressions.Expression{Func{TEntity, TMatch}}, CancellationToken)"/>.
/// The currency column has a database default. An export without currencies gets that default, an
/// export with a currency on every line keeps those currencies on insert and update, and an export
/// that has a currency on some lines only is refused before anything is written, because the
/// default cannot apply to some rows of one statement.
/// </summary>
public sealed class BulkUpsertSamplesTests(SamplePostgreSqlServer postgreSql)
{
    [Fact]
    public async Task ADatabaseDefaultedColumnIsWrittenOnlyWhenEveryImportedLineSetsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await postgreSql.CreateDatabaseAsync("bulk_upsert_defaults");
        var services = new ServiceCollection();
        services.AddDbContext<BudgetImportDbContext>(options => options.UseNewHeapPostgreSql(connectionString));
        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BudgetImportDbContext>();
        await context.Database.EnsureCreatedAsync(cancellationToken);
        IRepository<BudgetLine> repository = new Repository<BudgetLine>(context, scope.ServiceProvider);

        // The export has no currency column: every line gets the database default.
        await repository.ExecuteUpsertAsync(
            [
                new BudgetLine { ExternalKey = "FIN-001", Description = "Design sprint", Amount = 12_500m },
                new BudgetLine { ExternalKey = "FIN-002", Description = "User research", Amount = 4_800m }
            ],
            line => line.ExternalKey,
            cancellationToken);
        Assert.Equal("EUR", (await StoredAsync(context, "FIN-001")).Currency);

        // The next export names the currency on every line: inserted and updated lines keep it.
        await repository.ExecuteUpsertAsync(
            [
                new BudgetLine { ExternalKey = "FIN-001", Description = "Design sprint", Amount = 13_000m, Currency = "USD" },
                new BudgetLine { ExternalKey = "FIN-003", Description = "Accessibility audit", Amount = 2_000m, Currency = "GBP" }
            ],
            line => line.ExternalKey,
            cancellationToken);
        Assert.Equal("USD", (await StoredAsync(context, "FIN-001")).Currency);
        Assert.Equal(13_000m, (await StoredAsync(context, "FIN-001")).Amount);
        Assert.Equal("GBP", (await StoredAsync(context, "FIN-003")).Currency);

        // An export with a currency on some lines only is refused, naming the property.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ExecuteUpsertAsync(
            [
                new BudgetLine { ExternalKey = "FIN-002", Description = "User research", Amount = 5_000m, Currency = "USD" },
                new BudgetLine { ExternalKey = "FIN-004", Description = "Usability test", Amount = 900m }
            ],
            line => line.ExternalKey,
            cancellationToken));
        Assert.Contains("'BudgetLine.Currency'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(4_800m, (await StoredAsync(context, "FIN-002")).Amount);
        Assert.False(await context.BudgetLines.AnyAsync(line => line.ExternalKey == "FIN-004", cancellationToken));
    }

    private static Task<BudgetLine> StoredAsync(BudgetImportDbContext context, string externalKey)
    {
        return context.BudgetLines
            .AsNoTracking()
            .SingleAsync(line => line.ExternalKey == externalKey);
    }

    private sealed class BudgetImportDbContext(DbContextOptions<BudgetImportDbContext> options)
        : DbContext(options)
    {
        public DbSet<BudgetLine> BudgetLines => Set<BudgetLine>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BudgetLine>(entity =>
            {
                entity.HasKey(line => line.Id);
                entity.Property(line => line.ExternalKey).HasMaxLength(30);
                entity.Property(line => line.Description).HasMaxLength(200);
                entity.Property(line => line.Amount).HasPrecision(18, 2);
                // A database default makes the property generated on add.
                entity.Property(line => line.Currency).HasMaxLength(3).HasDefaultValue("EUR");
                entity.HasIndex(line => line.ExternalKey).IsUnique();
            });
        }
    }

    private sealed class BudgetLine
    {
        public Guid Id { get; set; }

        public string ExternalKey { get; set; } = "";

        public string Description { get; set; } = "";

        public decimal Amount { get; set; }

        /// <summary>The ISO currency code; <see langword="null"/> leaves it to the database default.</summary>
        public string? Currency { get; set; }
    }
}
