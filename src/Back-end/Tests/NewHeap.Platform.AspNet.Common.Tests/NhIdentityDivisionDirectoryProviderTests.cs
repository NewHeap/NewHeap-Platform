using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services.Actors;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.Common.Actors;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// NewHeap Identity lists its divisions by name, within the limit and in a stable order, and finds
/// divisions by id, on both relational providers. Without NewHeap Identity or an application directory
/// nothing lists divisions.
/// </summary>
public sealed class NhIdentityDivisionDirectoryProviderTests
{
    private static readonly Guid North = Guid.Parse("a1000000-0000-0000-0000-0000000000a1");
    private static readonly Guid South = Guid.Parse("b2000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Central = Guid.Parse("c3000000-0000-0000-0000-0000000000c3");
    private static readonly Guid Unnamed = Guid.Parse("d4000000-0000-0000-0000-0000000000d4");

    [Fact]
    public async Task DivisionsAreListedByNameAndFoundByIdOnBothRelationalProviders()
    {
        await using (var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build())
        {
            await sqlServer.StartAsync();
            await VerifyAsync(options => options.UseNewHeapSqlServer(sqlServer.GetConnectionString()));
        }

        await using (var postgreSql = new PostgreSqlBuilder("postgres:15.1").Build())
        {
            await postgreSql.StartAsync();
            await VerifyAsync(options => options.UseNewHeapPostgreSql(postgreSql.GetConnectionString()));
        }
    }

    [Fact]
    public void NewHeapIdentityRegistersTheDirectoryUnlessTheApplicationHasItsOwnAndNothingElseDoes()
    {
        var identity = new ServiceCollection();
        NhPlatformModuleRegistration.AddIdentityActorDefaults<NhUser, NhDivision>(identity);
        Assert.Equal(
            typeof(NhIdentityDivisionDirectory<NhDivision>),
            identity.Single(descriptor => descriptor.ServiceType == typeof(INhDivisionDirectory)).ImplementationType);

        var applicationFirst = new ServiceCollection();
        applicationFirst.AddNewHeapDivisionDirectory<ApplicationDirectory>();
        NhPlatformModuleRegistration.AddIdentityActorDefaults<NhUser, NhDivision>(applicationFirst);
        Assert.Equal(
            typeof(ApplicationDirectory),
            applicationFirst.Single(descriptor => descriptor.ServiceType == typeof(INhDivisionDirectory)).ImplementationType);

        var applicationLast = new ServiceCollection();
        NhPlatformModuleRegistration.AddIdentityActorDefaults<NhUser, NhDivision>(applicationLast);
        applicationLast.AddNewHeapDivisionDirectory<ApplicationDirectory>();
        Assert.Equal(
            typeof(ApplicationDirectory),
            applicationLast.Single(descriptor => descriptor.ServiceType == typeof(INhDivisionDirectory)).ImplementationType);

        // Divisions are optional: the generic defaults list none.
        var standalone = new ServiceCollection();
        NhPlatformModuleRegistration.AddGenericActorDefaults(standalone);
        Assert.DoesNotContain(standalone, descriptor => descriptor.ServiceType == typeof(INhDivisionDirectory));
    }

    private static async Task VerifyAsync(Action<DbContextOptionsBuilder> configureProvider)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DirectoryDbContext>(configureProvider);
        services.AddScoped<IRepository<NhDivision>>(provider =>
            new Repository<NhDivision>(provider.GetRequiredService<DirectoryDbContext>(), provider));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DirectoryDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Set<NhDivision>().AddRange(
                Division(South, "South"),
                Division(North, "North"),
                Division(Central, "Central"),
                Division(Unnamed, " "));
            await context.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var directory = new NhIdentityDivisionDirectory<NhDivision>(scope.ServiceProvider.GetRequiredService<IRepository<NhDivision>>());

            // By name, then by id; a division without a name shows its id.
            var all = await directory.ListAsync(NhDivisionInfo.MaxListLimit);
            Assert.Equal([Unnamed, Central, North, South], all.Select(division => division.Id));
            Assert.Equal(Unnamed.ToString("D"), all[0].Name);
            Assert.Equal([Unnamed, Central], (await directory.ListAsync(2)).Select(division => division.Id));
            // A limit outside the range is brought within it.
            Assert.Single(await directory.ListAsync(0));

            var found = await directory.GetAsync([North, South, Guid.NewGuid()]);
            Assert.Equal(new[] { North, South }.Order(), found.Keys.Order());
            Assert.Equal("North", found[North].Name);
            Assert.Empty(await directory.GetAsync([]));
        }
    }

    private static NhDivision Division(Guid id, string name)
    {
        return new NhDivision
        {
            Id = id,
            Name = name,
            Description = name,
            TimeZoneId = "UTC"
        };
    }

    private sealed class ApplicationDirectory : INhDivisionDirectory
    {
        public Task<IReadOnlyList<NhDivisionInfo>> ListAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NhDivisionInfo>>([]);
        }

        public Task<IReadOnlyDictionary<Guid, NhDivisionInfo>> GetAsync(IReadOnlyCollection<Guid> divisionIds, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, NhDivisionInfo>>(new Dictionary<Guid, NhDivisionInfo>());
        }
    }

    private sealed class DirectoryDbContext(DbContextOptions<DirectoryDbContext> options) : NhIdentityDbContext(options);
}
