using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The division claims on EF Core InMemory, which applications use in their unit tests. This is
/// non-relational evidence: it proves that the code path runs and that the lockout predicate means
/// the same in memory, not how SQL Server or PostgreSQL translate it, which
/// <see cref="DivisionLockoutProviderTests"/> proves. A confirmed user without an active division
/// and without memberships signs in next to members with and without lockouts; the predicate read
/// <c>LockOutStartDateTime.Value</c>, which threw "Nullable object must have a value" on InMemory.
/// </summary>
public sealed class DivisionLockoutInMemoryTests
{
    [Fact]
    public async Task AUserWithoutDivisionsSignsInOnInMemoryNextToLockedOutMembers()
    {
        var now = new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero);
        await using var host = await DivisionProviderTestHost.CreateInMemoryAsync(new DivisionTestClock(now));
        var division = await host.AddDivisionAsync("Team division");
        await host.AddDivisionAsync("Empty division");
        var roleName = $"member-{host.Label}";
        var memberRole = await host.AddDivisionRoleAsync(roleName, "general.view");

        var neverLocked = await host.AddUserAsync("never-locked");
        await host.AddMembershipAsync(neverLocked, division, [memberRole]);
        var lockedWithoutEnd = await host.AddUserAsync("locked-without-end");
        await host.AddMembershipAsync(lockedWithoutEnd, division, [memberRole], now.AddDays(-1));
        var endWithoutStart = await host.AddUserAsync("end-without-start");
        await host.AddMembershipAsync(endWithoutStart, division, [memberRole], lockOutEnd: now.AddDays(5));
        var lockoutEnded = await host.AddUserAsync("lockout-ended");
        await host.AddMembershipAsync(lockoutEnded, division, [memberRole], now.AddDays(-20), now.AddDays(-10));

        // Confirmed, no active division and no membership of any division.
        var withoutDivision = await host.AddSignInUserAsync("without-division");

        await using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<INhUserManager>();

        // The access token of the sign-in, with the division claims of an application with divisions.
        // The complete password sign-in also stores a refresh token, whose clean-up InMemory cannot run.
        var token = await scope.ServiceProvider.GetRequiredService<INhAuthenticationService>().CreateToken(withoutDivision);
        Assert.Contains(token.Claims, claim => claim.Type == JwtRegisteredClaimNames.NameId && claim.Value == withoutDivision.ToString());
        Assert.DoesNotContain(token.Claims, claim => claim.Type == NhPlatformClaimTypes.DivisionMember);

        var claims = await users.GetValidClaimsByUserIdAsync(withoutDivision, withDivision: true);
        Assert.Contains(claims, claim => claim.Type == ClaimTypes.NameIdentifier && claim.Value == withoutDivision.ToString());
        Assert.DoesNotContain(claims, claim => claim.Type == NhPlatformClaimTypes.DivisionMember);
        Assert.DoesNotContain(claims, claim => claim.Type == NhPlatformClaimTypes.DivisionRole);

        // The co-members get what they get on SQL Server and PostgreSQL.
        Assert.Empty(ClaimsFor(await users.GetValidClaimsByUserIdAsync(lockedWithoutEnd, withDivision: true), division));
        foreach (var active in new[] { neverLocked, endWithoutStart, lockoutEnded })
        {
            Assert.Equal(
                [$"{division}_general.view", $"{division}_{roleName}"],
                ClaimsFor(await users.GetValidClaimsByUserIdAsync(active, withDivision: true), division));
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
}
