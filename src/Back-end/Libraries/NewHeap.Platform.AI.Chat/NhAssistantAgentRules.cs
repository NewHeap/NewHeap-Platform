using System.Globalization;
using System.Text.Json;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Runs an agent's headless runs with application rules instead of a language model. Register it
/// with <c>ConfigureAgent(id, agent =&gt; agent.UseRuleHandler&lt;THandler&gt;())</c>. The handler
/// evaluates the subject's current state, proposes through
/// <see cref="NhAssistantRuleContext.ProposeAsync{TInput}"/> and ends the run with a summary, or hands
/// the run over to the agent's model when a case needs judgement. A rule turn calls no model.
/// </summary>
public interface INhAssistantAgentRuleHandler
{
    Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken);
}

/// <summary>
/// How a rule run ends: with a summary, or handed over to the agent's model.
/// </summary>
public sealed class NhAssistantRuleResult
{
    /// <summary>The longest summary a rule run writes into the thread.</summary>
    public const int MaxSummaryLength = 2_000;

    private NhAssistantRuleResult(string? summary, string? handOverReason, string? skillId = null)
    {
        Summary = summary;
        HandOverReason = handOverReason;
        SkillId = skillId;
    }

    /// <summary>
    /// The skill the rules route the run to; <see langword="null"/> otherwise.
    /// </summary>
    public string? SkillId { get; }

    public bool UsesSkill => SkillId is not null;

    /// <summary>
    /// What the rules did, written to the thread as the agent's message and kept as the run summary.
    /// </summary>
    public string? Summary { get; }

    /// <summary>
    /// Why the rules hand the run over to the model; <see langword="null"/> when they finished it.
    /// </summary>
    public string? HandOverReason { get; }

    public bool HandsOver => HandOverReason is not null;

    /// <summary>
    /// Ends the run. <paramref name="summary"/> says what the rules did, for example which
    /// proposals they made or that nothing needed doing.
    /// </summary>
    public static NhAssistantRuleResult Done(string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (summary.Length > MaxSummaryLength)
        {
            throw new ArgumentException($"A rule summary has at most {MaxSummaryLength} characters.", nameof(summary));
        }

        return new NhAssistantRuleResult(summary.Trim(), null);
    }

    /// <summary>
    /// Continues the same run with the agent's model, which reads <paramref name="reason"/>.
    /// Proposals the rules already made stay.
    /// </summary>
    public static NhAssistantRuleResult HandOver(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > MaxSummaryLength)
        {
            throw new ArgumentException($"A hand-over reason has at most {MaxSummaryLength} characters.", nameof(reason));
        }

