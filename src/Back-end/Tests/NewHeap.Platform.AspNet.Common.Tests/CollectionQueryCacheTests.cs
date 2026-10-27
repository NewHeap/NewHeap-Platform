using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Tests.Relational;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class CollectionQueryCacheTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer, false)]
    [InlineData(RelationalTestProvider.PostgreSql, false)]
    [InlineData(RelationalTestProvider.SqlServer, true)]
    [InlineData(RelationalTestProvider.PostgreSql, true)]
    public async Task RepeatedFiltersReuseCompilationWhenValuesChange(RelationalTestProvider provider, bool execute)
    {
        // Only the executing cases need a server; the others translate without a connection.
        var database = execute ? await ProviderTestDatabase.CreateAsync(provider) : null;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var compilations = 0;
        var options = new DbContextOptionsBuilder<Database>()
            .UseMemoryCache(cache)
            .LogTo(_ => compilations++, [CoreEventId.QueryCompilationStarting]);
        if (provider == RelationalTestProvider.SqlServer)
        {
            options.UseSqlServer(database?.ConnectionString ?? "Server=localhost;Database=TranslationOnly;Integrated Security=true");
        }
        else
        {
            options.UseNpgsql(database?.ConnectionString ?? "Host=localhost;Database=translation_only");
        }

        await using var db = new Database(options.Options);
        if (execute)
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var name in new[] { "alpha", "beta" })
            {
                db.Add(new Row { Name = name, Children = [new Child { Name = name }] });
            }

            await db.SaveChangesAsync();
        }

        var processor = new Processor();
        Func<string, List<FilterCollectionRequestModel>>[] filters =
        [
            name => [new() { Key = "Name", Operator = "==", Value = name }],
            name => [new() { Key = "Children{any}.Name", Operator = "==", Value = name }],
            name => [new() { Key = "Children{all}.Name", Operator = "==", Value = name }],
            name =>
            [
                new() { Key = "Name", Operator = "==", Value = name },
                new() { Key = "Children{any}.Name", Operator = "==", Value = name }
            ],
            name =>
            [
                new()
                {
                    Key = "Name", Operator = "==", Value = name,
                    Ands = [new() { Key = "Children{any}.Name", Operator = "==", Value = name }]
                }
            ],
            name =>
            [
                new()
                {
                    Key = "Name", Operator = "==", Value = "missing",
                    Ors = [new() { Key = "Children{any}.Name", Operator = "==", Value = name }]
                }
            ],
            name =>
            [
                new()
                {
                    Key = "Children{any}.Name", Operator = "==", Value = "missing",
                    Ors = [new() { Key = "Children{any}.Name", Operator = "==", Value = name }]
                }
            ],
            name =>
            [
                new()
                {
                    Key = "Name", Operator = "!=", Value = "missing",
                    Ands =
                    [
                        new()
                        {
                            Key = "Children{any}.Name", Operator = "==", Value = "missing",
                            Ors = [new() { Key = "Children{all}.Name", Operator = "==", Value = name }]
                        }
                    ]
                }
            ],
            name =>
            [
                new() { Key = "Children{any}.Name", Operator = "==", Value = name },
                new() { Key = "Name", Operator = "==", Value = name }
            ]
        ];
        foreach (var createFilters in filters)
        {
            var before = compilations;
            foreach (var (name, useOptions) in new[] { ("alpha", false), ("alpha", true), ("beta", false), ("beta", true), ("alpha", false) })
            {
                IQueryable<Row> query = db.Set<Row>().AsNoTracking();
                if (useOptions)
                {
                    processor.ProcessWithOptions(ref query, createFilters(name));
                }
                else
                {
                    processor.ProcessFilter<Row, Row>(ref query, createFilters(name));
                }

                var parameters = new LambdaParameters();
                parameters.Visit(query.Expression);
                Assert.Equal(Enumerable.Range(1, parameters.Names.Count).Select(index => $"x{index}"), parameters.Names);

                if (execute)
                {
                    Assert.Equal(1, await query.LongCountAsync());
                    Assert.Equal(name, Assert.Single(await query.ToListAsync()).Name);
                }
                else
                {
                    Assert.Contains("WHERE", query.ToQueryString());
                }

                Assert.Equal(execute ? 2 : 1, compilations - before);
            }
        }
    }

    private sealed class Processor() : CollectionProcessingService(Substitute.For<IMapper>())
    {
        public void ProcessWithOptions(ref IQueryable<Row> query, List<FilterCollectionRequestModel> filters)
        {
            ProcessFilter(ref query, filters, CollectionProcessingOptions<Row, Row>.Default);
        }
    }

    private sealed class LambdaParameters : ExpressionVisitor
    {
        public List<string?> Names { get; } = [];

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            Names.AddRange(node.Parameters.Select(parameter => parameter.Name));
            return base.VisitLambda(node);
        }
    }

    private sealed class Database(DbContextOptions<Database> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>().HasMany(row => row.Children).WithOne();
        }
    }

    private sealed class Row
    {
        public int Id { get; set; }
        [Filterable] public string Name { get; set; } = "";
        [Filterable] public List<Child> Children { get; set; } = [];
    }

    private sealed class Child
    {
        public int Id { get; set; }
        [Filterable] public string Name { get; set; } = "";
    }
}
