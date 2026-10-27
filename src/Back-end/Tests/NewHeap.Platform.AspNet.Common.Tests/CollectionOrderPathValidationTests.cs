using System.ComponentModel;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Tests.Relational;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// A client order key is checked segment by segment against the view model, like a filter key: every
/// navigation and collection step, and the member it ends on, must be [Orderable] on the view-model type
/// it belongs to, and nothing else of the key reaches Dynamic LINQ. A key that is not such a path, such as
/// a conditional over entity columns behind an [Orderable] date, is skipped like an unknown key, so the
/// rows keep the default order instead of an order the database derived from hidden columns.
/// </summary>
public sealed class CollectionOrderPathValidationTests
{
    private static readonly string[] IgnoredKeys =
    [
        "creationDateTime.year == 2001 ? secret.length : 0",
        "creationDateTime.year == 2001 ? owner.secret.length : 0",
        "creationDateTime.year == 2001 ? (secret > \"m\" ? 1 : 0) : 0",
        "id.compareTo(0)",
        "secret",
        "name.length",
        "owner.secret",
        "owner. displayName",
        "tokens",
        "tokens.label",
        "tokens.{first:asc}value",
        "tokens.{first:asc}label.length",
        "{first:asc}name",
        "owner.{first:asc}displayName",
        "name + secret",
        "name +",
        "tokens.{first:ASC}label",
        "tokens.{First:asc}label"
    ];

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task OrderKeysRunOnlyThroughOrderableViewModelMembers(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var options = new DbContextOptionsBuilder<AccountDbContext>();
        database.UseNewHeapProvider(options);

        await using var context = new AccountDbContext(options.Options);
        await context.Database.EnsureCreatedAsync();
        var created = new DateTimeOffset(2001, 5, 1, 0, 0, 0, TimeSpan.Zero);
        context.Accounts.AddRange(
            new Account
            {
                Name = "a", CreationDateTime = created, Secret = "zz-long-secret",
                Owner = new AccountOwner { DisplayName = "x", Secret = "y" },
                Tokens = [new AccountToken { Label = "q", Value = "alpha" }]
            },
            new Account
            {
                Name = "b", CreationDateTime = created, Secret = "aaa",
                Owner = new AccountOwner { DisplayName = "z", Secret = "zzzzzzzzzz" },
                Tokens = [new AccountToken { Label = "c", Value = "beta" }]
            },
            new Account
            {
                Name = "c", CreationDateTime = created, Secret = "mm-mid",
                Owner = new AccountOwner { DisplayName = "y", Secret = "q" },
                Tokens = [new AccountToken { Label = "m", Value = "gamma" }]
            });
        await context.SaveChangesAsync();

        var processor = new Processor();
        foreach (var useOptions in new[] { false, true })
        {
            Assert.Equal(["c", "b", "a"], await OrderAsync(context, processor, useOptions, Order("Name", "DESC")));
            Assert.Equal(["a", "c", "b"], await OrderAsync(context, processor, useOptions, Order("owner.displayName")));
            Assert.Equal(["b", "c", "a"], await OrderAsync(context, processor, useOptions,
                Order("Owner.DisplayName + Name", "DESC")));
            Assert.Equal(3, (await OrderAsync(context, processor, useOptions, Order("tokens.{first:asc}label"))).Count);
            Assert.Equal(["c", "b", "a"], await OrderAsync(context, processor, useOptions,
                Order("creationDateTime"), Order("name", "DESC")));

            // Every ordering an ignored key could produce differs from the default order c, b, a.
            foreach (var key in IgnoredKeys)
            {
                IQueryable<Account> query = context.Accounts.AsNoTracking();
                var applied = processor.Process<Account, AccountViewModel>(ref query, [Order(key)], useOptions,
                    (account => account.Name, ListSortDirection.Descending));

                Assert.Equal(["Name"], applied.Select(order => order.Key));
                Assert.Equal(["c", "b", "a"], await query.Select(account => account.Name).ToListAsync());
            }

            // An ignored key leaves the allowed keys of the same request in effect.
            IQueryable<Account> mixed = context.Accounts.AsNoTracking();
            var mixedApplied = processor.Process<Account, AccountViewModel>(ref mixed,
                [Order("creationDateTime.year == 2001 ? secret.length : 0"), Order("name")], useOptions,
                (account => account.Name, ListSortDirection.Descending));
            Assert.Equal(["name"], mixedApplied.Select(order => order.Key));
            Assert.Equal(["a", "b", "c"], await mixed.Select(account => account.Name).ToListAsync());
        }

        // A configured selector is an explicit allowlist entry for its exact key; a tail behind it is not.
        IQueryable<Account> configured = context.Accounts.AsNoTracking();
        processor.ProcessWithOptions(ref configured, [Order("owner-secret", "DESC")],
            builder => builder.OrderableFromAttributes().WithOrderable("owner-secret", account => account.Owner!.Secret));
        Assert.Equal(["b", "a", "c"], await configured.Select(account => account.Name).ToListAsync());

        IQueryable<Account> configuredTail = context.Accounts.AsNoTracking();
        processor.ProcessWithOptions(ref configuredTail, [Order("owner-secret.length")],
            builder => builder.WithOrderable("owner-secret", account => account.Owner!.Secret),
            (account => account.Name, ListSortDirection.Descending));
        Assert.Equal(["c", "b", "a"], await configuredTail.Select(account => account.Name).ToListAsync());
    }

