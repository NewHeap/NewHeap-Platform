using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-327: what waits for people reaches them. The daily approval digest
/// (<see cref="SampleApprovalDigest"/>) counts per person what the approval inbox lists per status, and
/// the whole application lists its people (<see cref="SampleSystemMembers"/>), so a company-wide
/// proposal of the project follow-up in a thread nobody opened still notifies the people who decide
/// it. Call it after <c>AddSampleProjectStarterBlocks</c>, which configures the whole application's
/// policies.
/// </summary>
public static class SampleDecisionDigestComposition
{
    public static IServiceCollection AddSampleDecisionDigest(this IServiceCollection services)
    {
        services.TryAddScoped<SampleApprovalDigest>();
        services.AddNewHeapAssistant(assistant => assistant
            // Roles keep coming from the read and approve policies; the list only names candidates.
            .UseSystemSubject(system => system.Members<SampleSystemMembers>()));
        return services;
    }
}
