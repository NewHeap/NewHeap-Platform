using System.Security.Claims;
using NewHeap.Platform.Common.Identity.Claims;

namespace SampleProjectManagement.Api.Composition;

public sealed record SampleHostedIdentityScope(Guid TenantId, Guid EnvironmentId, Guid ApplicationId,
    Guid DeploymentEnvironmentId, string Audience);

public sealed record SampleHostedAccessDecision(Guid SubjectId, SampleHostedIdentityScope Scope,
    bool Allowed, DateTimeOffset ValidUntil, IReadOnlyList<string> ApplicationPermissions);

/// <summary>
/// Consumer-owned projection after OIDC signature/issuer/audience validation and an authenticated,
/// server-to-server access-context lookup. This is not a token validator or a replacement for that lookup.
/// </summary>
public static class SampleHostedAccessContext
{
    public static ClaimsPrincipal Project(ClaimsPrincipal validatedToken, SampleHostedIdentityScope expected,
        SampleHostedAccessDecision decision, IReadOnlySet<string> permissionCatalog, DateTimeOffset now)
    {
        if (validatedToken.Identity?.IsAuthenticated != true || !decision.Allowed || decision.ValidUntil <= now ||
            decision.Scope != expected || decision.SubjectId == Guid.Empty || expected.TenantId == Guid.Empty ||
            expected.EnvironmentId == Guid.Empty || expected.ApplicationId == Guid.Empty ||
            expected.DeploymentEnvironmentId == Guid.Empty || string.IsNullOrWhiteSpace(expected.Audience) ||
            validatedToken.FindFirstValue("sub") != decision.SubjectId.ToString() ||
            validatedToken.FindFirstValue("tenant_id") != expected.TenantId.ToString() ||
            validatedToken.FindFirstValue("environment_id") != expected.EnvironmentId.ToString() ||
            validatedToken.FindFirstValue("application_id") != expected.ApplicationId.ToString() ||
            validatedToken.FindFirstValue("deployment_environment_id") != expected.DeploymentEnvironmentId.ToString() ||
            validatedToken.FindFirstValue("aud") != expected.Audience)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        // Rebuild volatile permissions from the current decision. Do not carry stale token permissions,
        // general control-plane permissions, local roles or access/refresh tokens into this identity.
        var identity = new ClaimsIdentity("hosted-access-context");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, decision.SubjectId.ToString()));
        identity.AddClaim(new Claim(NhPlatformClaimTypes.DivisionMember, expected.TenantId.ToString()));
        foreach (var permission in decision.ApplicationPermissions.Distinct(StringComparer.Ordinal))
        {
            if (permissionCatalog.Contains(permission))
            {
                identity.AddClaim(new Claim(NhPlatformClaimTypes.Permission, permission));
            }
        }

        return new ClaimsPrincipal(identity);
    }
}
