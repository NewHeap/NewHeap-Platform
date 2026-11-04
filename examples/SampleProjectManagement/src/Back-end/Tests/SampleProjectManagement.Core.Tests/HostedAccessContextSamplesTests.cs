using System.Security.Claims;
using Xunit;
using NewHeap.Platform.Common.Identity.Claims;
using SampleProjectManagement.Api.Composition;

namespace SampleProjectManagement.Core.Tests;

public sealed class HostedAccessContextSamplesTests
{
    [Fact]
    public void Current_application_permissions_replace_token_permissions_without_copying_credentials()
    {
        var (token, scope, decision) = Valid();
        var principal = SampleHostedAccessContext.Project(token, scope, decision,
            new HashSet<string>(["example.access"], StringComparer.Ordinal), DateTimeOffset.UtcNow);
        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal(decision.SubjectId.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("example.access", Assert.Single(principal.FindAll(NhPlatformClaimTypes.Permission)).Value);
        Assert.Equal(scope.TenantId.ToString(), Assert.Single(principal.FindAll(NhPlatformClaimTypes.DivisionMember)).Value);
        Assert.DoesNotContain(principal.Claims, c => c.Type is "access_token" or "refresh_token" || c.Value == "example.admin");
    }

    [Theory]
    [InlineData("tenant_id")]
    [InlineData("environment_id")]
    [InlineData("application_id")]
    [InlineData("deployment_environment_id")]
    [InlineData("sub")]
    [InlineData("aud")]
    public void A_token_from_another_identity_scope_is_anonymous(string claim)
    {
        var (token, scope, decision) = Valid();
        var identity = (ClaimsIdentity)token.Identity!;
        identity.RemoveClaim(identity.FindFirst(claim)!);
        identity.AddClaim(new Claim(claim, Guid.NewGuid().ToString()));
        Assert.False(SampleHostedAccessContext.Project(token, scope, decision, new HashSet<string>(), DateTimeOffset.UtcNow).Identity!.IsAuthenticated);
    }

    [Fact]
    public void Revocation_expiry_and_context_scope_mismatch_fail_closed()
    {
        var (token, scope, decision) = Valid();
        foreach (var invalid in new[] { decision with { Allowed = false }, decision with { ValidUntil = DateTimeOffset.MinValue },
            decision with { Scope = scope with { DeploymentEnvironmentId = Guid.NewGuid() } } })
        {
            Assert.False(SampleHostedAccessContext.Project(token, scope, invalid, new HashSet<string>(), DateTimeOffset.UtcNow).Identity!.IsAuthenticated);
        }
    }

    private static (ClaimsPrincipal Token, SampleHostedIdentityScope Scope, SampleHostedAccessDecision Decision) Valid()
    {
        var scope = new SampleHostedIdentityScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "example-hosted-client");
        var decision = new SampleHostedAccessDecision(Guid.NewGuid(), scope, true, DateTimeOffset.UtcNow.AddMinutes(1),
            ["example.access", "example.access", "control-plane.manage"]);
        var claims = new Dictionary<string, string>
        {
            ["sub"] = decision.SubjectId.ToString(), ["tenant_id"] = scope.TenantId.ToString(),
            ["environment_id"] = scope.EnvironmentId.ToString(), ["application_id"] = scope.ApplicationId.ToString(),
            ["deployment_environment_id"] = scope.DeploymentEnvironmentId.ToString(), ["aud"] = scope.Audience,
            [NhPlatformClaimTypes.Permission] = "example.admin", ["access_token"] = "test-access", ["refresh_token"] = "test-refresh"
        };
        return (new ClaimsPrincipal(new ClaimsIdentity(claims.Select(p => new Claim(p.Key, p.Value)), "validated-oidc")), scope, decision);
    }
}
