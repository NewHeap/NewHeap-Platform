using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Tests.Relational;
using System.Security.Claims;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// A division decision requires membership of the requested division, also when no claim or role
/// is required, on both relational providers: through <c>DivisionAccessAsync</c>,
/// <c>HasDivisionAccessAsync</c>, NewHeap Identity's <c>INhDivisionAccess</c> on the request and
/// without one, and a division policy without requirements. A member without division roles is
/// still a member, and a division that does not exist is denied.
/// </summary>
public sealed class DivisionMembershipAccessProviderTests
{
    private static readonly Claim GeneralView = new(NhPlatformClaimTypes.DivisionPermission, "general.view");

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task DivisionAccessRequiresMembershipOnEachRelationalProvider(RelationalTestProvider provider)
    {
        await using var host = await DivisionProviderTestHost.CreateAsync(provider);
        var divisionA = await host.AddDivisionAsync("Division A");
        var divisionB = await host.AddDivisionAsync("Division B");
        var unknownDivision = Guid.NewGuid();
        var memberRole = await host.AddDivisionRoleAsync($"member-{host.Label}", "general.view");

        var member = await host.AddUserAsync("member-of-a");
        var withoutRoles = await host.AddUserAsync("member-without-roles");
        var outsider = await host.AddUserAsync("outsider");
        var administrator = await host.AddUserAsync("administrator");
        await host.AddMembershipAsync(member, divisionA, [memberRole]);
        await host.AddMembershipAsync(withoutRoles, divisionA, []);
        await host.AddPermissionAsync(administrator, Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll);

        await using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<INhUserManager>();

        // The claims carry the membership itself, also without division roles.
        var memberClaims = await users.GetValidClaimsByUserIdAsync(member, withDivision: true);
        Assert.Contains(memberClaims, claim => claim.Type == NhPlatformClaimTypes.DivisionMember && claim.Value == divisionA.ToString());
        Assert.DoesNotContain(memberClaims, claim => claim.Value.StartsWith(divisionB.ToString(), StringComparison.Ordinal));
        var withoutRolesClaims = await users.GetValidClaimsByUserIdAsync(withoutRoles, withDivision: true);
        Assert.Equal(
            [divisionA.ToString()],
            withoutRolesClaims.Where(claim => claim.Type == NhPlatformClaimTypes.DivisionMember).Select(claim => claim.Value));

        // Without requirements, in every form, membership decides.
        Assert.False(await users.DivisionAccessAsync(divisionB, memberClaims));
        Assert.False(await users.DivisionAccessAsync(divisionB, memberClaims, [], null));
        Assert.False(await users.DivisionAccessAsync(divisionB, memberClaims, null, []));
        Assert.False(await users.DivisionAccessAsync(divisionB, memberClaims, [], []));
        Assert.False(await users.DivisionAccessAsync(unknownDivision, memberClaims));
        Assert.True(await users.DivisionAccessAsync(divisionA, memberClaims));
        Assert.True(await users.DivisionAccessAsync(divisionA, memberClaims, [], []));

        // Requirements come on top of membership.
        Assert.False(await users.DivisionAccessAsync(divisionB, memberClaims, [GeneralView]));
        Assert.True(await users.DivisionAccessAsync(divisionA, memberClaims, [GeneralView], [$"member-{host.Label}"]));
        Assert.True(await users.DivisionAccessAsync(divisionA, withoutRolesClaims));
        Assert.False(await users.DivisionAccessAsync(divisionA, withoutRolesClaims, [GeneralView]));

        var outsiderClaims = await users.GetValidClaimsByUserIdAsync(outsider, withDivision: true);
        Assert.False(await users.DivisionAccessAsync(divisionA, outsiderClaims));

        // The division access-all permission makes the user a member of every division that exists.
        var administratorClaims = await users.GetValidClaimsByUserIdAsync(administrator, withDivision: true);
        Assert.True(await users.DivisionAccessAsync(divisionB, administratorClaims));
        Assert.False(await users.DivisionAccessAsync(unknownDivision, administratorClaims));

        // The request's own user, through the HTTP context, INhDivisionAccess and a policy.
        var request = DivisionProviderTestHost.CreateRequest(scope, member, divisionB);
        Assert.False(await request.HasDivisionAccessAsync(divisionB));
        Assert.False(await request.HasDivisionAccessAsync(unknownDivision));
        Assert.True(await request.HasDivisionAccessAsync(divisionA));

        var divisions = scope.ServiceProvider.GetRequiredService<INhDivisionAccess>();
        Assert.False(await divisions.HasAccessAsync(NhActorRef.ForUser(member), divisionB));
        Assert.False(await divisions.HasAccessAsync(NhActorRef.ForUser(member), unknownDivision));
        Assert.True(await divisions.HasAccessAsync(NhActorRef.ForUser(member), divisionA));
        // Another user than the request's, as background work asks.
        Assert.False(await divisions.HasAccessAsync(NhActorRef.ForUser(outsider), divisionA));
        Assert.True(await divisions.HasAccessAsync(NhActorRef.ForUser(withoutRoles), divisionA));

        Assert.False(await AuthorizeAsync(scope, member, divisionB, DivisionProviderTestHost.MembershipPolicy));
        Assert.True(await AuthorizeAsync(scope, member, divisionA, DivisionProviderTestHost.MembershipPolicy));
        Assert.True(await AuthorizeAsync(scope, withoutRoles, divisionA, DivisionProviderTestHost.MembershipPolicy));
        Assert.False(await AuthorizeAsync(scope, withoutRoles, divisionA, DivisionProviderTestHost.GeneralViewPolicy));
        Assert.True(await AuthorizeAsync(scope, member, divisionA, DivisionProviderTestHost.GeneralViewPolicy));

        // Without a request, as background work asks.
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        Assert.False(await divisions.HasAccessAsync(NhActorRef.ForUser(member), divisionB));
        Assert.True(await divisions.HasAccessAsync(NhActorRef.ForUser(member), divisionA));
    }

    private static async Task<bool> AuthorizeAsync(AsyncServiceScope scope, Guid userId, Guid activeDivisionId, string policy)
    {
        var request = DivisionProviderTestHost.CreateRequest(scope, userId, activeDivisionId);
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        return (await authorization.AuthorizeAsync(request.User, null, policy)).Succeeded;
    }
}
