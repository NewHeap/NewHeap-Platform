using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using System.Data.Common;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class BulkUpsertProviderTests
{
    [Fact]
    public Task ThousandsOfRowsAreUpsertedOnSqlServer()
    {
        return VerifySqlServerAsync();
    }

    [Fact]
    public Task ThousandsOfRowsAreUpsertedOnPostgreSql()
    {
        return VerifyPostgreSqlAsync();
    }

    [Fact]
    public async Task UnsupportedProvidersFailBeforeEnumeratingInput()
    {
        var services = new ServiceCollection();
        services.AddDbContext<BulkUpsertDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<IRepository<ImportRow>>(serviceProvider =>
            new Repository<ImportRow>(
                serviceProvider.GetRequiredService<BulkUpsertDbContext>(),
                serviceProvider));
        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<ImportRow>>();
        var enumerated = false;

        IEnumerable<ImportRow> Rows()
        {
            enumerated = true;
            yield return CreateImportRow(0, DateTimeOffset.UtcNow);
        }

        var action = () => repository.ExecuteUpsertAsync(
            Rows(),
            row => new { row.DivisionId, row.ExternalKey });

        await action.Should().ThrowAsync<NotSupportedException>();
        enumerated.Should().BeFalse();
    }

    private static async Task VerifySqlServerAsync()
    {
        await using var container = new MsSqlBuilder(
            "mcr.microsoft.com/mssql/server:2022-latest").Build();
        await container.StartAsync();
        await VerifyProviderAsync(
            options => options.UseSqlServer(container.GetConnectionString()),
            "sql-server");
    }

    private static async Task VerifyPostgreSqlAsync()
    {
        await using var container = new PostgreSqlBuilder("postgres:15.1").Build();
        await container.StartAsync();
        await VerifyProviderAsync(
            options => options.UseNpgsql(container.GetConnectionString()),
            "postgresql");
    }

    private static async Task VerifyProviderAsync(
        Action<DbContextOptionsBuilder> configureProvider,
        string providerName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<BulkUpsertDbContext>(configureProvider);
        services.AddScoped<IRepository<ImportRow>>(serviceProvider =>
            new Repository<ImportRow>(
                serviceProvider.GetRequiredService<BulkUpsertDbContext>(),
                serviceProvider));
        services.AddScoped<IRepository<NumericImportRow>>(serviceProvider =>
            new Repository<NumericImportRow>(
                serviceProvider.GetRequiredService<BulkUpsertDbContext>(),
                serviceProvider));
        services.AddScoped<IRepository<GraphParent>>(serviceProvider =>
            new Repository<GraphParent>(
                serviceProvider.GetRequiredService<BulkUpsertDbContext>(),
                serviceProvider));
        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BulkUpsertDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<ImportRow>>();
        var numericRepository = scope.ServiceProvider.GetRequiredService<IRepository<NumericImportRow>>();
        var graphRepository = scope.ServiceProvider.GetRequiredService<IRepository<GraphParent>>();
        await context.Database.EnsureCreatedAsync();

        const int existingCount = 1_000;
        const int insertedCount = 1_000;
        var originalCreationDateTime = DateTimeOffset.UtcNow.AddDays(-1);
        var existing = Enumerable.Range(0, existingCount)
            .Select(index => CreateImportRow(index, originalCreationDateTime))
            .ToList();
        context.ImportRows.AddRange(existing);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var importDateTime = DateTimeOffset.UtcNow;
        var import = Enumerable.Range(0, existingCount + insertedCount)
            .Select(index => CreateImportRow(index, importDateTime, $"{providerName}-{index}"))
            .ToList();

        var affected = await repository.ExecuteUpsertAsync(
            import,
            row => new { row.DivisionId, row.ExternalKey });

        affected.Should().Be(existingCount + insertedCount);
        import.Single(row => row.ExternalKey == "ROW-0000").Id.Should().BeEmpty();
        import.Single(row => row.ExternalKey == "ROW-1500").Id.Should().NotBeEmpty();
        context.ChangeTracker.Entries<ImportRow>().Should().BeEmpty();
        var stored = await context.ImportRows
            .AsNoTracking()
            .OrderBy(row => row.ExternalKey)
            .ToListAsync();
        stored.Should().HaveCount(existingCount + insertedCount);
        stored.Should().OnlyContain(row => row.Name.StartsWith(providerName));
        stored.Single(row => row.ExternalKey == "ROW-0000").Name
            .Should().Be($"{providerName}-0");
        stored.Single(row => row.ExternalKey == "ROW-0000")
            .CreationDateTime.Should().BeCloseTo(originalCreationDateTime, TimeSpan.FromMilliseconds(1));
        stored.Single(row => row.ExternalKey == "ROW-1500")
            .CreationDateTime.Should().BeCloseTo(importDateTime, TimeSpan.FromMilliseconds(1));
        stored.Single(row => row.ExternalKey == "ROW-1500").Id
            .Should().Be(import.Single(row => row.ExternalKey == "ROW-1500").Id);
        stored.Single(row => row.ExternalKey == "ROW-0001")
            .State.Should().Be(ImportState.Inactive);

        var existingNumeric = new NumericImportRow
        {
            ExternalKey = "NUMERIC-EXISTING",
            Name = "Original"
        };
        context.NumericImportRows.Add(existingNumeric);
        await context.SaveChangesAsync();
        var existingNumericId = existingNumeric.Id;
        context.ChangeTracker.Clear();
        var numericImport = new[]
        {
            new NumericImportRow { ExternalKey = "NUMERIC-EXISTING", Name = "Updated" },
            new NumericImportRow { ExternalKey = "NUMERIC-INSERTED", Name = "Inserted" }
        };

        (await numericRepository.ExecuteUpsertAsync(
            numericImport,
            row => row.ExternalKey)).Should().Be(2);
        numericImport[0].Id.Should().Be(0);
        numericImport[1].Id.Should().BePositive();
        var storedNumeric = await context.NumericImportRows
            .AsNoTracking()
            .ToDictionaryAsync(row => row.ExternalKey);
        storedNumeric.Should().HaveCount(2);
        storedNumeric["NUMERIC-EXISTING"].Id.Should().Be(existingNumericId);
        storedNumeric["NUMERIC-EXISTING"].Name.Should().Be("Updated");
        storedNumeric["NUMERIC-INSERTED"].Id.Should().Be(numericImport[1].Id);
        storedNumeric["NUMERIC-INSERTED"].Name.Should().Be("Inserted");

        await VerifyNavigationGraphAsync(context, graphRepository, providerName);

        var rolledBackKey = $"ROLLBACK-{providerName}";
        await using (var transaction = await repository.StartOrGetTransactionScopeAsync())
        {
            var rollbackAffected = await repository.ExecuteUpsertAsync(
                [CreateImportRow(3_000, importDateTime, rolledBackKey, rolledBackKey)],
                row => new { row.DivisionId, row.ExternalKey });
            rollbackAffected.Should().Be(1);
            await transaction.RollbackAsync();
        }

        (await context.ImportRows
                .AsNoTracking()
                .AnyAsync(row => row.ExternalKey == rolledBackKey))
            .Should().BeFalse();

        var duplicateKey = $"DUPLICATE-{providerName}";
        var duplicateAction = () => repository.ExecuteUpsertAsync(
            [
                CreateImportRow(4_000, importDateTime, "First", duplicateKey),
                CreateImportRow(4_001, importDateTime, "Second", duplicateKey)
            ],
            row => new { row.DivisionId, row.ExternalKey });

        await duplicateAction.Should().ThrowAsync<DbException>();
    }

    private static async Task VerifyNavigationGraphAsync(
        BulkUpsertDbContext context,
        IRepository<GraphParent> repository,
        string providerName)
    {
        var seeded = new GraphParent
        {
            ExternalKey = $"GRAPH-{providerName}",
            Name = "Original root",
            Detail = new GraphDetail { Value = "Original detail" },
            Children =
            [
                new GraphChild { Value = "Original child" },
                new GraphChild { Value = "Child omitted from import" }
            ]
        };
        context.GraphParents.Add(seeded);
        await context.SaveChangesAsync();
        var seededParentId = seeded.Id;
        var seededDetailId = seeded.Detail!.Id;
        var updatedChildId = seeded.Children.First().Id;
        var omittedChildId = seeded.Children.Last().Id;
        context.ChangeTracker.Clear();

        var matched = new GraphParent
        {
            ExternalKey = seeded.ExternalKey,
            Name = "Updated root",
            Detail = new GraphDetail { Id = seededDetailId, Value = "Updated detail" },
            Children =
            [
                new GraphChild { Id = updatedChildId, Value = "Updated child" },
                new GraphChild { Value = "Inserted child" }
            ]
        };
        var inserted = new GraphParent
        {
            ExternalKey = $"GRAPH-NEW-{providerName}",
            Name = "Inserted root",
            Detail = new GraphDetail { Value = "Inserted detail" }
        };

        (await repository.ExecuteUpsertAsync(
            [matched, inserted],
            parent => parent.ExternalKey,
            [parent => parent.Detail, parent => parent.Children]))
            .Should().Be(6);

        matched.Id.Should().Be(seededParentId);
        inserted.Id.Should().NotBeEmpty();
        matched.Detail!.ParentId.Should().Be(seededParentId);
        inserted.Detail!.Id.Should().BePositive();
        inserted.Detail.ParentId.Should().Be(inserted.Id);
        matched.Children.Last().Id.Should().NotBeEmpty();
        matched.Children.Should().OnlyContain(child => child.ParentId == seededParentId);
        context.ChangeTracker.Entries().Should().BeEmpty();

        var stored = await context.GraphParents
            .AsNoTracking()
            .Include(parent => parent.Detail)
            .Include(parent => parent.Children)
            .ToDictionaryAsync(parent => parent.ExternalKey);
        stored[seeded.ExternalKey].Name.Should().Be("Updated root");
        stored[seeded.ExternalKey].Detail!.Value.Should().Be("Updated detail");
        stored[seeded.ExternalKey].Children.Single(child => child.Id == updatedChildId)
            .Value.Should().Be("Updated child");
        stored[seeded.ExternalKey].Children.Should().Contain(child => child.Id == omittedChildId);
        stored[seeded.ExternalKey].Children.Should().Contain(child => child.Value == "Inserted child");
        stored[inserted.ExternalKey].Detail!.Value.Should().Be("Inserted detail");

        var nestedImport = new GraphParent
        {
            ExternalKey = seeded.ExternalKey,
            Name = "Nested import must fail",
            Children =
            [
                new GraphChild
                {
                    Value = "Nested parent",
                    GrandChildren = [new GraphGrandChild { Value = "Nested dependent" }]
                }
            ]
        };
        var nestedAction = () => repository.ExecuteUpsertAsync(
            [nestedImport],
            parent => parent.ExternalKey,
            [parent => parent.Children]);

        await nestedAction.Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("*GraphParent.Children.GrandChildren*");
        var afterNestedFailure = await context.GraphParents
            .AsNoTracking()
            .Include(parent => parent.Children)
            .SingleAsync(parent => parent.Id == seededParentId);
        afterNestedFailure.Name.Should().Be("Updated root");
        afterNestedFailure.Children.Should().HaveCount(3);

        var missingChild = new GraphParent
        {
            ExternalKey = seeded.ExternalKey,
            Name = "Must roll back",
            Children = [new GraphChild { Id = Guid.NewGuid(), Value = "Missing child" }]
        };
        var missingAction = () => repository.ExecuteUpsertAsync(
            [missingChild],
            parent => parent.ExternalKey,
            [parent => parent.Children]);

        await missingAction.Should().ThrowAsync<InvalidOperationException>();
        (await context.GraphParents
                .AsNoTracking()
                .SingleAsync(parent => parent.Id == seededParentId))
            .Name.Should().Be("Updated root");
    }

    private static ImportRow CreateImportRow(
        int index,
        DateTimeOffset timestamp,
        string? name = null,
        string? externalKey = null)
    {
        return new ImportRow
        {
            Id = Guid.Empty,
            DivisionId = Guid.Empty,
            ExternalKey = externalKey ?? $"ROW-{index:0000}",
            Name = name ?? $"Original {index}",
            Description = index % 2 == 0 ? null : $"Description {index}",
            State = index % 2 == 0 ? ImportState.Active : ImportState.Inactive,
            CreationDateTime = timestamp,
            LastModifiedDateTime = timestamp
        };
    }

    private sealed class BulkUpsertDbContext(DbContextOptions<BulkUpsertDbContext> options)
        : DbContext(options)
    {
        public DbSet<ImportRow> ImportRows => Set<ImportRow>();

        public DbSet<NumericImportRow> NumericImportRows => Set<NumericImportRow>();

        public DbSet<GraphParent> GraphParents => Set<GraphParent>();

        public DbSet<GraphDetail> GraphDetails => Set<GraphDetail>();

        public DbSet<GraphChild> GraphChildren => Set<GraphChild>();

        public DbSet<GraphGrandChild> GraphGrandChildren => Set<GraphGrandChild>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ImportRow>(entity =>
            {
                entity.ToTable("BulkImportRows");
                entity.HasKey(row => row.Id);
                entity.Property(row => row.Id)
                    .ValueGeneratedOnAdd()
                    .HasDefaultValueSql(Database.IsSqlServer() ? "NEWID()" : "gen_random_uuid()");
                entity.Property(row => row.ExternalKey).HasMaxLength(40);
                entity.Property(row => row.Name).HasMaxLength(100);
                entity.Property(row => row.Description).HasMaxLength(200);
                entity.HasIndex(row => new { row.DivisionId, row.ExternalKey }).IsUnique();
            });

            modelBuilder.Entity<NumericImportRow>(entity =>
            {
                entity.ToTable("NumericBulkImportRows");
                entity.HasKey(row => row.Id);
                entity.Property(row => row.Id).ValueGeneratedOnAdd();
                entity.Property(row => row.ExternalKey).HasMaxLength(40);
                entity.Property(row => row.Name).HasMaxLength(100);
                entity.HasIndex(row => row.ExternalKey).IsUnique();
            });

            modelBuilder.Entity<GraphParent>(entity =>
            {
                entity.ToTable("BulkGraphParents");
                entity.HasKey(parent => parent.Id);
                entity.Property(parent => parent.Id).ValueGeneratedOnAdd();
                entity.Property(parent => parent.ExternalKey).HasMaxLength(80);
                entity.Property(parent => parent.Name).HasMaxLength(100);
                entity.HasIndex(parent => parent.ExternalKey).IsUnique();
            });

            modelBuilder.Entity<GraphDetail>(entity =>
            {
                entity.ToTable("BulkGraphDetails");
                entity.HasKey(detail => detail.Id);
                entity.Property(detail => detail.Id).ValueGeneratedOnAdd();
                entity.Property(detail => detail.Value).HasMaxLength(100);
                entity.HasOne(detail => detail.Parent)
                    .WithOne(parent => parent.Detail)
                    .HasForeignKey<GraphDetail>(detail => detail.ParentId);
            });

            modelBuilder.Entity<GraphChild>(entity =>
            {
                entity.ToTable("BulkGraphChildren");
                entity.HasKey(child => child.Id);
                entity.Property(child => child.Id).ValueGeneratedOnAdd();
                entity.Property(child => child.Value).HasMaxLength(100);
                entity.HasOne(child => child.Parent)
                    .WithMany(parent => parent.Children)
                    .HasForeignKey(child => child.ParentId);
            });

            modelBuilder.Entity<GraphGrandChild>(entity =>
            {
                entity.ToTable("BulkGraphGrandChildren");
                entity.HasKey(grandChild => grandChild.Id);
                entity.Property(grandChild => grandChild.Id).ValueGeneratedOnAdd();
                entity.Property(grandChild => grandChild.Value).HasMaxLength(100);
                entity.HasOne(grandChild => grandChild.Parent)
                    .WithMany(child => child.GrandChildren)
                    .HasForeignKey(grandChild => grandChild.ParentId);
            });
        }
    }

    private sealed class ImportRow : IdDbEntity
    {
        public Guid Id { get; set; }

        public DateTimeOffset CreationDateTime { get; set; }

        public DateTimeOffset LastModifiedDateTime { get; set; }

        public Guid DivisionId { get; set; }

        public string ExternalKey { get; set; } = "";

        public string Name { get; set; } = "";

        public string? Description { get; set; }

        public ImportState State { get; set; }
    }

    private sealed class NumericImportRow
    {
        public long Id { get; set; }

        public string ExternalKey { get; set; } = "";

        public string Name { get; set; } = "";
    }

    private sealed class GraphParent
    {
        public Guid Id { get; set; }

        public string ExternalKey { get; set; } = "";

        public string Name { get; set; } = "";

        public GraphDetail? Detail { get; set; }

        public ICollection<GraphChild> Children { get; set; } = [];
    }

    private sealed class GraphDetail
    {
        public long Id { get; set; }

        public Guid ParentId { get; set; }

        public GraphParent Parent { get; set; } = null!;

        public string Value { get; set; } = "";
    }

    private sealed class GraphChild
    {
        public Guid Id { get; set; }

        public Guid ParentId { get; set; }

        public GraphParent Parent { get; set; } = null!;

        public string Value { get; set; } = "";

        public ICollection<GraphGrandChild> GrandChildren { get; set; } = [];
    }

    private sealed class GraphGrandChild
    {
        public Guid Id { get; set; }

        public Guid ParentId { get; set; }

        public GraphChild Parent { get; set; } = null!;

        public string Value { get; set; } = "";
    }

    private enum ImportState
    {
        Active,
        Inactive
    }
}