    [Fact]
    public void DivisionUserOrderingCannotReachIdentityColumns()
    {
        var created = new DateTimeOffset(2001, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var divisionUsers = new[]
        {
            new NhDivisionUser
            {
                User = new NhUser { Email = "b@example.test", PasswordHash = "a", CreationDateTime = created },
                Division = new NhDivision { Name = "North" }
            },
            new NhDivisionUser
            {
                User = new NhUser { Email = "a@example.test", PasswordHash = "zz", CreationDateTime = created },
                Division = new NhDivision { Name = "South" }
            }
        };
        var processor = new Processor();

        foreach (var useOptions in new[] { false, true })
        {
            // The default order puts the a@ user first; each ignored key would put the b@ user first.
            foreach (var key in new[]
                     {
                         "User.CreationDateTime.Year == 2001 ? User.PasswordHash.Length : 0",
                         "User.Id == null ? 0 : 1",
                         "User.PasswordHash",
                         "User.SecurityStamp",
                         "LockOutStartDateTime.Value.Year",
                         "Division.Description",
                         "Roles.{first:asc}Name"
                     })
            {
                var query = divisionUsers.AsQueryable();
                var applied = processor.Process<NhDivisionUser, TestDivisionUserViewModel>(ref query, [Order(key)],
                    useOptions, (divisionUser => divisionUser.User.Email!, ListSortDirection.Ascending));

                Assert.DoesNotContain(applied, order => order.Key == key);
                Assert.Equal("a@example.test", query.First().User.Email);
            }

            foreach (var (key, first) in new[] { ("User.Email", "a@example.test"), ("Division.Name", "b@example.test") })
            {
                var query = divisionUsers.AsQueryable();
                processor.Process<NhDivisionUser, TestDivisionUserViewModel>(ref query, [Order(key)], useOptions);
                Assert.Equal(first, query.First().User.Email);
            }
        }
    }

    [Fact]
    public void AViewModelWithoutOrderableMembersKeepsIgnoringOrderKeys()
    {
        var processor = new Processor();

        foreach (var useOptions in new[] { false, true })
        {
            var query = new[] { new Account { Name = "a" } }.AsQueryable();
            var applied = processor.Process<Account, UnorderableAccountViewModel>(ref query, [Order("secret")], useOptions,
                (account => account.Id, ListSortDirection.Ascending));

            Assert.Equal(["Id"], applied.Select(order => order.Key));
        }
    }

    private static async Task<List<string>> OrderAsync(
        AccountDbContext context,
        Processor processor,
        bool useOptions,
        params OrderByCollectionRequestModel[] orderBy)
    {
        IQueryable<Account> query = context.Accounts.AsNoTracking();
        processor.Process<Account, AccountViewModel>(ref query, [.. orderBy], useOptions);

        return await query.Select(account => account.Name).ToListAsync();
    }

    private static OrderByCollectionRequestModel Order(string key, string direction = "ASC")
    {
        return new OrderByCollectionRequestModel { Key = key, Direction = direction };
    }

    private sealed class Processor() : CollectionProcessingService(Substitute.For<IMapper>())
    {
        public List<OrderByCollectionRequestModel> Process<TEntity, TViewModel>(
            ref IQueryable<TEntity> query,
            List<OrderByCollectionRequestModel> orderBy,
            bool useOptions,
            params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
            where TEntity : class
            where TViewModel : class
        {
            if (useOptions)
            {
                return ProcessOrderBy(ref query, orderBy, CollectionProcessingOptions<TEntity, TViewModel>.Default,
                    [.. defaultOrderBy]);
            }

            return ProcessOrderBy<TEntity, TViewModel>(ref query, orderBy, defaultOrderBy);
        }

        public void ProcessWithOptions(
            ref IQueryable<Account> query,
            List<OrderByCollectionRequestModel> orderBy,
            Action<CollectionProcessingOptionsBuilder<Account, AccountViewModel>> configure,
            params (Expression<Func<Account, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        {
            ProcessOrderBy(ref query, orderBy, CreateCollectionProcessingOptions(configure), [.. defaultOrderBy]);
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
        }
    }

    private sealed class Account
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTimeOffset CreationDateTime { get; set; }
        public string Secret { get; set; } = "";
        public AccountOwner? Owner { get; set; }
        public ICollection<AccountToken> Tokens { get; set; } = [];
    }

    private sealed class AccountOwner
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = "";
        public string Secret { get; set; } = "";
    }

    private sealed class AccountToken
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private sealed class AccountViewModel
    {
        [Orderable] public int Id { get; set; }
        [Orderable] public string Name { get; set; } = "";
        [Orderable] public DateTimeOffset CreationDateTime { get; set; }
        [Orderable] public AccountOwnerViewModel? Owner { get; set; }
        [Orderable] public List<AccountTokenViewModel> Tokens { get; set; } = [];
    }

    private sealed class AccountOwnerViewModel
    {
        [Orderable] public string DisplayName { get; set; } = "";
    }

    private sealed class AccountTokenViewModel
    {
        [Orderable] public string Label { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private sealed class UnorderableAccountViewModel
    {
        public int Id { get; set; }
    }
}
