using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// The plan an agent keeps of its work (<c>run.KeepPlan()</c>): <c>update_plan</c> sets or advances a
/// short checklist, the turn's message keeps it, the turn streams it as <c>plan.updated</c> and a run's
/// result shows the run's last plan.
/// </summary>
internal sealed partial class NhAssistantTurnRunner
{
    /// <summary>
    /// Whether the turn offers <c>update_plan</c>: a run, a turn in an agent's thread or a person's chat
    /// of an agent that keeps a plan. Consultations, which write nothing into the thread, never offer it.
    /// </summary>
    private static bool OffersPlan(NhAssistantTurnState state)
    {
        if (state.Scope.Consultation is not null)
        {
            return false;
        }

        var options = state.AgentTurn?.Options ?? state.ChatOptions;
        return options is { KeepsPlan: true };
    }

    /// <summary>
    /// <c>update_plan</c>: checks the update against the turn's plan so far and, when it fits, stores
    /// the plan in the turn's message and streams it. A refused update changes nothing.
    /// </summary>
    private NhAssistantMemoryFunction CreatePlanFunction(NhAssistantTurnState state)
    {
        return new NhAssistantMemoryFunction(
            NhAssistantMemoryFunction.PlanDescriptor,
            NhAssistantPlans.Description,
            NhAssistantPlans.Schema,
            async (arguments, cancellationToken) => await UpdatePlanAsync(state, arguments, cancellationToken));
    }

    private async Task<object?> UpdatePlanAsync(
        NhAssistantTurnState state,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!state.PlanLoaded)
        {
            // A turn that waited for a person and continues goes on with the plan its earlier part kept,
            // and with the updates that part used.
            var calls = await store.GetTurnToolResultsAsync(state.Conversation.Id, state.Scope.TurnId, cancellationToken);
            (state.Plan, state.PlanUpdates) = NhAssistantPlans.OfTurn(calls);
            state.PlanLoaded = true;
        }

        var (plan, refusal) = NhAssistantPlans.Update(arguments, state.Plan, state.PlanUpdates);
        if (plan is null)
        {
            return refusal;
        }

        state.Plan = plan;
        state.PlanUpdates++;
        var part = NhAssistantPlans.PartOf(plan, IsRun(state) ? state.Scope.RunId : null, DateTimeOffset.UtcNow);
        state.SetPlanPart(part);
        await store.UpdateMessageAsync(
            state.AssistantMessageId,
            NhAssistantContent.SerializeParts(state.Parts),
            state.InputTokens,
            state.OutputTokens,
            state.ToolCalls,
            cancellationToken);
        await state.EmitAsync(new NhAssistantPlanUpdatedEvent(
            state.Scope.TurnId,
            state.AssistantMessageId,
            NhAssistantPlans.ViewOf(part)!));
        if (IsRun(state) && services.GetService<NhAssistantAgentWorkUpdates>() is { } workUpdates)
        {
            // The run's result shows its plan: those who follow the run read it again. The update
            // names the run and carries none of the plan.
            await workUpdates.PublishAsync(
                state.Conversation.Id,
                NhAssistantAgentWorkReasons.Run,
                runId: state.Scope.RunId,
                cancellationToken: CancellationToken.None);
        }

        return NhAssistantPlans.Updated(plan);
    }
}
