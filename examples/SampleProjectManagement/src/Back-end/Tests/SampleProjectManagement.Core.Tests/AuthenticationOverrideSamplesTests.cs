using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NSubstitute;
using SampleProjectManagement.Api.Authorization;
using System.Security.Claims;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class AuthenticationOverrideSamplesTests
{
    [Fact]
    public async Task RuntimeClaimsAreHydratedOnlyOncePerRequestAndWithoutDuplicates()
    {
        var userId = Guid.NewGuid();
        var divisionClaim = new Claim(
            NhPlatformClaimTypes.DivisionPermission,
            $"{SampleAuthorizationDefaults.NorthDivisionId}_project.view");
        var projectClaim = new Claim(
            SampleProjectClaimTypes.ProjectPermission,
            SampleProjectClaimTypes.PermissionValue(
                SampleAuthorizationDefaults.AlphaProjectId,
                "confidential.view"));
        var userManager = Substitute.For<INhUserManager<NhUser>>();
        userManager
            .GetValidClaimsByUserIdAsync(
                userId,
                true,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                divisionClaim,
                projectClaim,
                new Claim(NhPlatformClaimTypes.Permission, "app.project.view")
            ]);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                divisionClaim
            ],
            "sample-test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        var transformer = new SampleRuntimeClaimsTransformation(
            new HttpContextAccessor { HttpContext = httpContext },
            userManager);

        await transformer.TransformAsync(principal);
        await transformer.TransformAsync(principal);

        Assert.Single(principal.Claims, claim =>
            claim.Type == divisionClaim.Type && claim.Value == divisionClaim.Value);
        Assert.Contains(principal.Claims, claim =>
            claim.Type == projectClaim.Type && claim.Value == projectClaim.Value);
        Assert.DoesNotContain(principal.Claims, claim =>
            claim.Type == NhPlatformClaimTypes.Permission &&
            claim.Value == "app.project.view");
        await userManager.Received(1).GetValidClaimsByUserIdAsync(
            userId,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RuntimeClaimClassificationKeepsApplicationClaimsInTheToken()
    {
        Assert.True(SampleRuntimeAuthorizationClaims.IsRequestScoped(
            NhPlatformClaimTypes.DivisionPermission));
        Assert.True(SampleRuntimeAuthorizationClaims.IsRequestScoped(
            SampleProjectClaimTypes.ProjectPermission));
        Assert.False(SampleRuntimeAuthorizationClaims.IsRequestScoped(
            NhPlatformClaimTypes.Permission));
    }

    [Fact]
    public async Task RemovedUserTurnsAStaleTokenIntoAnAnonymousPrincipal()
    {
        var userId = Guid.NewGuid();
        var userManager = Substitute.For<INhUserManager<NhUser>>();
        userManager
            .GetValidClaimsByUserIdAsync(
                userId,
                true,
                Arg.Any<CancellationToken>())
            .Returns([]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "stale-sample-token"));
        var httpContext = new DefaultHttpContext { User = principal };
        var transformer = new SampleRuntimeClaimsTransformation(
            new HttpContextAccessor { HttpContext = httpContext },
            userManager);

        var transformedPrincipal = await transformer.TransformAsync(principal);

        Assert.False(transformedPrincipal.Identity?.IsAuthenticated);
        Assert.Empty(transformedPrincipal.Claims);
    }
}
