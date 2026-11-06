using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Tests.Relational;
using System.Data.Common;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class BulkUpsertProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ThousandsOfRowsAreUpsertedOnEachRelationalProvider(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        await VerifyProviderAsync(database.UseNewHeapProvider, database.Label);
    }

    /// <summary>
    /// A property with a database default is generated on add, so the bulk plan used to skip its
    /// column even when the caller set it. It is now written when every row sets it, left to the
    /// database default when no row does, and a batch that mixes both is refused before any SQL.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task DatabaseDefaultedColumnsAreWrittenWhenEveryRowSetsThemOnEachRelationalProvider(
        RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        await using var environment = await BulkUpsertEnvironment.CreateAsync(database.UseNewHeapProvider);
        var context = environment.Context;
        var repository = environment.Repository<DefaultedImportRow>();

        // No row sets the defaulted columns: the database defaults apply, as before.
        (await repository.ExecuteUpsertAsync(
            [
                new DefaultedImportRow { ExternalKey = "DEFAULT-1", Name = "First" },
                new DefaultedImportRow { ExternalKey = "DEFAULT-2", Name = "Second" }
            ],
            row => row.ExternalKey)).Should().Be(2);
        var defaulted = await StoredDefaultedRowsAsync(context);
        defaulted["DEFAULT-1"].Priority.Should().Be(3);
        defaulted["DEFAULT-1"].Category.Should().Be("general");
        defaulted["DEFAULT-1"].ImportedAt.Should().NotBe(default);

        // Every row sets them: the values are written on insert and on update. The input is a
        // one-shot sequence; it is read once to inspect the batch.
        var importedAt = new DateTimeOffset(2026, 3, 1, 8, 30, 0, TimeSpan.Zero);
        var enumerations = 0;
        IEnumerable<DefaultedImportRow> SetRows()
        {
            enumerations++;
            yield return new DefaultedImportRow
            {
                ExternalKey = "DEFAULT-1",
                Name = "First updated",
                Priority = 7,
                Category = "sales",
                ImportedAt = importedAt
            };
            yield return new DefaultedImportRow
            {
                ExternalKey = "DEFAULT-3",
                Name = "Third",
                Priority = 1,
                Category = "support",
                ImportedAt = importedAt
            };
        }

        (await repository.ExecuteUpsertAsync(SetRows(), row => row.ExternalKey)).Should().Be(2);
        enumerations.Should().Be(1);
        var written = await StoredDefaultedRowsAsync(context);
        written["DEFAULT-1"].Name.Should().Be("First updated");
        written["DEFAULT-1"].Priority.Should().Be(7);
        written["DEFAULT-1"].Category.Should().Be("sales");
        written["DEFAULT-1"].ImportedAt.Should().Be(importedAt);
        written["DEFAULT-3"].Priority.Should().Be(1);
        written["DEFAULT-3"].Category.Should().Be("support");
        written["DEFAULT-3"].ImportedAt.Should().Be(importedAt);

        // An update that sets none of them keeps the stored values instead of resetting them.
        (await repository.ExecuteUpsertAsync(
            [new DefaultedImportRow { ExternalKey = "DEFAULT-1", Name = "First renamed" }],
            row => row.ExternalKey)).Should().Be(1);
        var kept = (await StoredDefaultedRowsAsync(context))["DEFAULT-1"];
        kept.Name.Should().Be("First renamed");
        kept.Priority.Should().Be(7);
        kept.Category.Should().Be("sales");

        // Only some rows set a defaulted column: refused with the property named, nothing written.
        var mixed = () => repository.ExecuteUpsertAsync(
            [
                new DefaultedImportRow { ExternalKey = "DEFAULT-2", Name = "Second updated", Priority = 5 },
                new DefaultedImportRow { ExternalKey = "DEFAULT-4", Name = "Fourth" }
            ],
            row => row.ExternalKey);
        await mixed.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*'DefaultedImportRow.Priority'*1 of 2 rows*");
        var afterMixed = await StoredDefaultedRowsAsync(context);
        afterMixed.Should().NotContainKey("DEFAULT-4");
        afterMixed["DEFAULT-2"].Name.Should().Be("Second");

        // The graph overload applies the same rule to each group of dependents.
        var graphRoot = new DefaultedImportRow
        {
            ExternalKey = "DEFAULT-GRAPH",
            Name = "Graph root",
            Notes =
            [
                new DefaultedImportNote { Text = "Weighted", Weight = 4 },
                new DefaultedImportNote { Text = "Also weighted", Weight = 2 }
            ]
        };
        (await repository.ExecuteUpsertAsync(
            [graphRoot],
            row => row.ExternalKey,
            [row => row.Notes])).Should().Be(3);
        var unweightedRoot = new DefaultedImportRow
        {
            ExternalKey = "DEFAULT-GRAPH-2",
            Name = "Second graph root",
            Notes = [new DefaultedImportNote { Text = "Unweighted" }]
        };
        (await repository.ExecuteUpsertAsync(
            [unweightedRoot],
            row => row.ExternalKey,
            [row => row.Notes])).Should().Be(2);
        var notes = await context.DefaultedImportNotes
            .AsNoTracking()
            .ToDictionaryAsync(note => note.Text);
        notes["Weighted"].Weight.Should().Be(4);
        notes["Also weighted"].Weight.Should().Be(2);
        notes["Unweighted"].Weight.Should().Be(1);

        var mixedGraph = () => repository.ExecuteUpsertAsync(
            [
                new DefaultedImportRow
                {
                    ExternalKey = "DEFAULT-GRAPH",
                    Name = "Must roll back",
                    Notes =
                    [
                        new DefaultedImportNote { Text = "Mixed weighted", Weight = 9 },
                        new DefaultedImportNote { Text = "Mixed unweighted" }
                    ]
                }
            ],
            row => row.ExternalKey,
            [row => row.Notes]);
        await mixedGraph.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*'DefaultedImportNote.Weight'*1 of 2 rows*");
        (await StoredDefaultedRowsAsync(context))["DEFAULT-GRAPH"].Name.Should().Be("Graph root");
        (await context.DefaultedImportNotes.AsNoTracking().CountAsync()).Should().Be(3);
    }

    /// <summary>
    /// A graph update stages the dependent's generated key in a temporary table. On SQL Server that
    /// table inherits the column's identity, which must not renumber the staged keys.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task GraphUpdatesTheMatchedIdentityKeyedDependentOnEachRelationalProvider(
        RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        await using var environment = await BulkUpsertEnvironment.CreateAsync(database.UseNewHeapProvider);
        var context = environment.Context;
        var repository = environment.Repository<GraphParent>();

        var first = new GraphParent
        {
            ExternalKey = "IDENTITY-1",
            Name = "First",
            Detail = new GraphDetail { Value = "First detail" }
        };
        var second = new GraphParent
        {
            ExternalKey = "IDENTITY-2",
            Name = "Second",
            Detail = new GraphDetail { Value = "Second detail" }
        };
        context.GraphParents.AddRange(first, second);
        await context.SaveChangesAsync();
        var firstDetailId = first.Detail.Id;
        var secondDetailId = second.Detail.Id;
        context.ChangeTracker.Clear();

        (await repository.ExecuteUpsertAsync(
            [
                new GraphParent
                {
                    ExternalKey = "IDENTITY-2",
                    Name = "Second updated",
                    Detail = new GraphDetail { Id = secondDetailId, Value = "Second detail updated" }
                }
            ],
            parent => parent.ExternalKey,
            [parent => parent.Detail])).Should().Be(2);

        var details = await context.GraphDetails
            .AsNoTracking()
            .ToDictionaryAsync(detail => detail.Id);
        details[firstDetailId].Value.Should().Be("First detail");
        details[secondDetailId].Value.Should().Be("Second detail updated");
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

    private static Task<Dictionary<string, DefaultedImportRow>> StoredDefaultedRowsAsync(BulkUpsertDbContext context)
    {
        return context.DefaultedImportRows
            .AsNoTracking()
            .ToDictionaryAsync(row => row.ExternalKey);
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

    /// <summary>
    /// A scope with the bulk test model created in the test's own database and a repository per
    /// entity type, as an application registers them.
    /// </summary>
    private sealed class BulkUpsertEnvironment : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly AsyncServiceScope _scope;

        private BulkUpsertEnvironment(ServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _scope = serviceProvider.CreateAsyncScope();
            Context = _scope.ServiceProvider.GetRequiredService<BulkUpsertDbContext>();
        }

        public BulkUpsertDbContext Context { get; }

        public static async Task<BulkUpsertEnvironment> CreateAsync(Action<DbContextOptionsBuilder> configureProvider)
        {
            var services = new ServiceCollection();
            services.AddDbContext<BulkUpsertDbContext>(configureProvider);
            var environment = new BulkUpsertEnvironment(services.BuildServiceProvider());
            await environment.Context.Database.EnsureCreatedAsync();
            return environment;
        }

        public IRepository<TEntity> Repository<TEntity>()
            where TEntity : class
        {
            return new Repository<TEntity>(Context, _scope.ServiceProvider);
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _serviceProvider.DisposeAsync();
        }
    }

    private sealed class BulkUpsertDbContext(DbContextOptions<BulkUpsertDbContext> options)
        : DbContext(options)
    {
        public DbSet<ImportRow> ImportRows => Set<ImportRow>();

        public DbSet<DefaultedImportRow> DefaultedImportRows => Set<DefaultedImportRow>();

        public DbSet<DefaultedImportNote> DefaultedImportNotes => Set<DefaultedImportNote>();

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

            // Database defaults make these properties generated on add by convention.
            modelBuilder.Entity<DefaultedImportRow>(entity =>
            {
                entity.ToTable("DefaultedBulkImportRows");
                entity.HasKey(row => row.Id);
                entity.Property(row => row.Id).ValueGeneratedOnAdd();
                entity.Property(row => row.ExternalKey).HasMaxLength(40);
                entity.Property(row => row.Name).HasMaxLength(100);
                entity.Property(row => row.Priority).HasDefaultValue(3);
                entity.Property(row => row.Category).HasMaxLength(40).HasDefaultValue("general");
                entity.Property(row => row.ImportedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.HasIndex(row => row.ExternalKey).IsUnique();
            });

            modelBuilder.Entity<DefaultedImportNote>(entity =>
            {
                entity.ToTable("DefaultedBulkImportNotes");
                entity.HasKey(note => note.Id);
                entity.Property(note => note.Id).ValueGeneratedOnAdd();
                entity.Property(note => note.Text).HasMaxLength(100);
                entity.Property(note => note.Weight).HasDefaultValue(1);
                entity.HasOne(note => note.Row)
                    .WithMany(row => row.Notes)
                    .HasForeignKey(note => note.RowId);
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

    private sealed class DefaultedImportRow
    {
        public long Id { get; set; }

        public string ExternalKey { get; set; } = "";

        public string Name { get; set; } = "";

        public int Priority { get; set; }

        public string? Category { get; set; }

        public DateTimeOffset ImportedAt { get; set; }

        public ICollection<DefaultedImportNote> Notes { get; set; } = [];
    }

    private sealed class DefaultedImportNote
    {
        public Guid Id { get; set; }

        public long RowId { get; set; }

        public DefaultedImportRow Row { get; set; } = null!;

        public string Text { get; set; } = "";

        public int Weight { get; set; }
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
