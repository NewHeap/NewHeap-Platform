using System.Globalization;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Finds what became of an event that an agent trigger took: the run that consumed its queued input,
/// or the input while it waits; and reads the facts of a run by its id, such as its status, failure,
/// proposal count, summary and result. It is a server API for the application, which knows its own
/// events and runs; it checks no person's access to the subject, so show its result only to people the
/// application lets see the agent's work on that subject.
/// <para>
/// With a tenant, only work of that tenant is found, and work of the whole application when that
/// tenant takes part in it (<see cref="NhAssistantBuilder.UseSingleTenant"/>); work of another tenant
/// reads as not found. Without a tenant the lookup spans every tenant.
/// </para>
/// </summary>
public interface INhAssistantAgentRunLookup
{
    /// <summary>
    /// The first input of <paramref name="triggerId"/> with the event key <paramref name="eventKey"/>
    /// (the trigger's <c>Dedupe</c> value) in a thread of <paramref name="agentId"/>, live or shadow, and
    /// the run that consumed it; <see langword="null"/> when no such input exists (yet). A deferred fire
    /// writes its input only once the application's transaction committed and the fire ran. The lookup
    /// goes through the queued inputs, so an event that coalesced into a run that another event started
    /// finds that run too. The event keys of candidate versions differ from the live event's and are not
    /// found by it.
    /// </summary>
    Task<NhAssistantTriggerEventRun?> FindByEventAsync(
        string agentId,
        string triggerId,
        string eventKey,
        string? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The input with <paramref name="inputId"/>, as <see cref="NhAssistantTriggerReceipt.InputId"/>
    /// names it, and the run that consumed it; <see langword="null"/> when no such input exists (yet),
    /// for example a deferred fire whose transaction was rolled back or whose fire has not run.
    /// </summary>
    Task<NhAssistantTriggerEventRun?> FindByInputAsync(
        Guid inputId,
        string? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The facts of the run with <paramref name="runId"/>, live or shadow, for example one that
    /// <see cref="NhAssistantTriggerEventRun.RunId"/> or <see cref="NhAssistantTriggerReceipt.RunId"/>
    /// named: its agent, subject and thread, its trigger, how it worked and ended, how many proposals
    /// it made, its summary and its result; <see langword="null"/> when no such run exists or it
    /// belongs to another tenant. It reads what <c>GET runs/{id}</c> shows without the person checks
    /// of that endpoint, so show the facts only to people the application lets see the agent's work on
    /// the subject. The result is model output: untrusted text that is shown only with a safe Markdown
    /// renderer and never as HTML.
    /// </summary>
    Task<NhAssistantAgentRunFacts?> FindRunAsync(
        Guid runId,
        string? tenantId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What one agent run is and how it ended, as <see cref="INhAssistantAgentRunLookup.FindRunAsync"/>
/// reads it for the application. Content-wise it holds only the run's summary and result.
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="AgentId">The agent whose thread the run worked in.</param>
/// <param name="Subject">The subject of the agent's thread.</param>
/// <param name="ConversationId">The agent's thread.</param>
/// <param name="Shadow">Whether the run worked in the agent's shadow thread of the subject.</param>
/// <param name="TriggerId">The trigger that started the run, or a reserved trigger such as a start by hand (<see cref="NhAssistantTriggerIds"/>).</param>
/// <param name="TriggerKind">What started the run, one of <see cref="NhAssistantTriggerKinds"/>; <see langword="null"/> for a run created before it was recorded.</param>
/// <param name="TriggerVersion">The version of the trigger defined as data that started the run; <see langword="null"/> for a trigger in code.</param>
/// <param name="TriggerCandidate">Whether the run evaluated the candidate version of its data trigger.</param>
/// <param name="Kind">How the run worked, one of <see cref="NhAssistantRunKinds"/>.</param>
/// <param name="Status">The run's status, one of <see cref="NhAssistantRunStatuses"/>.</param>
/// <param name="FailureClass">How a failed or cancelled run failed, one of <see cref="NhAssistantRunFailureClasses"/>.</param>
/// <param name="FailureCode">The safe code of the failure, such as <c>assistant-budget-exhausted</c>.</param>
/// <param name="Attempt">The attempt the run is in or ended with.</param>
/// <param name="ProposalCount">The proposals the run recorded.</param>
/// <param name="Summary">The bounded summary the run ended with, which the agent's next runs start from.</param>
/// <param name="Result">
/// The run's whole answer as Markdown, at most 20,000 characters, in the caller's current UI culture
/// when the run's rules wrote it per culture: model output, untrusted text that is rendered only with a
/// safe Markdown renderer and never as HTML; <see langword="null"/> while the run has none.
/// </param>
/// <param name="ResultTruncated">The answer was longer than 20,000 characters and <paramref name="Result"/> is its beginning.</param>
/// <param name="CreatedAt">When the run was created.</param>
/// <param name="StartedAt">When the run started working; <see langword="null"/> while it waits.</param>
/// <param name="EndedAt">When the run ended; <see langword="null"/> while it works or waits.</param>
public sealed record NhAssistantAgentRunFacts(
    Guid RunId,
    string AgentId,
    NhAiSubjectRef Subject,
    Guid ConversationId,
    bool Shadow,
    string TriggerId,
    string? TriggerKind,
    int? TriggerVersion,
    bool TriggerCandidate,
    string Kind,
    string Status,
    string? FailureClass,
    string? FailureCode,
    int Attempt,
    int ProposalCount,
    string? Summary,
    string? Result,
    bool ResultTruncated,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>
/// An event in an agent's thread and the run that took it.
/// </summary>
/// <param name="InputId">The queued input that holds the event.</param>
/// <param name="AgentId">The agent whose thread holds the input.</param>
/// <param name="TriggerId">The trigger that fired; <see langword="null"/> for a person's message.</param>
/// <param name="EventKey">The event's dedupe key; <see langword="null"/> for an event without one.</param>
/// <param name="Subject">The subject of the agent's thread.</param>
/// <param name="ConversationId">The agent's thread.</param>
/// <param name="Shadow">Whether the thread is the agent's shadow thread of the subject.</param>
/// <param name="ReceivedAt">When the input was written.</param>
/// <param name="Waiting">Whether the input still waits for a run to claim it.</param>
/// <param name="ConsumedAt">When a run claimed the input.</param>
/// <param name="RunId">
/// The run that consumed the input. While it waits, the run that this event started, which claims it
/// when it starts; <see langword="null"/> for a waiting event that joined a run another event started or
/// that waits while its agent is paused.
/// </param>
/// <param name="RunStatus">The status of <paramref name="RunId"/>, one of <see cref="NhAssistantRunStatuses"/>.</param>
public sealed record NhAssistantTriggerEventRun(
    Guid InputId,
    string AgentId,
    string? TriggerId,
    string? EventKey,
    NhAiSubjectRef Subject,
    Guid ConversationId,
    bool Shadow,
    DateTimeOffset ReceivedAt,
    bool Waiting,
    DateTimeOffset? ConsumedAt,
    Guid? RunId,
    string? RunStatus);

internal sealed class NhAssistantAgentRunLookup(
    NhAssistantRunStore runs,
    NhAssistantRegistrationState registration,
    NhAssistantTenancy tenancy) : INhAssistantAgentRunLookup
{
    public async Task<NhAssistantTriggerEventRun?> FindByEventAsync(
        string agentId,
        string triggerId,
        string eventKey,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        ArgumentException.ThrowIfNullOrEmpty(eventKey);

        var found = await runs.FindEventInputAsync(
            agentId,
            triggerId,
            eventKey,
            tenantId,
            tenantId is not null && NhAssistantWholeApplicationTenancy.TakesPart(registration, tenantId),
            cancellationToken);
        if (found is not { } match)
        {
            return null;
        }

        return await DescribeAsync(match.Input, match.Thread, cancellationToken);
    }

    public async Task<NhAssistantTriggerEventRun?> FindByInputAsync(
        Guid inputId,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var found = await runs.FindInputWithThreadAsync(inputId, cancellationToken);
        if (found is not { } match
            || (tenantId is not null && !tenancy.Reaches(match.Thread, tenantId)))
        {
            return null;
        }

        return await DescribeAsync(match.Input, match.Thread, cancellationToken);
    }

    public async Task<NhAssistantAgentRunFacts?> FindRunAsync(
        Guid runId,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var match = await runs.FindRunWithThreadAsync(runId, cancellationToken);
        if (match is null
            || (tenantId is not null && !tenancy.Reaches(match.Thread, tenantId)))
        {
            return null;
        }

        var run = match.Run;
        var thread = match.Thread;
        // A result the rules wrote per culture is read in the caller's culture, as people read it.
        var (result, truncated) = NhAssistantRunResults.ResultOf(run, CultureInfo.CurrentUICulture);
        return new NhAssistantAgentRunFacts(
            run.Id,
            thread.AgentId,
            new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
            thread.ConversationId,
            thread.Shadow,
            run.TriggerId,
            run.TriggerKind,
            run.TriggerVersion,
            run.TriggerCandidate,
            run.Kind,
            run.Status,
            run.FailureClass,
            run.FailureCode,
            run.Attempt,
            run.ProposalCount,
            run.Summary,
            result,
            truncated,
            run.CreatedAt,
            run.StartedAt,
            run.EndedAt);
    }

    private async Task<NhAssistantTriggerEventRun> DescribeAsync(
        AssistantQueuedInput input,
        AssistantThread thread,
        CancellationToken cancellationToken)
    {
        AssistantRun? run = null;
        if (input.ConsumedByRunId is { } consumer)
        {
            run = await runs.FindRunAsync(consumer, cancellationToken);
        }
        else if (input.TriggerId is { } triggerId && input.EventKey is { } eventKey)
        {
            // The run this event started records its key and claims the input when it starts.
            run = await runs.FindRunByEventKeyAsync(input.ConversationId, triggerId, eventKey, cancellationToken);
        }

        return new NhAssistantTriggerEventRun(
            input.Id,
            thread.AgentId,
            input.TriggerId,
            input.EventKey,
            new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
            thread.ConversationId,
            thread.Shadow,
            input.CreatedAt,
            input.ConsumedAt is null,
            input.ConsumedAt,
            run?.Id,
            run?.Status);
    }
}
