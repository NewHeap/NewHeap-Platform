using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The division claims on EF Core InMemory, which applications use in their unit tests. This is
/// non-relational evidence: it proves that the claims and token path of the sign-in runs in memory
/// and that the lockout days mean the same there, not how SQL Server or PostgreSQL translate the
/// predicate, which <see cref="DivisionLockoutProviderTests"/> proves. A confirmed user without an
/// active division and without memberships gets the sign-in's access token next to members with
/// and without lockouts. The predicate used to read <c>LockOutStartDateTime.Value</c>, which an
/// application reported as "Nullable object must have a value" on InMemory; it now compares
/// instants and reads no value.
/// </summary>
public sealed class DivisionLockoutInMemoryTests
{
    [Fact]
    public async Task AUserWithoutDivisionsGetsTheSignInTokenOnInMemoryNextToLockedOutMembers()
    {
        var now = new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero);
        var todayStart = new DateTimeOffset(2026, 3, 18, 0, 0, 0, TimeSpan.Zero);
        var tomorrowStart = todayStart.AddDays(1);
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
        var startsToday = await host.AddUserAsync("starts-today");
        await host.AddMembershipAsync(startsToday, division, [memberRole], todayStart.AddHours(23), tomorrowStart.AddDays(5));
        var startsTomorrow = await host.AddUserAsync("starts-tomorrow");
        await host.AddMembershipAsync(startsTomorrow, division, [memberRole], tomorrowStart, tomorrowStart.AddDays(5));
        var endsToday = await host.AddUserAsync("ends-today");
        await host.AddMembershipAsync(endsToday, division, [memberRole], todayStart.AddDays(-5), todayStart);
        var endedYesterday = await host.AddUserAsync("ended-yesterday");
        await host.AddMembershipAsync(endedYesterday, division, [memberRole], todayStart.AddDays(-5), todayStart.AddMilliseconds(-1));

        // Confirmed, no active division and no membership of any division.
        var withoutDivision = await host.AddUserAsync("without-division");

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
        foreach (var lockedOut in new[] { lockedWithoutEnd, startsToday, endsToday })
        {
            Assert.Empty(ClaimsFor(await users.GetValidClaimsByUserIdAsync(lockedOut, withDivision: true), division));
        }

        foreach (var active in new[] { neverLocked, endWithoutStart, startsTomorrow, endedYesterday })
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
