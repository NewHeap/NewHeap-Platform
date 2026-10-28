using NewHeap.Platform.AI;

namespace SampleProjectManagement.Core.Services;

public sealed class ProjectAiToolDiscoveryPolicy : INhAiToolDiscoveryPolicy
{
    /// <summary>
    /// SPM-278: the consultation tools that the assistant generates for agents that allow
    /// consultation (<c>NhAssistantConsultationTools</c> in NewHeap.Platform.AI.Chat). Their own
    /// policies and the subject's roles decide every call.
    /// </summary>
    private static readonly string[] ConsultationToolPrefixes = ["assistant.consult.", "assistant.request."];

    public ValueTask<bool> CanDiscoverAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasAuthorizedDivision = context.TryGetScopeValue(
                ProjectAiTools.DivisionScopeKey,
                out var divisionValue)
            && Guid.TryParse(divisionValue, out _);
        // SPM-373: the script tool runs only the project tools this turn offers, each authorized on its
        // own, so the division scope that every project tool needs is enough to discover it.
        var isProjectTool = descriptor.Id.StartsWith("projects.", StringComparison.Ordinal)
            || descriptor.CompositionFeature == NhAiCompositionFeature.Scripts
            || ConsultationToolPrefixes.Any(prefix => descriptor.Id.StartsWith(prefix, StringComparison.Ordinal));
        return ValueTask.FromResult(
            isProjectTool
            && descriptor.RequiresAuthorization
            && hasAuthorizedDivision);
    }
}
