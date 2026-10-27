using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Tests.Relational;
using System.Security.Claims;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// A locked-out division membership yields no claims for that division, on both relational
/// providers: with and without an end date, next to co-members who were never locked out or whose
/// lockout ended, and through the division policy that reloads the claims per request. A lockout
/// that starts later, one that has ended and an end date without a start leave the membership
/// active, and the member gets the division back once the lockout ends.
/// </summary>
public sealed class DivisionLockoutProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ALockedOutMembershipYieldsNoDivisionClaimsOnEachRelationalProvider(RelationalTestProvider provider)
    {
        await using var host = await DivisionProviderTestHost.CreateAsync(provider);
        var now = DateTimeOffset.UtcNow;
        var division = await host.AddDivisionAsync("Locked division");
        var otherDivision = await host.AddDivisionAsync("Other division");
        var roleName = $"member-{host.Label}";
        var memberRole = await host.AddDivisionRoleAsync(roleName, "general.view");

        var locked = await host.AddUserAsync("locked");
        var lockedMembership = await host.AddMembershipAsync(locked, division, [memberRole], now.AddDays(-1), now.AddDays(5));
        await host.AddMembershipAsync(locked, otherDivision, [memberRole]);
        var lockedWithoutEnd = await host.AddUserAsync("locked-without-end");
        await host.AddMembershipAsync(lockedWithoutEnd, division, [memberRole], now.AddDays(-1));

        // Co-members whose rows the division predicate must not count for someone else.
        var neverLocked = await host.AddUserAsync("never-locked");
        await host.AddMembershipAsync(neverLocked, division, [memberRole]);
        var lockoutEnded = await host.AddUserAsync("lockout-ended");
        await host.AddMembershipAsync(lockoutEnded, division, [memberRole], now.AddDays(-20), now.AddDays(-10));
        var lockoutLater = await host.AddUserAsync("lockout-later");
        await host.AddMembershipAsync(lockoutLater, division, [memberRole], now.AddDays(2), now.AddDays(10));
        var endWithoutStart = await host.AddUserAsync("end-without-start");
        await host.AddMembershipAsync(endWithoutStart, division, [memberRole], lockOutEnd: now.AddDays(5));
        var outsider = await host.AddUserAsync("outsider");

        await using (var scope = host.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<INhUserManager>();

            Assert.Empty(ClaimsFor(await users.GetValidClaimsByUserIdAsync(locked, withDivision: true), division));
            Assert.Empty(ClaimsFor(await users.GetValidClaimsByUserIdAsync(lockedWithoutEnd, withDivision: true), division));
            Assert.Empty(ClaimsFor(await users.GetValidClaimsByUserIdAsync(outsider, withDivision: true), division));

            // The lockout of one membership leaves the user's other divisions alone.
            Assert.Equal(
                [$"{otherDivision}_general.view", $"{otherDivision}_{roleName}"],
                ClaimsFor(await users.GetValidClaimsByUserIdAsync(locked, withDivision: true), otherDivision));

            foreach (var active in new[] { neverLocked, lockoutEnded, lockoutLater, endWithoutStart })
            {
                Assert.Equal(
                    [$"{division}_general.view", $"{division}_{roleName}"],
                    ClaimsFor(await users.GetValidClaimsByUserIdAsync(active, withDivision: true), division));
            }

            Assert.False(await AuthorizeAsync(scope, locked, division));
            Assert.False(await AuthorizeAsync(scope, lockedWithoutEnd, division));
            Assert.True(await AuthorizeAsync(scope, locked, otherDivision));
            Assert.True(await AuthorizeAsync(scope, neverLocked, division));
        }

        // Once the lockout has ended, the division is back.
        await using (var scope = host.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DivisionTestDbContext>();
            await context.Set<NhDivisionUser>()
                .Where(x => x.Id == lockedMembership)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LockOutEndDateTime, now.AddDays(-1)));
        }

        await using (var scope = host.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<INhUserManager>();

            Assert.Equal(
                [$"{division}_general.view", $"{division}_{roleName}"],
                ClaimsFor(await users.GetValidClaimsByUserIdAsync(locked, withDivision: true), division));
            Assert.True(await AuthorizeAsync(scope, locked, division));
        }
    }

    /// <summary>The values of the division-scoped role and permission claims of one division.</summary>
    private static List<string> ClaimsFor(IEnumerable<Claim> claims, Guid divisionId)
    {
        return claims
            .Where(claim => claim.Value.StartsWith($"{divisionId}_", StringComparison.Ordinal))
            .Select(claim => claim.Value)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<bool> AuthorizeAsync(AsyncServiceScope scope, Guid userId, Guid activeDivisionId)
    {
        var request = DivisionProviderTestHost.CreateRequest(scope, userId, activeDivisionId);
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        return (await authorization.AuthorizeAsync(request.User, null, DivisionProviderTestHost.GeneralViewPolicy)).Succeeded;
    }
}
