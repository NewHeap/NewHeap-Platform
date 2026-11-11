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

    /// <summary>
    /// SPM-394: the tools of the agent builder (<c>NhAssistantAgentBuilderIds.ToolIdPrefix</c> in
    /// NewHeap.Platform.AI.Chat). Only the builder is offered them, their policy requires the assistant's
    /// administration, and every change waits for the administrator's approval; they need no division.
    /// </summary>
    private const string AgentBuilderToolPrefix = "assistant.agent-builder.";

    /// <summary>
    /// SPM-399: the tools of the reviewed research server (<c>mcp.research.&lt;tool&gt;</c>), which the API
    /// exports to a person's own AI app when it opts in: reads, and changes the person confirms in that app.
    /// The server's required policy, or else the assistant's access policy, decides every call; they need
    /// no division.
    /// </summary>
    private const string ExportedResearchToolPrefix = "mcp.research.";

    public ValueTask<bool> CanDiscoverAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.Id.StartsWith(AgentBuilderToolPrefix, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(descriptor.RequiresAuthorization);
        }

        if (descriptor.Id.StartsWith(ExportedResearchToolPrefix, StringComparison.Ordinal))
        {
            // A read, or a change whose approval is the person's confirmation in their own AI app.
            return ValueTask.FromResult(descriptor.RequiresAuthorization
                && (descriptor.Effect == NhAiToolEffect.ReadOnly || descriptor.Approval == NhAiApprovalRequirement.ConsumerAuthoritative));
        }

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
