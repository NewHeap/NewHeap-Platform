using System.ComponentModel;
using System.Data.Common;
using System.Linq.Expressions;
using System.Security.Claims;
using MessagePack;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Resolvers;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class FieldSelectionTests
{
    private static readonly Expression<Func<Row, View>> Projection = row => new View
    {
        Id = row.Id, Name = row.Name, Secret = row.Secret, Amount = row.Amount,
        Enabled = row.Enabled, Date = row.Date, Optional = row.Optional, Alias = row.Name
    };

    [Theory]
    [InlineData("")]
    [InlineData("?format=messagepack")]
    [InlineData("?format=unknown")]
    public void LegacyRequestsDoNotOptIn(string query)
    {
        Assert.False(NhFieldSelectionService.HasSelection(Context(query).Request));
    }

    [Fact]
    public async Task SelectionPreservesFalsyValuesAliasesAndEmptyPageMetadata()
    {
        var service = Service();
        var context = Context("?fields=identifier,name,enabled,optional,identifier");
        var result = await service.GetCollectionAsync(context, new CollectionRequestModel(), Rows(), Projection);
        Assert.True(result.Success);
        Assert.Equal(["identifier", "name", "enabled", "optional"], result.Data!.Selection.ReturnedFields);
        Assert.Equal(5, result.Data.Selection.RequestedFields.Count);
        Assert.Equal(false, result.Data.Items[0]["enabled"]);
        Assert.Null(result.Data.Items[0]["optional"]);
        Assert.DoesNotContain("secret", result.Data.Items[0].Keys);

        var empty = await service.GetCollectionAsync(context, new CollectionRequestModel { Page = 100 }, Rows(), Projection);
        Assert.Empty(empty.Data!.Items);
        Assert.Equal(result.Data.Selection.ReturnedFields, empty.Data.Selection.ReturnedFields);
    }

    [Theory]
    [InlineData("?fields=")]
    [InlineData("?fields=name,")]
    [InlineData("?fields=secret")]
    [InlineData("?fields=amount")]
    [InlineData("?fields=unknown")]
    [InlineData("?fields=name&format=xml")]
    [InlineData("?fields=name&filter=invalid")]
    [InlineData("?fields=name&filter=%5B42%5D")]
    [InlineData("?fields=name&orderBy=%7B%7D")]
    public async Task InvalidOrForbiddenSelectionFailsBeforeQueryExecution(string query)
    {
        var collections = Substitute.For<ICollectionProcessingService>();
        var result = await Service(collections).GetCollectionAsync(Context(query), new CollectionRequestModel(), Rows(), Projection);
        Assert.False(result.Success);
        Assert.Empty(collections.ReceivedCalls());
    }

    [Theory]
    [InlineData("Finance")]
    [InlineData("Administrator")]
    public async Task RolesAreOrAndPolicyUsesTheSuppliedResource(string role)
    {
        var service = Service();
        var context = Context("?fields=secret,amount", role);
        context.User.AddIdentity(new ClaimsIdentity([new Claim("division.finance", "division-a")]));
        var allowed = await service.GetCollectionAsync(context, new CollectionRequestModel(), Rows(), Projection, "division-a");
        Assert.True(allowed.Success);
        var denied = await service.GetCollectionAsync(context, new CollectionRequestModel(), Rows(), Projection, "division-b");
        Assert.False(denied.Success);
        var metadata = await service.DescribeAsync<View>(context.User, "division-b");
        Assert.Contains(metadata.Fields, field => field.Name == "secret");
        Assert.DoesNotContain(metadata.Fields, field => field.Name == "amount");
        Assert.DoesNotContain((await service.DescribeAsync<View>(Context("").User)).Fields, field => field.Name == "secret");
    }

    [Fact]
    public async Task DeniedFieldsCannotBeFilteredSortedOrSearched()
    {
        var service = Service();
        var context = Context("?fields=name");
        var filter = new CollectionRequestModel
        {
            Filter = [new() { Key = "name", Operator = "==", Value = "visible", Ors = [new() { Key = "secret", Operator = "==", Value = "private" }] }]
        };
        Assert.False((await service.GetCollectionAsync(context, filter, Rows(), Projection)).Success);
        Assert.False((await service.GetCollectionAsync(context, new CollectionRequestModel
        {
            OrderBy = [new() { Key = "secret" }]
        }, Rows(), Projection)).Success);
    }

    [Fact]
    public async Task JsonAndMessagePackHaveEquivalentBrowserContracts()
    {
        var options = JsonOptions();
        var context = Context("?fields=identifier,name,enabled,optional,date,amount,uppername&format=messagepack");
        context.User.AddIdentity(new ClaimsIdentity([new Claim("division.finance", "division-a")]));
        var result = await Service().GetCollectionAsync(context, new CollectionRequestModel(), Rows(), Projection, "division-a");
        Assert.True(result.Success);
        var json = JToken.Parse(JsonConvert.SerializeObject(result.Data, options.Value.SerializerSettings));
        Assert.Equal("visible", json["items"]![0]!["UPPERName"]!.Value<string>());
        Assert.Contains("UPPERName", result.Data!.Selection.ReturnedFields);
        var bytes = NhSelectedCollectionMessagePack.Serialize(result.Data!, options.Value.SerializerSettings);
        var unpacked = JToken.Parse(MessagePackSerializer.ConvertToJson(bytes));
        Assert.True(JToken.DeepEquals(json, unpacked), $"JSON: {json}\nMessagePack: {unpacked}");
        Assert.IsType<FileContentResult>(Service().ToActionResult(result));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteNumbersFollowTheJsonStringContract(double value)
    {
        var result = new NhSelectedCollectionResult
        {
            Selection = new NhCollectionSelection(["number"], ["number"], "messagepack"),
            Items = [new Dictionary<string, object?> { ["number"] = value }]
        };
        var settings = JsonOptions().Value.SerializerSettings;
        var expected = JToken.Parse(JsonConvert.SerializeObject(result, settings));
        var actual = JToken.Parse(MessagePackSerializer.ConvertToJson(NhSelectedCollectionMessagePack.Serialize(result, settings)));
        Assert.True(JToken.DeepEquals(expected, actual));
    }

    [Theory]
    [InlineData("sql-server")]
    [InlineData("postgresql")]
    public async Task SelectionTranslatesWithoutLoadingOtherColumns(string provider)
    {
        var options = new DbContextOptionsBuilder<Database>();
        if (provider == "sql-server")
        {
            options.UseSqlServer("Server=localhost;Database=TranslationOnly;Integrated Security=true");
        }
        else
        {
            options.UseNpgsql("Host=localhost;Database=translation_only");
        }
        await using var db = new Database(options.Options);
        var capture = new TranslationOnlyCollections();
        var request = new CollectionRequestModel
        {
            Search = "visible", ItemsPerPage = 10,
            Filter = [new() { Key = "identifier", Operator = ">", Value = 0 }],
            OrderBy = [new() { Key = "name", Direction = "ASC" }]
        };
        var result = await Service(capture).GetCollectionAsync(Context("?fields=identifier,name"), request, db.Rows, Projection);
        Assert.True(result.Success);
        Assert.Contains("WHERE", capture.Sql);
        Assert.Contains("ORDER BY", capture.Sql);
        Assert.Contains("LIKE", capture.Sql);
        Assert.DoesNotContain("Secret", capture.Sql);
        Assert.DoesNotContain("Amount", capture.Sql);
        Assert.DoesNotContain("Date", capture.Sql);
    }

    [Theory]
    [InlineData("sql-server")]
    [InlineData("postgresql")]
    public async Task ProjectionExecutesOnRelationalProviderWithoutSelectingSecret(string provider)
    {
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await using var postgres = new PostgreSqlBuilder("postgres:15.1").Build();
        var capture = new Capture();
        var options = new DbContextOptionsBuilder<Database>().AddInterceptors(capture);
        if (provider == "sql-server")
        {
            await sql.StartAsync();
            options.UseSqlServer(sql.GetConnectionString());
        }
        else
        {
            await postgres.StartAsync();
            options.UseNpgsql(postgres.GetConnectionString());
        }
        await using var db = new Database(options.Options);
        await db.Database.EnsureCreatedAsync();
        var row = Rows().Single();
        row.Id = 0;
        db.Rows.Add(row);
        await db.SaveChangesAsync();
        capture.Commands.Clear();
        var result = await Service().GetCollectionAsync(Context("?fields=identifier,name"),
            new CollectionRequestModel { ItemsPerPage = 1, Search = "visible" }, db.Rows, Projection,
            defaultOrderBy: [(row => row.Id, ListSortDirection.Ascending)]);
        Assert.True(result.Success);
        Assert.Single(result.Data!.Items);
        Assert.Equal(2, capture.Commands.Count);
        Assert.All(capture.Commands, command => Assert.DoesNotContain("Secret", command));
        Assert.DoesNotContain("Amount", capture.Commands.Last());
        Assert.DoesNotContain("Date", capture.Commands.Last());

        var secretSearch = await Service().GetCollectionAsync(Context("?fields=name"),
            new CollectionRequestModel { Search = "private" }, db.Rows, Projection,
            defaultOrderBy: [(row => row.Id, ListSortDirection.Ascending)]);
        Assert.Equal(0, secretSearch.Data!.TotalCount);
    }

    private static IQueryable<Row> Rows()
    {
        return new[] { new Row { Id = 1, Name = "visible", Secret = "private", Amount = 123.45m, Date = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero) } }.AsQueryable();
    }

    private static DefaultHttpContext Context(string query, string? role = null)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(role is null ? [] : [new Claim(ClaimTypes.Role, role)], "test"));
        return context;
    }

    private static IOptions<MvcNewtonsoftJsonOptions> JsonOptions()
    {
        var options = new MvcNewtonsoftJsonOptions();
        new MvcNewtonsoftJsonOptionsWrapper().Configure(options);
        return Options.Create(options);
    }

    private static NhFieldSelectionService Service(ICollectionProcessingService? collections = null)
    {
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(call => Task.FromResult(call.ArgAt<ClaimsPrincipal>(0).HasClaim(call.ArgAt<string>(2), call.ArgAt<object?>(1)?.ToString() ?? "")
                ? AuthorizationResult.Success() : AuthorizationResult.Failed()));
        return new NhFieldSelectionService(collections ?? new CollectionProcessingService(Substitute.For<IMapper>()), auth, JsonOptions());
    }

    public sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Secret { get; set; } = "";
        public decimal Amount { get; set; }
        public bool Enabled { get; set; }
        public string? Optional { get; set; }
        public DateTimeOffset Date { get; set; }
    }

    public sealed class View
    {
        [Selectable, JsonProperty("UPPERName")]
        public string Alias { get; set; } = "";
        [Selectable, Filterable, Orderable, JsonProperty("identifier")]
        public int Id { get; set; }
        [Selectable, Filterable, Orderable, Searchable]
        public string Name { get; set; } = "";
        [Selectable, Filterable, Orderable, Searchable, FieldAccess(Roles = "Finance,Administrator")]
        public string Secret { get; set; } = "";
        [Selectable, FieldAccess(Policy = "division.finance")]
        public decimal Amount { get; set; }
        [Selectable]
        public bool Enabled { get; set; }
        [Selectable]
        public string? Optional { get; set; }
        [Selectable]
        public DateTimeOffset Date { get; set; }
    }

    private sealed class Database(DbContextOptions<Database> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Row>().Property(row => row.Amount).HasPrecision(18, 2);
        }
    }

    private sealed class Capture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    // Execute the real query composition and selection callback, but translate without opening a connection.
    // The separate Testcontainers tests prove command execution, counts and materialization.
    private sealed class TranslationOnlyCollections() : CollectionProcessingService(Substitute.For<IMapper>())
    {
        public string Sql { get; private set; } = "";

        public override async Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
            ICollectionRequestModel requestModel, IQueryable<TEntity> queryable,
            Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions,
            Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
            bool asNoTracking = true, CancellationToken cancellationToken = default,
            params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        {
            var options = CreateCollectionProcessingOptions(configureOptions);
            ProcessSearch(ref queryable, requestModel.Search, options);
            ProcessFilter(ref queryable, requestModel.Filter, options);
            ProcessOrderBy(ref queryable, requestModel.OrderBy, options, defaultOrderBy.ToList());
            queryable = queryable.Skip(0).Take(requestModel.ItemsPerPage);
            queryable = await resultQueryableFunc!(queryable, cancellationToken);
            Sql = queryable.ToQueryString();
            return new CollectionResultModel<TViewModel> { Items = [] };
        }
    }
}
