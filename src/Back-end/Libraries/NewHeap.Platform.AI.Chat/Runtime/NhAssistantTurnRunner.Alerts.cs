using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// The model tool of alerts (FB87-9): <c>raise_alert</c> (<c>assistant.alert.raise</c>) in a run whose
/// agent lists kinds that allow a model and that an administrator switched to shadow or live for it.
/// </summary>
internal sealed partial class NhAssistantTurnRunner
{
    /// <summary>
    /// Reads which alert kinds the run's model is offered, once per prepared turn: only a run's own work
    /// on one subject, never a person's turn, a consultation or a chat.
    /// </summary>
    private async Task LoadAlertModelKindsAsync(NhAssistantTurnState state, CancellationToken cancellationToken)
    {
        if (!IsRun(state) || state.AgentTurn is not { Options.AlertKinds.Count: > 0 } agentTurn)
        {
            state.AlertModelKinds = new Dictionary<string, string>(StringComparer.Ordinal);
            return;
        }

        state.AlertModelKinds = await services.GetRequiredService<NhAssistantAlertModelTool>()
            .OfferedKindsAsync(agentTurn.Options, agentTurn.Subject, cancellationToken);
    }

    /// <summary>
    /// <c>raise_alert</c> for the kinds the turn offers: it raises an alert about the run's own subject
    /// for the people the kind chooses, with the parameters the platform checked against the kind's
    /// template, and tells the model only whether it was recorded.
    /// </summary>
    private NhAssistantMemoryFunction CreateRaiseAlertFunction(NhAssistantTurnState state)
    {
        var tool = services.GetRequiredService<NhAssistantAlertModelTool>();
        var offered = state.AlertModelKinds;
        return new NhAssistantMemoryFunction(
            RaiseAlertDescriptor(ClassificationOf(state)),
            tool.DescriptionOf(offered),
            tool.SchemaOf(offered),
            async (arguments, cancellationToken) =>
            {
                var outcome = await services.GetRequiredService<NhAssistantAlertModelTool>()
                    .RaiseAsync(state.AgentTurn!, offered, arguments, cancellationToken);
                return NhAssistantAlertModelTool.ResultOf(outcome);
            });
    }

    /// <summary>
    /// The descriptor of <c>raise_alert</c>, classified as the run's model calls are, so the values the
    /// model gave are stored with its call as the run's data.
    /// </summary>
    private static NhAiToolDescriptor RaiseAlertDescriptor(NhAiDataClassification classification)
    {
        const NhAiToolEffect effect = NhAiToolEffect.IdempotentMutation;
        return new NhAiToolDescriptor(
            NhAssistantAlertModelTool.ToolId,
            1,
            "Raises an alert of a kind declared in code about the run's subject.",
            typeof(JsonElement),
            typeof(JsonElement),
            effect,
            NhAiToolExposure.Agent,
            false,
            [])
        {
            ExportName = NhAssistantAlertModelTool.ExportName,
            CatalogId = "assistant",
            ContractHash = NhAiCanonicalJson.ComputeHash(NhAssistantAlertModelTool.ToolId + "@1:" + effect),
            Approval = NhAiApprovalRequirement.NotRequired,
            DataClassification = classification,
            RetentionCategory = NhAiRetentionCategory.ConversationContent
        };
    }
}
