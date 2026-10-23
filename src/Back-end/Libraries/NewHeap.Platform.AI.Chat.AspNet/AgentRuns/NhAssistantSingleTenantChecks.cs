using NewHeap.Platform.AI.AspNet;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The startup check of the single-tenant mode (<see cref="NhAssistantBuilder.UseSingleTenant"/>): the
/// AI ASP.NET integration may give callers the configured tenant or none, never another one. It fails
/// for an issuer projected into another fixed tenant (<c>UseAuthenticatedClaimsForSingleTenant</c>) and
/// for an issuer whose tenant comes from a claim (<c>UseAuthenticatedClaims</c>), which can carry more
/// than one. An issuer without a tenant (<c>UseAuthenticatedClaimsWithoutTenant</c>) passes: its people
/// take part in the whole application as callers without a tenant always do. A resolver of the
/// application's own (<c>UseAuthenticatedInvocationContextResolver</c>) cannot be checked here; it must
/// return the configured tenant or none, and a caller of any other tenant sees nothing of the whole
/// application.
/// </summary>
internal static class NhAssistantSingleTenantChecks
{
    public static void Validate(NhAssistantRegistrationState state, NhAiAspNetRegistrationState? aspNet)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SingleTenantId is not { } tenantId || aspNet is null)
        {
            return;
        }

        foreach (var projection in aspNet.IdentityProjections.OrderBy(item => item.ExpectedIssuer, StringComparer.Ordinal))
        {
            if (projection.TenantClaimType is { } claimType)
            {
                throw new InvalidOperationException(
                    $"UseSingleTenant(\"{tenantId}\") serves one tenant, but the AI ASP.NET integration takes the tenant of issuer "
                    + $"'{projection.ExpectedIssuer}' from claim '{claimType}', which can carry more than one. Project the issuer with "
                    + $"UseAuthenticatedClaimsForSingleTenant(issuer, \"{tenantId}\") or UseAuthenticatedClaimsWithoutTenant.");
            }
            if (projection.FixedTenantId is { } fixedTenant
                && !string.Equals(fixedTenant, tenantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"UseSingleTenant(\"{tenantId}\") serves one tenant, but the AI ASP.NET integration projects issuer "
                    + $"'{projection.ExpectedIssuer}' into tenant \"{fixedTenant}\". Use the same tenant in "
                    + "UseAuthenticatedClaimsForSingleTenant and UseSingleTenant.");
            }
        }
    }
}
