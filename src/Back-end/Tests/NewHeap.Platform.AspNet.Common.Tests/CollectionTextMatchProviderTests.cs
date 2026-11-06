using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Tests.Relational;
using NSubstitute;
using System.ComponentModel;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// Collection search and the <c>LIKE</c> filter find the same rows on SQL Server and PostgreSQL:
/// SQL Server's default collation matches case-insensitively, and <c>UseNewHeapPostgreSql</c>
/// matches with <c>ILIKE</c>. <c>UseNewHeapCaseSensitiveCollectionTextMatch</c> restores
/// PostgreSQL's case-sensitive <c>LIKE</c>.
/// </summary>
public sealed class CollectionTextMatchProviderTests
{
    private static readonly string[] MixedCaseAlphaNames = ["ALPHA beta", "Alpha release", "alphabet"];

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Mixed_case_search_finds_the_same_rows_on_each_provider(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        await using var context = await CreateSeededContextAsync(database.UseNewHeapProvider);
        var processor = new CollectionProcessingService(Substitute.For<IMapper>());

        IQueryable<TextItem> attributeSearch = context.Items.AsNoTracking();
        processor.ProcessSearch<TextItem, TextItemViewModel>(ref attributeSearch, "aLpHa");
        Assert.Equal(MixedCaseAlphaNames, await NamesAsync(attributeSearch));

        IQueryable<TextItem> optionsSearch = context.Items.AsNoTracking();
        processor.ProcessSearch(ref optionsSearch, "aLpHa", CollectionProcessingOptions<TextItem, TextItemViewModel>.Default);
        Assert.Equal(MixedCaseAlphaNames, await NamesAsync(optionsSearch));

        // A configured selector over a collection searches inside an EXISTS; the case rule holds there too.
        var byTag = await processor.GetCollectionResultModelAsync<TextItem, TextItem>(
            new CollectionRequestModel { Page = 1, ItemsPerPage = 20, Search = "uRgEnT" },
            context.Items,
            options => options.WithSearchable(item => item.Tags.Select(tag => tag.Label)),
            resultQueryableFunc: null,
            asNoTracking: true,
            cancellationToken: default,
            (item => (object)item.Name, ListSortDirection.Ascending));
        Assert.Equal(2, byTag.TotalCount);
        Assert.Equal(["ALPHA beta", "Gamma"], byTag.Items.Select(item => item.Name));
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Like_filter_follows_the_same_case_rule_on_each_provider(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        await using var context = await CreateSeededContextAsync(database.UseNewHeapProvider);
        var processor = new CollectionProcessingService(Substitute.For<IMapper>());

        IQueryable<TextItem> contains = context.Items.AsNoTracking();
        processor.ProcessFilter<TextItem, TextItemViewModel>(ref contains,
            [new FilterCollectionRequestModel { Key = "name", Operator = "LIKE", Value = "%ALPHA%" }]);
        Assert.Equal(MixedCaseAlphaNames, await NamesAsync(contains));

        // The client's wildcards keep their LIKE meaning: _ is one character and % any text.
        IQueryable<TextItem> wildcard = context.Items.AsNoTracking();
        processor.ProcessFilter<TextItem, TextItemViewModel>(ref wildcard,
            [new FilterCollectionRequestModel { Key = "name", Operator = "like", Value = "AL_HA%" }]);
        Assert.Equal(MixedCaseAlphaNames, await NamesAsync(wildcard));

        IQueryable<TextItem> nested = context.Items.AsNoTracking();
        processor.ProcessFilter<TextItem, TextItemViewModel>(ref nested,
        [
            new FilterCollectionRequestModel
            {
                Key = "name",
                Operator = "LIKE",
                Value = "GAMMA",
                Ors = [new FilterCollectionRequestModel { Key = "name", Operator = "LIKE", Value = "alpha%BETA" }]
            }
        ]);
        Assert.Equal(["ALPHA beta", "Gamma"], await NamesAsync(nested));
    }

    /// <summary>
    /// A query of its own matches as collection search does when it marks the pattern with
    /// <see cref="NhCollectionTextMatch.Pattern"/>; an unmarked <c>LIKE</c> stays as written, so on
    /// PostgreSQL it remains case-sensitive. The SQL shows which operator each provider runs.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Marked_patterns_match_case_insensitively_and_unmarked_like_stays_as_written(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        await using var context = await CreateSeededContextAsync(database.UseNewHeapProvider);
        var text = "aLpHa";

        var marked = context.Items.AsNoTracking()
            .Where(item => EF.Functions.Like(item.Name, NhCollectionTextMatch.Pattern("%" + text + "%")));
        var unmarked = context.Items.AsNoTracking()
            .Where(item => EF.Functions.Like(item.Name, "%" + text + "%"));

        Assert.Equal(MixedCaseAlphaNames, await NamesAsync(marked));

        if (provider == RelationalTestProvider.SqlServer)
        {
            Assert.Equal(MixedCaseAlphaNames, await NamesAsync(unmarked));
            Assert.Contains(" LIKE ", marked.ToQueryString(), StringComparison.Ordinal);
            Assert.DoesNotContain("ILIKE", marked.ToQueryString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(await NamesAsync(unmarked));
            Assert.Contains(" ILIKE ", marked.ToQueryString(), StringComparison.Ordinal);
            Assert.DoesNotContain("ILIKE", unmarked.ToQueryString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The search text and the <c>LIKE</c> value are query parameters on both providers, so another
    /// text reuses the compiled query; PostgreSQL translates the match once, when it compiles.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public void Search_and_like_filter_reuse_the_compiled_query_when_the_text_changes(RelationalTestProvider provider)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var compilations = 0;
        var options = new DbContextOptionsBuilder<TextMatchDbContext>()
            .UseMemoryCache(cache)
            .LogTo(_ => compilations++, [CoreEventId.QueryCompilationStarting]);
        if (provider == RelationalTestProvider.SqlServer)
        {
            options.UseNewHeapSqlServer("Server=localhost;Database=TranslationOnly;Integrated Security=true");
        }
        else
        {
            options.UseNewHeapPostgreSql("Host=localhost;Database=translation_only");
        }

        using var context = new TextMatchDbContext(options.Options);
        var processor = new CollectionProcessingService(Substitute.For<IMapper>());
        foreach (var text in new[] { "alpha", "BETA", "Gamma" })
        {
            IQueryable<TextItem> query = context.Items;
            processor.ProcessSearch<TextItem, TextItemViewModel>(ref query, text);
            processor.ProcessFilter<TextItem, TextItemViewModel>(ref query,
                [new FilterCollectionRequestModel { Key = "name", Operator = "LIKE", Value = "%" + text + "%" }]);

            var sql = query.ToQueryString();

            Assert.Contains(provider == RelationalTestProvider.SqlServer ? " LIKE " : " ILIKE ", sql, StringComparison.Ordinal);
            Assert.Equal(1, compilations);
        }
    }

    /// <summary>
    /// The opt-out keeps PostgreSQL's case-sensitive <c>LIKE</c> for search and the <c>LIKE</c>
    /// filter. A context with the default on the same database still matches case-insensitively in
    /// the same process: the two choices do not share compiled queries.
    /// </summary>
    [Fact]
    public async Task Case_sensitive_opt_out_restores_like_on_postgresql()
    {
        var database = await ProviderTestDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        await using var caseSensitive = await CreateSeededContextAsync(
            options => options.UseNewHeapPostgreSql(
                database.ConnectionString,
                postgres => postgres.UseNewHeapCaseSensitiveCollectionTextMatch()));
        await using var caseInsensitive = CreateContext(
            options => options.UseNewHeapPostgreSql(database.ConnectionString));
        var processor = new CollectionProcessingService(Substitute.For<IMapper>());

        IQueryable<TextItem> sensitiveSearch = caseSensitive.Items.AsNoTracking();
        processor.ProcessSearch<TextItem, TextItemViewModel>(ref sensitiveSearch, "Alpha");
        Assert.Equal(["Alpha release"], await NamesAsync(sensitiveSearch));
        Assert.DoesNotContain("ILIKE", sensitiveSearch.ToQueryString(), StringComparison.Ordinal);

        IQueryable<TextItem> insensitiveSearch = caseInsensitive.Items.AsNoTracking();
        processor.ProcessSearch<TextItem, TextItemViewModel>(ref insensitiveSearch, "Alpha");
        Assert.Equal(MixedCaseAlphaNames, await NamesAsync(insensitiveSearch));

        IQueryable<TextItem> sensitiveFilter = caseSensitive.Items.AsNoTracking();
        processor.ProcessFilter<TextItem, TextItemViewModel>(ref sensitiveFilter,
            [new FilterCollectionRequestModel { Key = "name", Operator = "LIKE", Value = "%ALPHA%" }]);
        Assert.Equal(["ALPHA beta"], await NamesAsync(sensitiveFilter));
    }

    private static async Task<TextMatchDbContext> CreateSeededContextAsync(Action<DbContextOptionsBuilder> configureProvider)
    {
        var context = CreateContext(configureProvider);
        await context.Database.EnsureCreatedAsync();
        context.Items.AddRange(
            new TextItem { Name = "Alpha release", Tags = [new TextTag { Label = "routine" }] },
            new TextItem { Name = "ALPHA beta", Tags = [new TextTag { Label = "URGENT" }] },
            new TextItem { Name = "alphabet" },
            new TextItem { Name = "Gamma", Tags = [new TextTag { Label = "Urgent" }] });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return context;
    }

    private static TextMatchDbContext CreateContext(Action<DbContextOptionsBuilder> configureProvider)
    {
        var options = new DbContextOptionsBuilder<TextMatchDbContext>();
        configureProvider(options);
        return new TextMatchDbContext(options.Options);
    }

    // Ordinal order, so both providers list the names alike whatever their collation sorts first.
    private static async Task<string[]> NamesAsync(IQueryable<TextItem> query)
    {
        var names = await query.Select(item => item.Name).ToListAsync();
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class TextMatchDbContext(DbContextOptions<TextMatchDbContext> options) : DbContext(options)
    {
        public DbSet<TextItem> Items => Set<TextItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TextItem>().HasMany(item => item.Tags).WithOne();
        }
    }

    private sealed class TextItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ICollection<TextTag> Tags { get; set; } = [];
    }

    private sealed class TextTag
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    private sealed class TextItemViewModel
    {
        [Filterable] public int Id { get; set; }
        [Searchable, Filterable] public string Name { get; set; } = "";
    }
}
