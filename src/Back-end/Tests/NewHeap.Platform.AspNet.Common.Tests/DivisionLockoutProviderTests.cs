using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Tests.Relational;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// A locked-out division membership yields no claims for that division, on both relational
/// providers: with and without an end date, next to co-members who were never locked out or whose
/// lockout ended, and through the division policy that reloads the claims per request. A lockout
/// that starts later, one that has ended and an end date without a start leave the membership
/// active, and the member gets the division back once the lockout ends. Lockouts count in whole
/// UTC days of the application's clock, also for rows stored with another offset, and a user
/// without an active division or memberships signs in with application claims only.
/// </summary>
public sealed class DivisionLockoutProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task LockoutsCountInWholeUtcDaysOfTheApplicationClockOnEachRelationalProvider(RelationalTestProvider provider)
    {
        // 10:00 UTC on the clock, so "today" is 18 March from midnight to midnight UTC.
        var now = new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero);
        var todayStart = new DateTimeOffset(2026, 3, 18, 0, 0, 0, TimeSpan.Zero);
        var tomorrowStart = todayStart.AddDays(1);
        await using var host = await DivisionProviderTestHost.CreateAsync(provider, new DivisionTestClock(now));
        var division = await host.AddDivisionAsync("Day edge division");
        var roleName = $"member-{host.Label}";
        var memberRole = await host.AddDivisionRoleAsync(roleName, "general.view");

        // The start day counts whole: a lockout that starts later today applies from the morning on.
        var startsToday = await host.AddUserAsync("starts-today");
        await host.AddMembershipAsync(startsToday, division, [memberRole], todayStart.AddHours(23), tomorrowStart.AddDays(5));
        var startsTomorrow = await host.AddUserAsync("starts-tomorrow");
        await host.AddMembershipAsync(startsTomorrow, division, [memberRole], tomorrowStart, tomorrowStart.AddDays(5));

        // The end day counts whole too: a lockout that ended at midnight still applies today.
        var endsToday = await host.AddUserAsync("ends-today");
        await host.AddMembershipAsync(endsToday, division, [memberRole], todayStart.AddDays(-5), todayStart);
        var endedYesterday = await host.AddUserAsync("ended-yesterday");
        await host.AddMembershipAsync(endedYesterday, division, [memberRole], todayStart.AddDays(-5), todayStart.AddMilliseconds(-1));

        // Rows stored with another offset follow the UTC day of their instant. SQL Server keeps the
        // offset, PostgreSQL stores the instant in UTC. 01:30 at +02:00 on 18 March ended on
        // 17 March UTC; 00:30 at +02:00 on 19 March starts on 18 March UTC.
        var endedYesterdayElsewhere = await host.AddUserAsync("ended-yesterday-elsewhere");
        await host.AddMembershipAsync(
            endedYesterdayElsewhere,
            division,
            [memberRole],
            Stored(provider, new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.FromHours(2))),
            Stored(provider, new DateTimeOffset(2026, 3, 18, 1, 30, 0, TimeSpan.FromHours(2))));
        var startsTodayElsewhere = await host.AddUserAsync("starts-today-elsewhere");
        await host.AddMembershipAsync(
            startsTodayElsewhere,
            division,
            [memberRole],
            Stored(provider, new DateTimeOffset(2026, 3, 19, 0, 30, 0, TimeSpan.FromHours(2))),
            Stored(provider, new DateTimeOffset(2026, 3, 25, 0, 0, 0, TimeSpan.FromHours(2))));

        // Confirmed, without an active division and without a membership of any division.
        var withoutDivision = await host.AddSignInUserAsync("without-division");

        await using (var scope = host.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<INhUserManager>();

            foreach (var lockedOut in new[] { startsToday, endsToday, startsTodayElsewhere })
            {
                Assert.Empty(ClaimsFor(await users.GetValidClaimsByUserIdAsync(lockedOut, withDivision: true), division));
                Assert.False(await AuthorizeAsync(scope, lockedOut, division));
            }

            foreach (var active in new[] { startsTomorrow, endedYesterday, endedYesterdayElsewhere })
            {
                Assert.Equal(
                    [$"{division}_general.view", $"{division}_{roleName}"],
                    ClaimsFor(await users.GetValidClaimsByUserIdAsync(active, withDivision: true), division));
                Assert.True(await AuthorizeAsync(scope, active, division));
            }

            var claims = await users.GetValidClaimsByUserIdAsync(withoutDivision, withDivision: true);
            Assert.Contains(claims, claim => claim.Type == ClaimTypes.NameIdentifier && claim.Value == withoutDivision.ToString());
            Assert.DoesNotContain(claims, claim => claim.Type == NhPlatformClaimTypes.DivisionMember);
            Assert.DoesNotContain(claims, claim => claim.Type == NhPlatformClaimTypes.DivisionRole);
            Assert.False(await AuthorizeAsync(scope, withoutDivision, division));
        }

        // The password sign-in of an application with divisions issues a session with application
        // claims only.
        Assert.Null(await host.ReadAsync(context => context.Set<NhUser>()
            .Where(user => user.Id == withoutDivision)
            .Select(user => user.ActiveDivisionId)
            .SingleAsync()));
        var signIn = await host.SignInAsync(withoutDivision);
        Assert.True(signIn.Success);
        var tokenClaims = new JwtSecurityTokenHandler().ReadJwtToken(signIn.Data!.Token).Claims.ToList();
        Assert.Contains(tokenClaims, claim => claim.Type == JwtRegisteredClaimNames.NameId && claim.Value == withoutDivision.ToString());
        Assert.DoesNotContain(tokenClaims, claim => claim.Type == NhPlatformClaimTypes.DivisionMember);
        Assert.NotNull(signIn.Data.RefreshToken);
    }

    /// <summary>
    /// The value as the provider can store it: PostgreSQL's <c>timestamptz</c> takes only UTC
    /// values, SQL Server's <c>datetimeoffset</c> keeps the offset.
    /// </summary>
    private static DateTimeOffset Stored(RelationalTestProvider provider, DateTimeOffset value)
    {
        return provider == RelationalTestProvider.PostgreSql ? value.ToUniversalTime() : value;
    }

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
