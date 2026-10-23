using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-302: a deployment that serves one tenant, such as one installation per customer whose people
/// sign in at the customer's identity provider. The AI ASP.NET integration projects every person of
/// that issuer into the one tenant without trusting a tenant claim, and the assistant's single-tenant
/// mode lets those people take part in the work of the whole application (<c>nh-system</c>): its
/// approval inbox, deciding its proposals, boards, live watches, its threads and run history, and
/// running its agents by hand. That work stays stored without a tenant, so the mode can be switched off
/// again without a migration. The read and approve policies of the whole application and each agent's
/// run and view policies still decide what a person sees and decides.
/// </summary>
/// <remarks>
/// Name the same tenant in both calls: startup fails when the integration projects another tenant, or
/// takes the tenant of an issuer from a claim, which could carry more than one. A person's actor id then
/// belongs to the issuer (<c>oidc:...</c>), so such a host also registers an <c>INhAiPrincipalFactory</c>
/// of its own that rebuilds its people's rights for headless decisions, as the whole application's roles
/// are evaluated. The sample's own API serves many divisions without tenants and does not call this.
/// </remarks>
public static class SampleSingleTenantComposition
{
    /// <summary>
    /// Projects the people of <paramref name="issuer"/> into <paramref name="tenantId"/> and lets them take
    /// part in the whole application's work. Call it after <c>AddSampleAssistant</c>.
    /// </summary>
    public static IServiceCollection AddSampleSingleTenant(this IServiceCollection services, string issuer, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        services.AddNewHeapPlatformAIAspNet(ai => ai.UseAuthenticatedClaimsForSingleTenant(issuer, tenantId));
        services.AddNewHeapAssistant(assistant => assistant.UseSingleTenant(tenantId));
        return services;
    }
}
