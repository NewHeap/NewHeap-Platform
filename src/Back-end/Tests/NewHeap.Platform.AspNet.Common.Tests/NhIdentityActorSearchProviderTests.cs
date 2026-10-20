using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.Actors;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// NewHeap Identity's actor search finds users by part of their user name or e-mail address,
/// ignoring case, leaves out locked-out users and other actor kinds, and stays within the limit, on
/// both relational providers.
/// </summary>
public sealed class NhIdentityActorSearchProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task UsersAreFoundByNameOrEmailOnEachRelationalProvider(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        await VerifyAsync(database.UseNewHeapProvider);
    }

    [Fact]
    public void NewHeapIdentityRegistersTheSearchUnlessTheApplicationHasItsOwn()
    {
        var identity = new ServiceCollection();
        NhPlatformModuleRegistration.AddIdentityActorDefaults<NhUser, NhDivision>(identity);
        Assert.Equal(
            typeof(NhIdentityActorSearch<NhUser>),
            identity.Single(descriptor => descriptor.ServiceType == typeof(INhActorSearch)).ImplementationType);

        var application = new ServiceCollection();
        application.AddNewHeapActorSearch<ApplicationSearch>();
        NhPlatformModuleRegistration.AddIdentityActorDefaults<NhUser, NhDivision>(application);
        Assert.Equal(
            typeof(ApplicationSearch),
            application.Single(descriptor => descriptor.ServiceType == typeof(INhActorSearch)).ImplementationType);
    }

    private static async Task VerifyAsync(Action<DbContextOptionsBuilder> configureProvider)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SearchDbContext>(configureProvider);
        services.AddScoped<IRepository<NhUser>>(provider =>
            new Repository<NhUser>(provider.GetRequiredService<SearchDbContext>(), provider));
        await using var provider = services.BuildServiceProvider();

        var operations = Guid.NewGuid();
        var lead = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Users.AddRange(
                User(operations, "operations", "operations@example.test"),
                User(lead, "project-lead", "lead@example.test"),
                User(Guid.NewGuid(), "locked-operator", "locked@example.test", lockedOut: true),
                User(Guid.NewGuid(), "viewer", "viewer@example.test"));
            await context.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var search = new NhIdentityActorSearch<NhUser>(scope.ServiceProvider.GetRequiredService<IRepository<NhUser>>());

            var byName = await search.SearchAsync(new NhActorSearchRequest("OPERAT"));
            Assert.Equal([NhActorRef.ForUser(operations)], byName.Select(item => item.Actor));
            Assert.Equal(("operations", "operations@example.test"), (byName[0].DisplayName, byName[0].Email));

            var byEmail = await search.SearchAsync(new NhActorSearchRequest("lead@exa"));
            Assert.Equal([NhActorRef.ForUser(lead)], byEmail.Select(item => item.Actor));

            Assert.Equal(2, (await search.SearchAsync(new NhActorSearchRequest("example.test", Limit: 2))).Count);
            Assert.Empty(await search.SearchAsync(new NhActorSearchRequest("o")));
            Assert.Empty(await search.SearchAsync(new NhActorSearchRequest("operations") { Kinds = [NhActorKinds.System] }));
            // A percent sign is text, not a pattern.
            Assert.Empty(await search.SearchAsync(new NhActorSearchRequest("%%")));
        }
    }

    private static NhUser User(Guid id, string userName, string email, bool lockedOut = false)
    {
        return new NhUser
        {
            Id = id,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            LockoutEnd = lockedOut ? DateTimeOffset.UtcNow.AddDays(1) : null
        };
    }

    private sealed class ApplicationSearch : INhActorSearch
    {
        public Task<IReadOnlyList<NhActorInfo>> SearchAsync(NhActorSearchRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NhActorInfo>>([]);
        }
    }

    private sealed class SearchDbContext(DbContextOptions<SearchDbContext> options) : NhIdentityDbContext(options);
}