        return new NhAssistantRuleResult(null, reason.Trim());
    }

    /// <summary>
    /// Continues the same run in one of the agent's skills, with <paramref name="reason"/> as the
    /// first section of its briefing. Proposals the rules already made stay. A skill that is paused
    /// lets the run's events wait for it; a stopped skill ends the run.
    /// </summary>
    public static NhAssistantRuleResult UseSkill(string skillId, string reason)
    {
        if (!NhAiSubjectRef.IsValidType(skillId))
        {
            throw new ArgumentException("A skill id is a dash-case identifier.", nameof(skillId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > MaxSummaryLength)
        {
            throw new ArgumentException($"A routing reason has at most {MaxSummaryLength} characters.", nameof(reason));
        }

        return new NhAssistantRuleResult(null, reason.Trim(), skillId);
    }
}

/// <summary>
/// One input of a rule run: an event, schedule tick, webhook call, request or follow-up.
/// </summary>
/// <param name="TriggerId">The trigger that wrote it.</param>
/// <param name="Text">The trigger's message.</param>
/// <param name="PayloadJson">The payload the trigger stored with the event, as JSON.</param>
public sealed record NhAssistantRuleInput(
    string? TriggerId,
    string Text,
    string? PayloadJson,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// The payload the trigger stored with <c>Payload(...)</c>, or <see langword="null"/>.
    /// </summary>
    public TPayload? GetPayload<TPayload>()
    {
        return PayloadJson is null ? default : JsonSerializer.Deserialize<TPayload>(PayloadJson, JsonSerializerOptions.Web);
    }
}

/// <summary>
/// An earlier proposal of the thread that people rejected or that closed because it no longer fit.
/// </summary>
/// <param name="ClosedAt">When it was rejected or closed.</param>
/// <param name="Kind">For an invalidation, one of <see cref="NhAssistantStaleKinds"/>.</param>
public sealed record NhAssistantRuleDecision(
    Guid ApprovalId,
    string ToolId,
    DateTimeOffset ClosedAt,
    string? Kind);

/// <summary>
/// What became of a proposal a rule asked for.
/// </summary>
/// <param name="ApprovalId">The recorded proposal, or the open or rejected one that made it unnecessary.</param>
/// <param name="Superseded">Pending proposals for the same objects that the new one replaced.</param>
/// <param name="RejectedAt">When people rejected the same proposal, for <see cref="NhAssistantProposeOutcome.RecentlyRejected"/>.</param>
/// <param name="Code">Why a proposal was refused.</param>
public sealed record NhAssistantRuleProposal(
    NhAssistantProposeOutcome Outcome,
    Guid? ApprovalId,
    IReadOnlyList<Guid> Superseded,
    DateTimeOffset? RejectedAt,
    string? Code)
{
    internal static NhAssistantRuleProposal Refused(string code)
    {
        return new NhAssistantRuleProposal(NhAssistantProposeOutcome.Refused, null, [], null, code);
    }
}

/// <summary>
/// What became of a request to record a proposal.
/// </summary>
public enum NhAssistantProposeOutcome
{
    /// <summary>The proposal was recorded and waits for people.</summary>
    Created = 0,

    /// <summary>
    /// The same proposal, or one for the same objects, already waits for a decision or its execution.
    /// </summary>
    AlreadyOpen = 1,

    /// <summary>People rejected the same proposal within the policy's rejection cooldown.</summary>
    RecentlyRejected = 2,

    /// <summary>
    /// The tool may not be proposed this way: the agent may not use it, it needs no approval or the
    /// arguments do not fit.
    /// </summary>
    Refused = 3
}

/// <summary>
/// What a rule handler knows about its run and what it may do. Read the application's own data
/// directly; propose through <see cref="ProposeAsync{TInput}"/>.
/// </summary>
public sealed class NhAssistantRuleContext
{
    private readonly Func<string, object, Func<CultureInfo, string?>?, CancellationToken, Task<NhAssistantRuleProposal>> _propose;
    private readonly Func<object, CancellationToken, Task<bool>> _updateMemory;
    private readonly Func<string, NhAiSubjectRef, string, object?, CancellationToken, Task<NhAssistantHandOffReceipt>> _handOff;

    internal NhAssistantRuleContext(
        string agentId,
        Guid runId,
        string triggerId,
        NhAiSubjectInfo subject,
        string? initiatorActorId,
        string accountableOwnerId,
        bool shadow,
        IReadOnlyList<NhAssistantRuleInput> inputs,
        string? memoryJson,
        IReadOnlyList<string> recentSummaries,
        IReadOnlyList<NhAssistantRuleDecision> recentRejections,
        IReadOnlyList<NhAssistantRuleDecision> recentInvalidations,
        Func<string, object, Func<CultureInfo, string?>?, CancellationToken, Task<NhAssistantRuleProposal>> propose,
        Func<object, CancellationToken, Task<bool>> updateMemory,
        Func<string, NhAiSubjectRef, string, object?, CancellationToken, Task<NhAssistantHandOffReceipt>> handOff)
    {
        AgentId = agentId;
        RunId = runId;
        TriggerId = triggerId;
        Subject = subject;
        InitiatorActorId = initiatorActorId;
        AccountableOwnerId = accountableOwnerId;
        Shadow = shadow;
        Inputs = inputs;
        MemoryJson = memoryJson;
        RecentSummaries = recentSummaries;
        RecentRejections = recentRejections;
        RecentInvalidations = recentInvalidations;
        _propose = propose;
        _updateMemory = updateMemory;
        _handOff = handOff;
    }

    public string AgentId { get; }

    public Guid RunId { get; }

    /// <summary>The trigger that started the run, such as an event trigger or <c>manual</c>.</summary>
    public string TriggerId { get; }

    /// <summary>The subject as its resolver describes it now.</summary>
    public NhAiSubjectInfo Subject { get; }

    public string? InitiatorActorId { get; }

    public string AccountableOwnerId { get; }

    /// <summary>The run is a shadow run: its proposals are recorded for evaluation only.</summary>
    public bool Shadow { get; }

    /// <summary>The inputs the run received, oldest first.</summary>
    public IReadOnlyList<NhAssistantRuleInput> Inputs { get; }

    /// <summary>The thread's working memory as JSON, or <see langword="null"/>.</summary>
    public string? MemoryJson { get; }

    /// <summary>The summaries of the thread's last runs, newest first.</summary>
    public IReadOnlyList<string> RecentSummaries { get; }

    /// <summary>Proposals of the thread that people rejected recently, newest first.</summary>
    public IReadOnlyList<NhAssistantRuleDecision> RecentRejections { get; }

    /// <summary>
    /// Proposals of the thread that closed recently because the work was already done or no longer
    /// applied, newest first.
    /// </summary>
    public IReadOnlyList<NhAssistantRuleDecision> RecentInvalidations { get; }

    /// <summary>The working memory as <typeparamref name="TMemory"/>, or <see langword="null"/>.</summary>
    public TMemory? GetMemory<TMemory>()
        where TMemory : class
    {
        return MemoryJson is null ? null : JsonSerializer.Deserialize<TMemory>(MemoryJson, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// Records a proposal of <paramref name="toolId"/> through the same governed flow as a model's
    /// tool call: the agent's tools and grants, the approval policy (always deferred for rules), the
    /// presentation in every supported culture, guards, deduplication, audit and live updates.
    /// <paramref name="rationale"/> says why, in each culture; approvers read it with the proposal
    /// and the model reads it when a person asks about it in the thread. Only tools that change
    /// state can be proposed; nothing executes before people approve.
    /// </summary>
    public Task<NhAssistantRuleProposal> ProposeAsync<TInput>(
        string toolId,
        TInput arguments,
        Func<CultureInfo, string?>? rationale = null,
        CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(arguments);
        return _propose(toolId, arguments, rationale, cancellationToken);
    }

    /// <summary>
    /// Applies a JSON Merge Patch to the working memory, under the same schema rules as the model's
    /// memory function. Returns false when the patch does not fit the schema.
    /// </summary>
    public Task<bool> UpdateMemoryAsync(object patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return _updateMemory(patch, cancellationToken);
    }

    /// <summary>
    /// Hands work on <paramref name="subject"/> to another agent that accepts hand-offs from this
    /// agent (<c>AcceptHandOffs</c>). The target works in its own thread, under its own grants and
    /// approval policies, with <paramref name="message"/> as data from this agent and
    /// <paramref name="payload"/> for its rule handler. The subject must belong to the same scope
    /// key as this run's subject. A paused target lets the work wait; a refusal carries its code.
    /// The same run hands the same subject to the same agent once.
    /// </summary>
    public Task<NhAssistantHandOffReceipt> HandOffAsync(
        string targetAgentId,
        NhAiSubjectRef subject,
        string message,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        NhAssistantNames.ValidateSegment(targetAgentId, nameof(targetAgentId));
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > NhAssistantHandOffTools.MaxMessageChars)
        {
            throw new ArgumentException(
                $"A hand-off message has at most {NhAssistantHandOffTools.MaxMessageChars} characters.",
                nameof(message));
        }

        return _handOff(targetAgentId, subject, message.Trim(), payload, cancellationToken);
    }
}
