using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Tests.Relational;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// A client filter key is checked segment by segment against the view model: every navigation
/// and collection step, and the member it ends on, must be [Filterable] on the view-model type
/// it belongs to. Entity members that the view model does not mark stay unreachable.
/// </summary>
public sealed class CollectionFilterPathValidationTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task NestedKeysRunOnlyThroughFilterableViewModelMembers(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var options = new DbContextOptionsBuilder<AccountDbContext>();
        database.UseNewHeapProvider(options);

        await using var context = new AccountDbContext(options.Options);
        await context.Database.EnsureCreatedAsync();
        context.Accounts.AddRange(
            new Account
            {
                Name = "first",
                Owner = new AccountOwner { Email = "first@example.test", DisplayName = "First owner", Secret = "alpha-secret" },
                Tokens = [new AccountToken { Label = "laptop", Value = "alpha-token" }]
            },
            new Account
            {
                Name = "second",
                Owner = new AccountOwner { Email = "second@example.test", DisplayName = "Second owner", Secret = "beta-secret" },
                Tokens = [new AccountToken { Label = "phone", Value = "beta-token" }]
            });
        await context.SaveChangesAsync();

        var processor = new Processor();
        foreach (var useOptions in new[] { false, true })
        {
            Assert.Equal(["first"], await FilterAsync(context, processor, useOptions,
                Filter("Owner.Email", "==", "first@example.test")));
            Assert.Equal(["second"], await FilterAsync(context, processor, useOptions,
                Filter("tokens{any}.label", "==", "phone")));
            Assert.Equal(["first"], await FilterAsync(context, processor, useOptions,
                Filter("Name", "==", "missing", ors: [Filter("Tokens{all}.Label", "LIKE", "lap%")])));

            await AssertRejectedAsync(context, processor, useOptions, Filter("Owner.Secret", "LIKE", "alpha%"));
            await AssertRejectedAsync(context, processor, useOptions, Filter("Tokens{any}.Value", "LIKE", "alpha%"));
            await AssertRejectedAsync(context, processor, useOptions, Filter("Tokens.Value", "==", "alpha-token"));
            await AssertRejectedAsync(context, processor, useOptions, Filter("Owner.Id", "==", "1"));
            await AssertRejectedAsync(context, processor, useOptions,
                Filter("Name", "==", "first", ands: [Filter("Owner.Secret", "==", "alpha-secret")]));
            await AssertRejectedAsync(context, processor, useOptions,
                Filter("Name", "==", "first", ors: [Filter("Name", "==", "second", ands: [Filter("Tokens{any}.Value", "==", "beta-token")])]));
        }

        // A configured selector is an explicit allowlist entry for its exact key, also inside an OR group.
        IQueryable<Account> configured = context.Accounts.AsNoTracking();
        processor.ProcessWithOptions(ref configured,
            [Filter("Name", "==", "missing", ors: [Filter("owner-name", "LIKE", "Second%")])],
            builder => builder.FilterableFromAttributes().WithFilterable("owner-name", account => account.Owner!.DisplayName));
        Assert.Equal(["second"], await configured.Select(account => account.Name).ToListAsync());

        IQueryable<Account> configuredPrefix = context.Accounts.AsNoTracking();
        Assert.Throws<InvalidFilterCollectionResultException>(() => processor.ProcessWithOptions(ref configuredPrefix,
            [Filter("owner-name.Length", "==", "6")],
            builder => builder.WithFilterable("owner-name", account => account.Owner!.DisplayName)));
    }

    [Fact]
    public void DivisionUserFiltersCannotReachIdentityColumns()
    {
        var owner = new NhUser { Email = "owner@example.test", PasswordHash = "hash", SecurityStamp = "stamp" };
        owner.AuthRefreshTokens.Add(new NhUserAuthRefreshToken { Token = "refresh-token" });
        var divisionUsers = new[] { new NhDivisionUser { User = owner, Division = new NhDivision { Name = "Division" } } };
        var processor = new Processor();

        foreach (var key in new[] { "User.PasswordHash", "User.SecurityStamp", "User.AuthRefreshTokens{any}.Token", "User.AuthRefreshTokens.Token", "Division.Description" })
        {
            foreach (var useOptions in new[] { false, true })
            {
                var query = divisionUsers.AsQueryable();
                Assert.Throws<InvalidFilterCollectionResultException>(() =>
                    processor.Process<NhDivisionUser, TestDivisionUserViewModel>(ref query, [Filter(key, "==", "x")], useOptions));
            }
        }

        foreach (var (key, value) in new[] { ("User.Email", "owner@example.test"), ("Division.Name", "Division") })
        {
            foreach (var useOptions in new[] { false, true })
            {
                var query = divisionUsers.AsQueryable();
                processor.Process<NhDivisionUser, TestDivisionUserViewModel>(ref query, [Filter(key, "==", value)], useOptions);
                Assert.Single(query);
            }
        }
    }

    [Fact]
    public void QuantifierOfEachCollectionStepAppliesToThatStep()
    {
        var accounts = new[]
        {
            new Account
            {
                Name = "mixed",
                Tokens =
                [
                    new AccountToken { Label = "a", Scopes = [new AccountTokenScope { Name = "read" }] },
                    new AccountToken { Label = "b", Scopes = [new AccountTokenScope { Name = "write" }] }
                ]
            },
            new Account
            {
                Name = "uniform",
                Tokens = [new AccountToken { Label = "c", Scopes = [new AccountTokenScope { Name = "read" }] }]
            }
        };
        var processor = new Processor();

        foreach (var useOptions in new[] { false, true })
        {
            var all = accounts.AsQueryable();
            processor.Process<Account, AccountViewModel>(ref all, [Filter("Tokens{all}.Scopes{any}.Name", "==", "read")], useOptions);
            Assert.Equal(["uniform"], all.Select(account => account.Name));

            var any = accounts.AsQueryable();
            processor.Process<Account, AccountViewModel>(ref any, [Filter("Tokens{any}.Scopes{any}.Name", "==", "read")], useOptions);
            Assert.Equal(["mixed", "uniform"], any.Select(account => account.Name));

            var none = accounts.AsQueryable();
            processor.Process<Account, AccountViewModel>(ref none, [Filter("Tokens{!any}.Scopes{any}.Name", "==", "write")], useOptions);
            Assert.Equal(["uniform"], none.Select(account => account.Name));
        }
    }

    private static async Task<List<string>> FilterAsync(
        AccountDbContext context,
        Processor processor,
        bool useOptions,
        FilterCollectionRequestModel filter)
    {
        IQueryable<Account> query = context.Accounts.AsNoTracking();
        processor.Process<Account, AccountViewModel>(ref query, [filter], useOptions);

        return await query.OrderBy(account => account.Name).Select(account => account.Name).ToListAsync();
    }

    private static async Task AssertRejectedAsync(
        AccountDbContext context,
        Processor processor,
        bool useOptions,
        FilterCollectionRequestModel filter)
    {
        await Assert.ThrowsAsync<InvalidFilterCollectionResultException>(() => FilterAsync(context, processor, useOptions, filter));
    }

    private static FilterCollectionRequestModel Filter(
        string key,
        string @operator,
        object? value,
        List<FilterCollectionRequestModel>? ands = null,
        List<FilterCollectionRequestModel>? ors = null)
    {
        return new FilterCollectionRequestModel
        {
            Key = key,
            Operator = @operator,
            Value = value,
            Ands = ands ?? [],
            Ors = ors ?? []
        };
    }

    private sealed class Processor() : CollectionProcessingService(Substitute.For<IMapper>())
    {
        public void Process<TEntity, TViewModel>(
            ref IQueryable<TEntity> query,
            List<FilterCollectionRequestModel> filters,
            bool useOptions)
            where TEntity : class
            where TViewModel : class
        {
            if (useOptions)
            {
                ProcessFilter(ref query, filters, CollectionProcessingOptions<TEntity, TViewModel>.Default);
            }
            else
            {
                ProcessFilter<TEntity, TViewModel>(ref query, filters);
            }
        }

        public void ProcessWithOptions(
            ref IQueryable<Account> query,
            List<FilterCollectionRequestModel> filters,
            Action<CollectionProcessingOptionsBuilder<Account, AccountViewModel>> configure)
        {
            ProcessFilter(ref query, filters, CreateCollectionProcessingOptions(configure));
        }
    }

    private sealed class TestDivisionUserViewModel
        : DivisionUserViewModel<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhDivisionRoleViewModel>
    {
    }

    private sealed class AccountDbContext(DbContextOptions<AccountDbContext> options) : DbContext(options)
    {
        public DbSet<Account> Accounts => Set<Account>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Account>().HasOne(account => account.Owner).WithMany();
            modelBuilder.Entity<Account>().HasMany(account => account.Tokens).WithOne();
            modelBuilder.Entity<AccountToken>().HasMany(token => token.Scopes).WithOne();
        }
    }

    private sealed class Account
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public AccountOwner? Owner { get; set; }
        public ICollection<AccountToken> Tokens { get; set; } = [];
    }

    private sealed class AccountOwner
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Secret { get; set; } = "";
    }

    private sealed class AccountToken
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
        public ICollection<AccountTokenScope> Scopes { get; set; } = [];
    }

    private sealed class AccountTokenScope
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class AccountViewModel
    {
        [Filterable] public int Id { get; set; }
        [Filterable] public string Name { get; set; } = "";
        [Filterable] public AccountOwnerViewModel? Owner { get; set; }
        [Filterable] public List<AccountTokenViewModel> Tokens { get; set; } = [];
    }

    private sealed class AccountOwnerViewModel
    {
        public int Id { get; set; }
        [Filterable] public string Email { get; set; } = "";
    }

    private sealed class AccountTokenViewModel
    {
        [Filterable] public string Label { get; set; } = "";
        [Filterable] public List<AccountTokenScopeViewModel> Scopes { get; set; } = [];
    }

    private sealed class AccountTokenScopeViewModel
    {
        [Filterable] public string Name { get; set; } = "";
    }
}
