using System.Text.Json;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Mutable state of one running turn. Only the turn's own asynchronous flow touches it.
/// </summary>
internal sealed class NhAssistantTurnState
{
    private readonly object _sync = new();
    private readonly List<NhAssistantStoredPart> _parts = [];

    public required NhAssistantTurnScope Scope { get; init; }

    public required AssistantConversation Conversation { get; init; }

    public required NhAssistantAgent EffectiveAgent { get; init; }

    /// <summary>
    /// The agent side of a turn in an agent thread; <see langword="null"/> in a chat.
    /// </summary>
    public NhAssistantAgentTurnContext? AgentTurn { get; init; }

    /// <summary>
    /// Proposals this turn recorded, inline or deferred.
    /// </summary>
    public int ProposalCount { get; set; }

    /// <summary>
    /// How the turn ended, set when it ends: one of <see cref="NhAssistantTurnStatuses"/>.
    /// </summary>
    public string? OutcomeStatus { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>
    /// Tools of the MCP servers assigned to the agent, loaded once per turn.
    /// </summary>
    public NhAssistantMcpToolSet? McpTools { get; set; }

    /// <summary>
    /// The personal MCP servers a headless turn left out, kept when its tools are released so the
    /// turn's result can name them.
    /// </summary>
    public IReadOnlyList<NhAssistantMcpMissingConnection> MissingMcpConnections { get; set; } = [];

    /// <summary>
    /// The servers whose connection card this turn showed, for its tools or its readers; each once.
    /// </summary>
    public HashSet<string> AnnouncedConnections { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The application map entries the acting person may read, loaded once per turn of an agent that
    /// uses the map.
    /// </summary>
    public IReadOnlyList<NhAiApplicationMapEntry>? ApplicationMapEntries { get; set; }

    /// <summary>
    /// The retrieved context of the turn, its hash and the highest classification of its items,
    /// retrieved once before the first model call.
    /// </summary>
    public (string Block, string Hash, NhAiDataClassification Classification)? RetrievedContext { get; set; }

    /// <summary>
    /// The highest classification of the retrieved context the model of this turn reads;
    /// <see cref="NhAiDataClassification.Public"/> without retrieved context.
    /// </summary>
    public NhAiDataClassification RetrievedClassification => RetrievedContext?.Classification ?? NhAiDataClassification.Public;

    /// <summary>
    /// Assistant functions of an agent turn next to its tools, such as the working memory.
    /// </summary>
    public IReadOnlyList<AIFunction> ExtraFunctions { get; set; } = [];

    public required Guid UserMessageId { get; init; }

    public required Guid AssistantMessageId { get; init; }

    public required ChannelWriter<NhAssistantTurnEvent> Events { get; init; }

    /// <summary>
    /// Shares every event with the other open sessions of the owner and participants. It never throws.
    /// </summary>
    public Func<NhAssistantTurnEvent, ValueTask>? Broadcast { get; init; }

    /// <summary>
    /// When this run of the turn started; a resumed turn starts again after the decision.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public NhAssistantApprovalView? PendingApproval { get; set; }

    /// <summary>
    /// The question this turn waits for (LS21); set once its <c>ask_question</c> call was recorded.
    /// </summary>
    public NhAssistantQuestionView? PendingQuestion { get; set; }

    /// <summary>
    /// Set by <c>ask_question</c> once the question passed its checks; the interceptor then records it
    /// with the call and ends the model loop.
    /// </summary>
    public NhAssistantAskedQuestion? AskedQuestion { get; set; }

    /// <summary>
    /// Whether the turn waits for a person: an inline approval or a question.
    /// </summary>
    public bool WaitsForPerson => PendingApproval is not null || PendingQuestion is not null;

    /// <summary>
    /// The run settings of the agent in a person's chat that offers its skills or questions
    /// (<c>SkillsInChat</c>, <c>AllowQuestions</c>); <see langword="null"/> in any other chat and
    /// outside chats, where <see cref="AgentTurn"/> carries the run settings.
    /// </summary>
    public NhAssistantAgentRunOptions? ChatOptions { get; set; }

    public bool ToolCallLimitReached { get; set; }

    /// <summary>
    /// The token ceiling of the run in the skill it works in; <see langword="null"/> without one or
    /// outside a run. A skill switch changes it.
    /// </summary>
    public long? TokenCeiling { get; set; }

    /// <summary>
    /// The tokens earlier turns of the same execution of the run used.
    /// </summary>
    public long TokensBefore { get; init; }

    /// <summary>
    /// The skill the turn works in; <see langword="null"/> for the agent's own tools.
    /// </summary>
    public NhAssistantSkillOptions? Skill { get; set; }

    /// <summary>
    /// Who chose <see cref="Skill"/>: one of <see cref="NhAssistantSkillChoosers"/>.
    /// </summary>
    public string? SkillChosenBy { get; set; }

    /// <summary>
    /// The active skills of the agent when the turn started.
    /// </summary>
    public IReadOnlyList<NhAssistantSkillOptions> ActiveSkills { get; set; } = [];

    /// <summary>
    /// The stored states of the agent's skills that are not active.
    /// </summary>
    public IReadOnlyDictionary<string, string> SkillStates { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// The skills that work in shadow while the turn works in a live thread: <c>use_skill</c> does
    /// not offer them, and a run that rules or triage route to one hands its events to the shadow
    /// thread. Empty in a shadow thread, where every skill works in shadow.
    /// </summary>
    public IReadOnlySet<string> ShadowSkills { get; set; } = new HashSet<string>();

    public int SkillSwitches { get; set; }

    /// <summary>
    /// Set by <c>use_skill</c>: the skill the model continues in once the current loop ended.
    /// </summary>
    public NhAssistantSkillOptions? RequestedSkill { get; set; }

    public string? RequestedSkillReason { get; set; }

    /// <summary>
    /// The language, client context and speakers the prompt was composed with, to compose it again
    /// for a skill.
    /// </summary>
    public string Language { get; init; } = "en";

    public NhAssistantClientContext? ClientContext { get; init; }

    public Collaboration.NhAssistantSpeakers? Speakers { get; init; }

    /// <summary>
    /// Set when the run's token ceiling ended the model loop before the next model call.
    /// </summary>
    public bool RunTokenLimitReached { get; set; }

    /// <summary>
    /// Whether the turn used its token allowance, so no further model call may start.
    /// </summary>
    public bool TokenAllowanceUsed => TokenCeiling is { } ceiling && TokensBefore + InputTokens + OutputTokens >= ceiling;

    /// <summary>
    /// Set when the durable daily tool budget refused a tool call in this turn.
    /// </summary>
    public bool ToolBudgetExhausted { get; set; }

    public bool InstructionsTooLong { get; set; }

    /// <summary>
    /// No profile of the turn's model call permits its data classification; the turn fails with
    /// <see cref="NhAssistantErrorCodes.ClassificationNotPermitted"/>.
    /// </summary>
    public bool ClassificationNotPermitted { get; set; }

    /// <summary>
    /// No profile of the turn's model call permits the execution region for its data classification;
    /// the turn fails with <see cref="NhAssistantErrorCodes.ModelRegionDenied"/>.
    /// </summary>
    public bool RegionNotPermitted { get; set; }

    /// <summary>
    /// The files this turn gave the model, from attachments and tool results; the turn's file limits
    /// bound them.
    /// </summary>
    public List<NhAssistantTurnFile> Files { get; } = [];

    /// <summary>
    /// The files of earlier turns this turn opened again; the same limits bound them on their own.
    /// </summary>
    public List<NhAssistantTurnFile> EarlierFiles { get; } = [];

    /// <summary>
    /// Whether the model of this turn reads files.
    /// </summary>
    public bool HasFiles => Files.Count > 0 || EarlierFiles.Count > 0;

    /// <summary>
    /// The highest classification of the files the model of this turn reads;
    /// <see cref="NhAiDataClassification.Public"/> without files.
    /// </summary>
    public NhAiDataClassification FileClassification => Files.Concat(EarlierFiles)
        .Select(file => file.Classification)
        .DefaultIfEmpty(NhAiDataClassification.Public)
        .Max();

    /// <summary>
    /// Set after a rejected approval and before the closing answer after a tool limit: the model may
    /// give one closing message but call no more tools.
    /// </summary>
    public bool ToolsDisabled { get; set; }

    /// <summary>
    /// The agent's rule handler runs the turn without a model; its proposals always wait deferred.
    /// </summary>
    public bool RuleTurn { get; set; }

    /// <summary>
    /// The agent's rules handed the turn over to the model.
    /// </summary>
    public bool HandedOver { get; set; }

    /// <summary>
    /// A read of the agent's own grant succeeded while its results are restricted per reader, so the
    /// run's summary may hold what some readers may not see.
    /// </summary>
    public bool UsedRestrictedResults { get; set; }

    /// <summary>
    /// The agent's or a skill's rules ran in this turn.
    /// </summary>
    public bool RulesRan { get; set; }

    /// <summary>
    /// Why the agent's rules made each proposal of the turn, in the run's culture.
    /// </summary>
    public List<string> RuleRationales { get; } = [];

    /// <summary>
    /// Why the agent's rules made each proposal of the turn, in any culture, for the turn's answer per
    /// culture.
    /// </summary>
    public List<Func<CultureInfo, string?>> RuleRationaleWriters { get; } = [];

    /// <summary>
    /// What became of each proposal the agent's rules asked for in this turn, with its tool, in order,
    /// for the model's briefing when the rules hand the run over.
    /// </summary>
    public List<NhAssistantRuleProposalNote> RuleProposals { get; } = [];

    /// <summary>
    /// The recent rejections of the thread the rules were given (<see cref="NhAssistantRuleContext.RecentRejections"/>).
    /// </summary>
    public IReadOnlyList<NhAssistantRuleDecision> RuleRejections { get; set; } = [];

    /// <summary>
    /// The recent invalidations of the thread the rules were given (<see cref="NhAssistantRuleContext.RecentInvalidations"/>).
    /// </summary>
    public IReadOnlyList<NhAssistantRuleDecision> RuleInvalidations { get; set; } = [];

    /// <summary>
    /// The answer of a rule turn in each supported UI culture, by culture name; <see langword="null"/>
    /// for a model's answer or an application with one culture.
    /// </summary>
    public IReadOnlyDictionary<string, string>? LocalizedAnswer { get; set; }

    /// <summary>
    /// How the turn worked: one of the values in <see cref="NhAssistantRunKinds"/>.
    /// </summary>
    public string Kind => HandedOver
        ? NhAssistantRunKinds.RuleThenModel
        : RuleTurn ? NhAssistantRunKinds.Rule : NhAssistantRunKinds.Model;

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int ToolCalls { get; set; }

    public IReadOnlyList<NhAssistantStoredPart> Parts
    {
        get
        {
            lock (_sync)
            {
                return _parts.ToArray();
            }
        }
    }

    public void AppendText(string text)
    {
        lock (_sync)
        {
            if (_parts.Count > 0 && _parts[^1].Type == NhAssistantStoredPart.TextType)
            {
                _parts[^1] = _parts[^1] with { Text = _parts[^1].Text + text };
                return;
            }
            _parts.Add(new NhAssistantStoredPart(NhAssistantStoredPart.TextType, Text: text));
        }
    }

    public void AddPart(NhAssistantStoredPart part)
    {
        lock (_sync)
        {
            _parts.Add(part);
        }
    }

    public ValueTask EmitAsync(NhAssistantTurnEvent evt)
    {
        // The stream is unbounded; a disconnected client never blocks the turn.
        Events.TryWrite(evt);
        return Broadcast?.Invoke(evt) ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// Result of one governed function execution inside a turn.
/// </summary>
internal sealed record NhAssistantToolExecution(
    object? Result,
    NhAssistantToolOutcome Outcome,
    NhAssistantToolCallScope Call)
{
    /// <summary>
    /// The tool threw instead of returning a result, so a side effect may or may not have
    /// happened.
    /// </summary>
    public bool Threw { get; init; }
}

/// <summary>
/// Agent Framework function middleware that turns every governed tool call into persisted,
/// streamed and audited assistant state. It never bypasses the governed function: the call runs
/// through <c>next</c>, which executes the <see cref="INhAiGovernedAIFunction"/> and its shared
/// <see cref="INhAiToolInvoker"/>. When the invoker reports a missing approval, the interceptor
/// creates the exact NewHeap proposal from the arguments the invoker evaluated, pauses the turn and
/// terminates the model loop.
/// </summary>
internal sealed class NhAssistantToolCallInterceptor(
    NhAssistantTurnState state,
    INhAssistantStore store,
    INhAiProposalFactory proposalFactory,
    NhAssistantToolPresentationResolver toolPresentation,
    IReadOnlyList<INhAssistantBusinessAuditSink> businessSinks,
    ILogger? logger = null,
    NhAssistantApprovalPolicies? approvalPolicies = null,
    NhAssistantProposalGuards? proposalGuards = null,
    NhAssistantProposalDuplicates? duplicates = null,
    NhAssistantProposalInvalidation? invalidation = null,
    NhAssistantStaleExplanations? texts = null,
    NhAssistantProposalPlacement? placements = null,
    Live.NhAssistantAgentWorkUpdates? workUpdates = null,
    TimeProvider? applicationClock = null,
    NhAssistantTurnFiles? turnFiles = null,
    NhAssistantConsultationProposals? consultationProposals = null,
    NhAssistantRegistrationState? registration = null)
{
    public const string ToolsDisabledCode = "assistant-tools-disabled";
    public const string ProposalRecordedCode = "assistant-proposal-recorded";

    private IReadOnlyDictionary<string, NhAiToolDescriptor> _offeredTools =
        new Dictionary<string, NhAiToolDescriptor>(StringComparer.Ordinal);

    /// <summary>
    /// The rationale of the rule proposal being recorded, and the outcome of the last proposal.
    /// </summary>
    private Func<CultureInfo, string?>? _rationale;
    private NhAssistantRecordedProposal? _lastProposal;

    /// <summary>
    /// The application's clock, which a proposal's lifetime and reminders follow.
    /// </summary>
    private readonly TimeProvider _applicationClock = applicationClock ?? TimeProvider.System;

    /// <summary>
    /// Whether the application executes the approved proposals of <paramref name="toolId"/>
    /// (<see cref="NhAssistantBuilder.ExecuteInApplication"/>).
    /// </summary>
    private bool ExecutedByApplication(string toolId)
    {
        return registration?.TryGetApplicationExecution(toolId, out _) == true;
    }

    /// <summary>
    /// Registers the governed descriptors the adapter offered to the agent, keyed by function name.
    /// Agent Framework middleware may wrap the governed function; the call itself still runs through it.
    /// </summary>
    public void UseOfferedTools(IEnumerable<NhAiToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var offered = new Dictionary<string, NhAiToolDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (!string.IsNullOrWhiteSpace(descriptor.ExportName) && !offered.TryAdd(descriptor.ExportName, descriptor))
            {
                throw new InvalidOperationException(
                    $"Two offered assistant tools share the function name '{descriptor.ExportName}'.");
            }
        }
        _offeredTools = offered;
    }

    private NhAiToolDescriptor? FindOffered(string functionName)
    {
        return _offeredTools.TryGetValue(functionName, out var descriptor) ? descriptor : null;
    }

    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var descriptor = FindGoverned(context.Function)?.Descriptor
            ?? FindOffered(context.Function.Name)
            ?? FindMemory(context.Function)?.Descriptor
            ?? throw new InvalidOperationException(
                "The assistant only executes governed NewHeap AI functions.");

        if (state.ToolsDisabled)
        {
            context.Terminate = true;
            return Refusal(ToolsDisabledCode);
        }
        if (state.TokenAllowanceUsed)
        {
            // The result of this call would go to another model call, which the run's ceiling
            // forbids; nothing runs, and the loop ends.
            state.RunTokenLimitReached = true;
            context.Terminate = true;
            return Refusal(NhAssistantErrorCodes.RunTokenLimit);
        }
        if (state.Scope.Consultation is { } consultation
            && descriptor.Effect != NhAiToolEffect.ReadOnly
            && !NhAssistantTurnRunner.OffersEffects(state.Scope.Agent, consultation))
        {
            // Such tools are not offered; a model that names one anyway changes nothing.
            return Refusal(NhAssistantErrorCodes.ConsultReadOnly);
        }
        if (state.Scope.Consultation is { } selecting
            && FindMemory(context.Function) is null
            && !selecting.Options.Selects(descriptor.Id))
        {
            // A tool outside the consultation's selection is not offered either; nothing runs.
            return Refusal(NhAssistantErrorCodes.ConsultToolNotSelected);
        }
        if (state.Scope.IncrementToolCalls() > state.Scope.Limits.MaxToolCallsPerTurn)
        {
            state.ToolCallLimitReached = true;
            context.Terminate = true;
            return Refusal(NhAssistantErrorCodes.ToolCallLimitReached);
        }

        var presentationCulture = CultureInfo.CurrentUICulture;
        var displayName = await toolPresentation.ResolveDisplayNameAsync(
            descriptor,
            presentationCulture,
            cancellationToken);
        var row = await BeginToolCallAsync(
            descriptor,
            context.CallContent.CallId,
            context.Function.Name,
            context.Arguments,
            displayName,
            cancellationToken);

        // A run's call that names another object than the run's own is checked against that object
        // before it runs, and runs in that object's scope (K11, K12).
        var placement = placements is not null
            && state.AgentTurn is { } agentTurn
            && NhAssistantProposalPlacement.Applies(agentTurn, state.Scope.Consultation is not null, descriptor)
                ? await placements.DecideAsync(agentTurn, descriptor, context.Function, context.Arguments, cancellationToken)
                : NhAssistantPlacementDecision.Own;
        if (placement.Refused)
        {
            return await NotPlacedAsync(descriptor, row, placement.RefusalReason!, placement.Named, cancellationToken);
        }

        NhAiAgentInvocationScope? targetScope = null;
        if (placement.Target is { } placedAt)
        {
            // Every call of a run works in the agent's invocation scope; the object's derives from it.
            var currentScope = NhAiAgentInvocationScopes.Current
                ?? throw new InvalidOperationException("A tool call of an agent run has no agent invocation scope.");
            targetScope = currentScope with
            {
                Subject = placedAt,
                // A call for another object never changes it directly: every effect is a proposal.
                RequireApprovalForEffects = true
            };
        }
        var execution = await ExecuteAsync(
            descriptor,
            row,
            null,
            null,
            token => InvokeInScopeAsync(targetScope, () => next(context, token)),
            cancellationToken,
            logger);
        if (execution.Outcome.Kind == NhAssistantToolOutcomeKind.ApprovalMissing)
        {
            NhAssistantPlacedThread? placed = null;
            if (placement.Target is { } target)
            {
                if (!NhAssistantProposalPlacement.Confirms(placement, descriptor, execution.Call.CapturedArguments))
                {
                    return await NotPlacedAsync(descriptor, row, NhAssistantPlacementReasons.NotFound, target.Subject, cancellationToken);
                }

                // The thread at the object is created only once every check passed, right before
                // the proposal is recorded there.
                var thread = await placements!.PrepareThreadAtAsync(state.EffectiveAgent, target, state.AgentTurn!.Thread.Shadow, cancellationToken);
                if (!thread.Success)
                {
                    // Nobody answers for the agent at the object, so it cannot wait there.
                    return await NotPlacedAsync(descriptor, row, NhAssistantProposalPlacement.ReasonOf(thread), target.Subject, cancellationToken);
                }

                placed = new NhAssistantPlacedThread(thread.Data!, target);
            }

            var requested = await RequestApprovalAsync(
                descriptor,
                row,
                execution.Call,
                presentationCulture,
                cancellationToken,
                placed);
            if (requested is { NotPlacedReason: { } notPlaced } && placed is not null)
            {
                // Rules read the reason from the call's result, as for a refusal before the call ran.
                return await NotPlacedAsync(descriptor, row, notPlaced, placed.Subject.Subject, cancellationToken);
            }
            _lastProposal = requested;
            if (requested is { ConsultationRefusal: { } refusal })
            {
                // No route of the consultation applies; the consulted model describes the change.
                return NhAssistantConsultationProposals.RefusalResult(refusal);
            }
            if (requested is { Outcome: NhAssistantProposeOutcome.NotNeeded } notNeeded)
            {
                // A guard found the work done or no longer applicable; nothing waits for people.
                return NotNeeded(notNeeded);
            }
            if (requested is { Outcome: not NhAssistantProposeOutcome.Created } skipped)
            {
                // The same proposal is open or was rejected recently; nothing new waits for people.
                return Duplicate(skipped);
            }
            if (requested is { PlacedAt: { } placedSubject } placedProposal)
            {
                // The proposal waits at the object for its approvers while the run continues.
                return ProposalPlaced(placedProposal.ProposalId, placedSubject);
            }
            if (requested is { ConsultationRoute: { } route } consulted)
            {
                // The consulted model goes on and tells the asker who decides.
                return ConsultationProposalRecorded(consulted.ProposalId, route);
            }
            if (requested is { Mode: NhAiApprovalMode.Inline } inline && !inline.Shadow)
            {
                context.Terminate = true;
                return execution.Result;
            }

            if (requested is { } recorded)
            {
                // A deferred or shadow proposal waits for people while the model continues.
                return ProposalRecorded(recorded.ProposalId);
            }
        }

        if (execution.Outcome.Kind == NhAssistantToolOutcomeKind.Succeeded
            && execution.Call.AskerDecision is { } askerDecision
            && state.Scope.Consultation is null)
        {
            // A consultation left a proposal this turn's person decides now: the turn waits for it.
            context.Terminate = true;
            return await AwaitAskerDecisionAsync(row, execution, askerDecision, cancellationToken);
        }
        if (state.AskedQuestion is { } asked && state.PendingQuestion is null)
        {
            // ask_question: the turn waits for the addressee's answer (LS21).
            context.Terminate = true;
            return await AwaitAnswerAsync(row, asked, cancellationToken);
        }

        var withFiles = await WithFilesAsync(descriptor, row, execution, cancellationToken);
        await CompleteToolCallAsync(row, withFiles.Execution, cancellationToken);
        if (state.RequestedSkill is not null)
        {
            // use_skill: the loop ends here and the turn continues in the chosen skill.
            context.Terminate = true;
        }
        if (state.Scope.BudgetExhausted)
        {
            // A reservation inside a tool call is a tool-budget reservation.
            state.ToolBudgetExhausted = true;
            context.Terminate = true;
        }
        return withFiles.ModelResult;
    }

    /// <summary>
    /// Opens the files a successful tool result names, through the application's file source with the
    /// actor of the turn, so a tool can never hand the model a file that actor may not read. The
    /// result then names, per file, whether the model received it or why not; the thread and the
    /// audit keep references and hashes only. Files the model receives follow the tool results of the
    /// round as untrusted external data (<see cref="NhAiToolResultWithContent"/>).
    /// </summary>
    internal async Task<(NhAssistantToolExecution Execution, object? ModelResult)> WithFilesAsync(
        NhAiToolDescriptor descriptor,
        AssistantToolInvocation row,
        NhAssistantToolExecution execution,
        CancellationToken cancellationToken)
    {
        if (turnFiles is null
            || state.RuleTurn
            || execution.Outcome.Kind != NhAssistantToolOutcomeKind.Succeeded
            || execution.Result is not JsonElement { ValueKind: JsonValueKind.Object } result
            || !result.TryGetProperty(NhAiToolResult.FilesPropertyName, out _))
        {
            return (execution, execution.Result);
        }

        var valid = NhAiToolResult.TryGetFiles(result, out var references);
        var outcomes = valid
            ? await turnFiles.OpenAsync(state, references, NhAssistantFileOrigins.ToolResult, descriptor.Id, cancellationToken)
            : [];
        var described = NhAssistantTurnFiles.DescribeInResult(result, outcomes, !valid);
        foreach (var outcome in outcomes)
        {
            if (outcome.File is { } file)
            {
                state.AddPart(new NhAssistantStoredPart(
                    NhAssistantStoredPart.FileType,
                    InvocationId: row.Id,
                    File: NhAssistantStoredFile.From(outcome.Reference, file.Sha256, NhAssistantFileOrigins.ToolResult, descriptor.Id)));
            }
            await RecordBusinessEventAsync(NhAssistantTurnFiles.AuditEventOf(state, outcome, row.Id), cancellationToken);
        }
        // A reader the person has to connect first shows its card, as a server's tools do (D33).
        await AnnounceConnectionsAsync(NhAssistantTurnFiles.ConnectionsNeededOf(outcomes), cancellationToken);

        var contents = outcomes.SelectMany(outcome => outcome.Contents).ToArray();
        return (
            execution with { Result = described },
            contents.Length > 0 ? new NhAiToolResultWithContent(described, contents) : described);
    }

    /// <summary>
    /// Shows the person of a request turn which servers need their connection: a
    /// <c>connection-required</c> part and event per server, once per turn, whether the server's tools
    /// or one of its readers asked for it. The part survives a reload; a consultation shows none.
    /// </summary>
    public async Task AnnounceConnectionsAsync(
        IEnumerable<NhAssistantMcpConnectionNeeded> needed,
        CancellationToken cancellationToken)
    {
        if (state.Scope.Consultation is not null)
        {
            return;
        }

        var announced = needed.Where(item => state.AnnouncedConnections.Add(item.ServerId)).ToArray();
        if (announced.Length == 0)
        {
            return;
        }

        foreach (var item in announced)
        {
            state.AddPart(new NhAssistantStoredPart(
                NhAssistantStoredPart.ConnectionRequiredType,
                ServerId: item.ServerId,
                DisplayName: item.DisplayName,
                Reason: item.Reason));
        }
        await FlushAsync(cancellationToken);
        foreach (var item in announced)
        {
            await state.EmitAsync(new NhAssistantConnectionRequiredEvent(item.ServerId, item.DisplayName, item.Reason));
        }
    }

    /// <summary>
    /// Runs the call in the scope of the object it concerns, when it concerns another one than the
    /// run's own: the invocation gate then authorizes the tool against that object and its division,
    /// and the proposal carries that object's execution scope.
    /// </summary>
    private static async ValueTask<object?> InvokeInScopeAsync(
        NhAiAgentInvocationScope? scope,
        Func<ValueTask<object?>> invoke)
    {
        if (scope is null)
        {
            return await invoke();
        }

        using (NhAiAgentInvocationScopes.Begin(scope))
        {
            return await invoke();
        }
    }

    /// <summary>
    /// Ends a call whose proposal concerns another object that refused it: nothing ran, the run
    /// continues, and the model reads why.
    /// </summary>
    private async Task<JsonElement> NotPlacedAsync(
        NhAiToolDescriptor descriptor,
        AssistantToolInvocation row,
        string reason,
        NhAiSubjectRef? named,
        CancellationToken cancellationToken)
    {
        var result = NotPlaced(reason, named);
        row.Status = NhAssistantToolCallStatuses.NotExecuted;
        row.ResultCode = NhAssistantErrorCodes.ProposalNotPlaced;
        row.ResultJson = NhAssistantContent.SerializeResult(result);
        row.CompletedAt = DateTimeOffset.UtcNow;
        await store.UpdateToolInvocationAsync(row, cancellationToken);
        await state.EmitAsync(new NhAssistantToolCompletedEvent(
            row.Id,
            row.Status,
            row.ResultCode,
            NhAssistantContent.ResultPreview(row.ResultJson, row.DataClassification)));
        await RecordBusinessEventAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AgentProposalNotPlaced,
                state.Conversation.Id,
                state.Scope.TurnId,
                state.Scope.OwnerActorId,
                state.Scope.Agent.Id,
                state.Scope.Agent.Version,
                DateTimeOffset.UtcNow)
            {
                ToolId = descriptor.Id,
                ToolVersion = descriptor.Version,
                InvocationId = row.Id,
                ResultCode = reason,
                ObjectId = named?.ToString(),
                StartedAt = row.StartedAt
            },
            cancellationToken);
        return result;
    }

    /// <summary>
    /// Executes a governed function inside an assistant tool-call scope and classifies its outcome
    /// from the content-free audit record the shared invoker wrote.
    /// </summary>
    public static async Task<NhAssistantToolExecution> ExecuteAsync(
        NhAiToolDescriptor descriptor,
        AssistantToolInvocation row,
        Guid? proposalId,
        Guid? approvalId,
        Func<CancellationToken, ValueTask<object?>> invoke,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var call = new NhAssistantToolCallScope
        {
            InvocationId = row.Id,
            Descriptor = descriptor,
            IdempotencyKey = "assistant-" + (proposalId ?? row.Id).ToString("N"),
            ProposalId = proposalId,
            ApprovalId = approvalId
        };
        object? result;
        using (NhAssistantExecutionScope.EnterCall(call))
        {
            try
            {
                result = await invoke(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // Transport or remote failures end this tool call only; the model receives a stable code.
                // The log is content-free: tool identity, turn and exception type, never arguments,
                // results, exception messages or stack traces.
                if (logger is not null)
                {
                    LogUnexpectedToolException(
                        logger,
                        descriptor.Id,
                        descriptor.Version,
                        NhAssistantExecutionScope.CurrentTurn?.TurnId,
                        exception.GetType().FullName ?? exception.GetType().Name);
                }
                var mcp = descriptor.Id.StartsWith("mcp.", StringComparison.Ordinal);
                var code = mcp
                    ? NhAssistantMcpFailures.Classify(exception)
                    : NhAiToolFailureCodes.Failed;
                return new NhAssistantToolExecution(
                    Refusal(code),
                    new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Failed, code),
                    call)
                {
                    // An MCP request that never left the host, or whose credential the server refused,
                    // cannot have changed anything.
                    Threw = !(mcp && NhAssistantMcpFailures.ToolNotRun(exception))
                };
            }
        }

        var records = call.AuditRecords;
        var outcome = records.Count > 0
            ? NhAssistantToolOutcome.FromAudit(records[^1])
            : ClassifyResult(result);
        return new NhAssistantToolExecution(result, outcome, call);
    }

    public async Task<AssistantToolInvocation> BeginToolCallAsync(
        NhAiToolDescriptor descriptor,
        string callId,
        string functionName,
        IEnumerable<KeyValuePair<string, object?>>? arguments,
        string displayName,
        CancellationToken cancellationToken)
    {
        var row = new AssistantToolInvocation
        {
            Id = Guid.NewGuid(),
            ConversationId = state.Conversation.Id,
            TurnId = state.Scope.TurnId,
            MessageId = state.AssistantMessageId,
            CallId = NhAssistantContent.Bound(string.IsNullOrWhiteSpace(callId) ? Guid.NewGuid().ToString("N") : callId, 128),
            FunctionName = NhAssistantContent.Bound(functionName, 256),
            ToolId = descriptor.Id,
            ToolVersion = descriptor.Version,
            ContractHash = NhAssistantContent.Bound(descriptor.ContractHash, 128),
            DisplayName = displayName,
            Status = NhAssistantToolCallStatuses.Running,
            ArgumentsJson = NhAssistantContent.SerializeArguments(arguments),
            DataClassification = descriptor.DataClassification,
            RetentionCategory = descriptor.RetentionCategory,
            StartedAt = DateTimeOffset.UtcNow
        };
        if (state.Scope.Consultation is not null)
        {
            // A consultation writes nothing into the thread; a call it proposes is stored when the
            // proposal is.
            state.ToolCalls++;
            return row;
        }

        await store.AddToolInvocationAsync(row, cancellationToken);
        state.ToolCalls++;
        state.AddPart(new NhAssistantStoredPart(NhAssistantStoredPart.ToolCallType, InvocationId: row.Id));
        await FlushAsync(cancellationToken);
        await state.EmitAsync(new NhAssistantToolStartedEvent(
            row.Id,
            row.ToolId,
            row.ToolVersion,
            row.DisplayName,
            NhAssistantContent.Preview(row.ArgumentsJson, row.DataClassification)));
        return row;
    }

    public async Task CompleteToolCallAsync(
        AssistantToolInvocation row,
        NhAssistantToolExecution execution,
        CancellationToken cancellationToken)
    {
        var succeeded = execution.Outcome.Kind == NhAssistantToolOutcomeKind.Succeeded;
        row.Status = succeeded ? NhAssistantToolCallStatuses.Succeeded : NhAssistantToolCallStatuses.Failed;
        row.ResultCode = succeeded ? null : NhAssistantContent.Bound(execution.Outcome.Code, 128);
        row.ResultJson = NhAssistantContent.SerializeResult(execution.Result);
        row.CompletedAt = DateTimeOffset.UtcNow;
        if (execution.Call.AuditRecords.Count > 0 && execution.Call.AuditRecords[^1].GrantSource != NhAiGrantSource.None)
        {
            row.GrantSource = execution.Call.AuditRecords[^1].GrantSource;
        }
        if (row.GrantSource == NhAiGrantSource.DelegatedRead)
        {
            // Only the person whose rights the read used sees its result; without a known person, nobody.
            row.ReadForActorId = NhAiAgentInvocationScopes.Current?.GatewayReadActorId;
        }
        if (succeeded
            && (row.GrantSource == NhAiGrantSource.DelegatedRead
                || (row.GrantSource is NhAiGrantSource.Agent or NhAiGrantSource.Accountable
                    && execution.Call.Descriptor.Effect == NhAiToolEffect.ReadOnly
                    && state.AgentTurn?.Options.ElevatedReads == NhAiElevatedReads.Restricted)))
        {
            state.UsedRestrictedResults = true;
        }
        if (state.Scope.Consultation is not null)
        {
            return;
        }

        await store.UpdateToolInvocationAsync(row, cancellationToken);
        // A result of the agent's own grant, of the accountable account's rights or of a delegated
        // read is shielded per viewer; live events carry none of it, and viewers read the thread
        // again to see what they may.
        var shielded = row.GrantSource is NhAiGrantSource.Agent or NhAiGrantSource.Accountable or NhAiGrantSource.DelegatedRead;
        await state.EmitAsync(new NhAssistantToolCompletedEvent(
            row.Id,
            row.Status,
            row.ResultCode,
            shielded ? null : NhAssistantContent.ResultPreview(row.ResultJson, row.DataClassification)));
    }

    /// <summary>
    /// Records a proposal for the agent's rules through the same governed call as a model's tool
    /// call. The agent scope requires approval for every effect in a rule turn, so the tool never
    /// executes here.
    /// </summary>
    public async Task<NhAssistantRuleProposal> ProposeAsync(
        AIFunction function,
        AIFunctionArguments arguments,
        Func<CultureInfo, string?>? rationale,
        CancellationToken cancellationToken)
    {
        _rationale = rationale;
        _lastProposal = null;
        try
        {
            var callContent = new FunctionCallContent("rule-" + Guid.NewGuid().ToString("N"), function.Name, arguments);
            var context = new FunctionInvocationContext
            {
                Function = function,
                Arguments = arguments,
                CallContent = callContent
            };
            var result = await InvokeAsync(
                null!,
                context,
                (invocation, token) => invocation.Function.InvokeAsync(invocation.Arguments, token),
                cancellationToken);
            if (_lastProposal is not { } recorded)
            {
                // The call failed before it became a proposal, for example on its arguments or rights,
                // or the object it concerns refused it.
                return NhAssistantRuleProposal.Refused(CodeOf(result) ?? NhAssistantErrorCodes.ProposalRefused) with
                {
                    PlacementReason = ValueOf(result, "reason")
                };
            }

            if (recorded.Outcome == NhAssistantProposeOutcome.NotNeeded)
            {
                return new NhAssistantRuleProposal(NhAssistantProposeOutcome.NotNeeded, null, [], null, recorded.NotNeededReason)
                {
                    NotNeededKind = recorded.NotNeededStaleKind,
                    Explanation = recorded.NotNeededExplanation,
                    Explanations = recorded.NotNeededExplanations
                };
            }

            return new NhAssistantRuleProposal(
                recorded.Outcome,
                recorded.ApprovalId,
                recorded.Superseded,
                recorded.Outcome == NhAssistantProposeOutcome.RecentlyRejected ? recorded.Existing?.DecidedAt : null,
                null)
            {
                PlacedAt = recorded.PlacedAt
            };
        }
        finally
        {
            _rationale = null;
            _lastProposal = null;
        }
    }

    private static string? CodeOf(object? result)
    {
        return ValueOf(result, "code");
    }

    private static string? ValueOf(object? result, string name)
    {
        return result is JsonElement { ValueKind: JsonValueKind.Object } json
               && json.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (state.Scope.Consultation is not null)
        {
            // A consultation has no message in the thread; its answer goes back to the asker.
            return;
        }

        await store.UpdateMessageAsync(
            state.AssistantMessageId,
            NhAssistantContent.SerializeParts(state.Parts),
            state.InputTokens,
            state.OutputTokens,
            state.ToolCalls,
            cancellationToken);
    }

    public async Task RecordBusinessEventAsync(
        NhAssistantAuditEvent evt,
        CancellationToken cancellationToken,
        NhAiSubjectRef? subject = null)
    {
        if (state.AgentTurn is { } agentTurn)
        {
            // A proposal placed at another object is recorded about that object.
            evt = evt with
            {
                RunId = agentTurn.RunId,
                TriggerId = agentTurn.TriggerId,
                SubjectType = subject?.Type ?? agentTurn.Thread.SubjectType,
                SubjectId = subject?.Id ?? agentTurn.Thread.SubjectId,
                InitiatorActorId = agentTurn.InitiatorActorId
            };
        }
        if (state.Scope.Consultation is { } consultation)
        {
            evt = evt with
            {
                ConsultationId = consultation.ConsultationId,
                ConsultationDepth = consultation.Depth,
                AskingAgentId = consultation.AskingAgentId
            };
        }

        foreach (var sink in businessSinks)
        {
            await sink.RecordAsync(state.Scope.WithPromptIdentity(evt), cancellationToken);
        }
    }

    private async Task<NhAssistantRecordedProposal?> RequestApprovalAsync(
        NhAiToolDescriptor descriptor,
        AssistantToolInvocation row,
        NhAssistantToolCallScope call,
        CultureInfo culture,
        CancellationToken cancellationToken,
        NhAssistantPlacedThread? placed = null)
    {
        if (call.CapturedArguments is null
            || call.CapturedContext is not { RunId: not null, AccountableOwnerId: not null } context)
        {
            // Another evidence provider answered; the assistant cannot bind a proposal to this call.
            return null;
        }

        var agentTurn = state.AgentTurn;
        var consultation = state.Scope.Consultation;
        NhAssistantConsultationRoute? route = null;
        if (consultation is not null && agentTurn is not null && consultationProposals is not null)
        {
            // A consultation's proposal takes the first route that applies (CP3): the asker decides
            // it now, it waits at the subject, or it is refused and the model describes the change.
            route = await consultationProposals.DecideAsync(consultation, agentTurn, state.EffectiveAgent, descriptor, cancellationToken);
            if (route.Refused)
            {
                return await RefuseConsultationProposalAsync(descriptor, row, route.RefusalReason!, cancellationToken);
            }
        }

        var policy = route is not null
            ? route.Policy
            : agentTurn is not null && approvalPolicies is not null
            ? consultation is null
                ? approvalPolicies.Resolve(agentTurn.Thread.AgentId, descriptor.Id, agentTurn.TriggerId, agentTurn.RunId is not null, state.Skill?.Id)
                // A consultation answers now and cannot wait: its proposals always wait for the
                // subject's approvers, under the agent's policy or the trigger default.
                : approvalPolicies.Resolve(agentTurn.Thread.AgentId, descriptor.Id, null, isRun: true) with
                {
                    Mode = NhAiApprovalMode.Deferred
                }
            : null;
        // A request turn cannot wait days for a decision: a person typing always decides inline. A
        // rule turn has no model to continue with a decision, so its proposals always wait deferred,
        // as a proposal placed at another object does: the run never waits in another thread. Neither
        // does a run for a tool the application executes, whose result never reaches the turn.
        var mode = policy is null || (agentTurn!.IsRequestTurn && consultation is null)
            ? NhAiApprovalMode.Inline
            : state.RuleTurn || placed is not null || (route is null && ExecutedByApplication(descriptor.Id))
                ? NhAiApprovalMode.Deferred
                : policy.Mode;
        // A run in a shadow thread proposes only shadow proposals, which never execute; a proposal it
        // places waits in the agent's shadow thread at the object. Its agent scope requires approval
        // for every effect, so each effect it calls arrives here as a proposal.
        var shadow = agentTurn is not null && NhAssistantTurnRunner.IsShadowRunTurn(agentTurn);
        var bindPrompt = policy is null || policy.BindTo == NhAiApprovalBinding.ToolContractAndPrompt;
        var lifetime = policy?.Lifetime ?? state.Scope.Limits.ApprovalLifetime;
        // The thread the proposal waits in: the run's own, or the agent's thread at the object.
        var home = placed?.Thread ?? agentTurn?.Thread;

        // The proposal lives by the application's clock: its validity starts and ends there, where
        // decisions, reminders and the invoker's validation read it. When it was recorded stays on
        // the system clock, which audit events and proposal guards compare with the subject's own
        // changes.
        var now = DateTimeOffset.UtcNow;
        var proposedAt = _applicationClock.GetUtcNow();
        var expiresAt = proposedAt.Add(lifetime);
        var targets = context.ExecutionScopes
            .Take(32)
            .Select(scope => new NhAiProposalTarget(scope.Type, scope.Id))
            .ToArray();
        var intent = NhAssistantContent.Bound(
            string.IsNullOrWhiteSpace(descriptor.Description) ? descriptor.Id : descriptor.Description,
            512);
        var proposal = proposalFactory.Create(new NhAiProposalCreateRequest(
            Guid.NewGuid(),
            context.RunId,
            context.ActorKind,
            context.ActorId,
            // A proposal that executes with the agent's own rights answers to whom the agent answers.
            route?.AccountableOwnerId ?? context.AccountableOwnerId,
            descriptor,
            call.CapturedArguments,
            targets,
            intent,
            [EffectCode(descriptor.Effect)],
            new Dictionary<string, string>(StringComparer.Ordinal),
            new NhAiActionBudget(1),
            proposedAt,
            expiresAt)
        {
            // A proposal bound to the tool contract only stays valid across prompt updates.
            ModelProfileName = bindPrompt ? context.ModelProfileName : null,
            PromptVersion = bindPrompt ? context.PromptVersion : null,
            PromptHash = bindPrompt ? context.PromptHash : null,
            CatalogHash = bindPrompt ? context.CatalogHash : null,
            ContextHash = bindPrompt ? context.ContextHash : null
        });
        var targetLabels = proposal.Targets
            .Select(target => $"{target.Type}:{target.Id}")
            .ToArray();
        var approvalId = Guid.NewGuid();
        var argumentsHash = NhAssistantProposalDuplicates.IdentityOf(descriptor.Id, proposal.Arguments);
        // Guards that check at proposal time keep work that is already done or no longer applies
        // from being proposed at all.
        if (await CheckAtProposalAsync(home, descriptor.Id, proposal, now, cancellationToken) is { } stale)
        {
            if (route?.Route == NhAssistantConsultationRoutes.AskerDecides)
            {
                NhAssistantConsultationProposals.ReleaseAskerDecision(consultation!);
            }
            return await SkipNotNeededAsync(row, proposal.ProposalId, stale, mode, cancellationToken);
        }

        // Proposals of a person's own turn wait in the conversation; only agent proposals can be
        // revalidated or deduplicated.
        var agentProposal = agentTurn is not null && !shadow && !(mode == NhAiApprovalMode.Inline && agentTurn.RunId is null);
        // A shadow proposal names its objects as a live one does, so the policy's rule for a newer
        // proposal for the same objects applies among shadow proposals too (FB3). Its status keeps it
        // out of revalidation, inboxes and counts.
        var keys = agentProposal || shadow
            ? await GuardKeysAsync(home!, descriptor.Id, proposal, now, cancellationToken)
            : [];
        var duplicate = agentProposal && duplicates is not null && policy is not null
            ? await duplicates.CheckAsync(home!, descriptor.Id, argumentsHash, keys, policy, cancellationToken)
            // A shadow proposal never waits, so it counts as open within its lifetime: an unchanged
            // finding is not proposed again, as live would not (LS9), and a newer one for the same
            // objects follows the policy's rule among shadow proposals (FB3).
            : shadow && duplicates is not null
                ? await duplicates.CheckShadowAsync(home!, descriptor.Id, argumentsHash, keys, policy, cancellationToken)
                : NhAssistantDuplicateCheck.None;
        if (duplicate is { Outcome: not NhAssistantProposeOutcome.Created, Existing: { } existing })
        {
            return await SkipDuplicateAsync(descriptor, row, duplicate.Outcome, existing, mode, shadow, cancellationToken);
        }

        var presentations = await toolPresentation.ResolveApprovalInCulturesAsync(
            descriptor,
            call.CapturedArguments,
            context,
            culture,
            cancellationToken);
        var presentation = presentations.Presentation;
        if (presentation is not null)
        {
            row.DisplayName = presentation.ToolDisplayName;
        }

        if (placed is not null)
        {
            // Every check passed: the proposal waits in the agent's thread at the object, which is
            // created now when it is new.
            var stored = await placements!.ThreadAtAsync(state.EffectiveAgent, placed.Subject, placed.Thread.Shadow, cancellationToken);
            if (!stored.Success)
            {
                return new NhAssistantRecordedProposal(Guid.Empty, Guid.Empty, mode, shadow)
                {
                    Outcome = NhAssistantProposeOutcome.Refused,
                    NotPlacedReason = NhAssistantProposalPlacement.ReasonOf(stored)
                };
            }

            placed = placed with { Thread = stored.Data! };
            home = placed.Thread;
        }

        // A placed proposal executes from its own copy of the call in the thread at the object, so
        // the run's thread keeps its call as it ended here.
        var waitingCall = placed is null ? row : CopyForPlacement(row, placed.Thread.ConversationId);
        // The subject the proposal waits at belongs to its parent as the resolver described it now;
        // the inbox groups by this snapshot.
        var parent = placed is null ? agentTurn?.Subject.Parent : placed.Subject.Parent;
        var approval = new AssistantApproval
        {
            Id = approvalId,
            ConversationId = placed?.Thread.ConversationId ?? state.Conversation.Id,
            TurnId = state.Scope.TurnId,
            ToolInvocationId = waitingCall.Id,
            ProposalId = proposal.ProposalId,
            ProposalHash = proposal.ProposalHash,
            ProposalJson = NhAssistantProposalSerializer.Serialize(proposal),
            ToolId = descriptor.Id,
            Summary = "Approval is required before this tool can run.",
            PresentationJson = NhAssistantToolPresentationResolver.Serialize(presentation),
            PresentationsJson = presentations.LocalizedJson,
            ArgumentsPreview = NhAssistantContent.Preview(row.ArgumentsJson, row.DataClassification) ?? "{}",
            TargetsJson = JsonSerializer.Serialize(targetLabels, NhAssistantContent.JsonOptions),
            Status = shadow ? NhAssistantApprovalStatuses.Shadow : NhAssistantApprovalStatuses.Pending,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            RunId = agentTurn?.RunId,
            PlacedFromConversationId = placed is null ? null : state.Conversation.Id,
            PolicyId = policy?.Id,
            Mode = NhAssistantApprovalModes.From(mode),
            PolicyJson = policy is null ? null : NhAssistantApprovalPolicySnapshot.Serialize(policy),
            InitiatorActorId = agentTurn is null ? state.Scope.OwnerActorId : agentTurn.InitiatorActorId,
            // The precondition compares the version of the object the proposal waits at.
            SubjectVersion = placed?.Subject.Version ?? agentTurn?.Subject.Version,
            ParentSubjectType = parent?.Subject.Type,
            ParentSubjectId = parent?.Subject.Id,
            ParentDisplayName = parent?.DisplayName is { } parentName
                ? NhAssistantContent.Bound(parentName, NhAiSubjectParent.MaxDisplayNameLength)
                : null,
            GrantSource = route?.GrantSource ?? context.GrantSource,
            ConsultationId = consultation?.ConsultationId,
            NextReminderAt = policy is not null && mode == NhAiApprovalMode.Deferred && !shadow
                ? NhAssistantApprovalReminders.Next(policy, proposedAt, 0, expiresAt)
                : null,
            ArgumentsHash = argumentsHash,
            RationalesJson = _rationale is null ? null : texts?.Render(_rationale),
            ConcurrencyStamp = Guid.NewGuid()
        };
        if (consultation is not null || placed is not null)
        {
            // The execution of an approved proposal replays the stored invocation.
            waitingCall.Status = NhAssistantToolCallStatuses.AwaitingApproval;
            waitingCall.ProposalId = proposal.ProposalId;
            waitingCall.ApprovalId = approval.Id;
            waitingCall.GrantSource = approval.GrantSource;
            await store.AddToolInvocationAsync(waitingCall, cancellationToken);
        }

        await store.AddApprovalAsync(
            approval,
            keys.Select(key => new AssistantApprovalKey
                {
                    ApprovalId = approval.Id,
                    ScopeKey = home!.ScopeKey,
                    KeyType = key.Type,
                    KeyId = key.Id
                })
                .ToArray(),
            cancellationToken);
        var superseded = new List<Guid>();
        foreach (var older in duplicate.Superseded)
        {
            // The newer proposal for the same objects replaces the pending one, or in shadow the
            // shadow one within its lifetime.
            if (invalidation is null)
            {
                continue;
            }

            var replaced = shadow
                ? await invalidation.SupersedeShadowAsync(older, home!, approval.Id, state.Scope.Agent.ActorId, CancellationToken.None)
                : await invalidation.SupersedeAsync(older, home!, approval.Id, state.Scope.Agent.ActorId, CancellationToken.None);
            if (replaced is not null)
            {
                superseded.Add(older.Id);
            }
        }

        state.ProposalCount++;
        if (route is not null)
        {
            consultation!.Outcome.Record(approval.Id, route.Route!);
        }
        if (placed is not null)
        {
            return await CompletePlacedAsync(descriptor, row, approval, placed, superseded, mode, shadow, cancellationToken);
        }

        row.Status = NhAssistantToolCallStatuses.AwaitingApproval;
        row.ProposalId = proposal.ProposalId;
        row.ApprovalId = approval.Id;
        row.GrantSource = approval.GrantSource;
        await store.UpdateToolInvocationAsync(row, cancellationToken);

        var view = new NhAssistantApprovalView(
            approval.Id,
            approval.ProposalId,
            approval.ProposalHash,
            approval.ToolId,
            approval.Summary,
            presentation,
            approval.ArgumentsPreview,
            targetLabels,
            approval.ExpiresAt,
            approval.Status);
        if (consultation is null)
        {
            // A consult turn neither waits nor shows a card: the asking turn does, when the asker
            // decides.
            if (mode == NhAiApprovalMode.Inline && !shadow)
            {
                state.PendingApproval = view;
            }
            state.AddPart(new NhAssistantStoredPart(NhAssistantStoredPart.ApprovalType, ApprovalId: approval.Id));
            await FlushAsync(cancellationToken);
            if (!shadow)
            {
                await state.EmitAsync(new NhAssistantApprovalRequiredEvent(view));
            }
        }
        await RecordBusinessEventAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.ApprovalRequested,
                state.Conversation.Id,
                state.Scope.TurnId,
                state.Scope.OwnerActorId,
                state.Scope.Agent.Id,
                state.Scope.Agent.Version,
                now)
            {
                ToolId = descriptor.Id,
                ToolVersion = descriptor.Version,
                InvocationId = row.Id,
                ResultCode = NhAiToolFailureCodes.ApprovalRequired,
                ApprovalId = approval.Id,
                ProposalId = proposal.ProposalId,
                StartedAt = row.StartedAt,
                GrantSource = route is null ? NhAiGrantSource.None : approval.GrantSource,
                ApprovalPolicyId = route is null ? null : approval.PolicyId,
                ConsultationRoute = route?.Route
            },
            cancellationToken);
        return new NhAssistantRecordedProposal(approval.Id, proposal.ProposalId, mode, shadow)
        {
            Superseded = superseded,
            ConsultationRoute = route?.Route
        };
    }

    /// <summary>
    /// Ends the run's call of a proposal it placed at another object: the call did not run here and
    /// names where its proposal waits; the people of that thread learn about it, and the audit records
    /// the proposal about that object.
    /// </summary>
    private async Task<NhAssistantRecordedProposal> CompletePlacedAsync(
        NhAiToolDescriptor descriptor,
        AssistantToolInvocation row,
        AssistantApproval approval,
        NhAssistantPlacedThread placed,
        IReadOnlyList<Guid> superseded,
        NhAiApprovalMode mode,
        bool shadow,
        CancellationToken cancellationToken)
    {
        var subject = placed.Subject.Subject;
        row.Status = NhAssistantToolCallStatuses.NotExecuted;
        row.ResultCode = NhAssistantErrorCodes.ProposalPlaced;
        row.ResultJson = NhAssistantContent.SerializeResult(ProposalPlaced(approval.ProposalId, subject));
        row.ProposalId = approval.ProposalId;
        row.ApprovalId = approval.Id;
        row.GrantSource = approval.GrantSource;
        row.CompletedAt = DateTimeOffset.UtcNow;
        await store.UpdateToolInvocationAsync(row, cancellationToken);
        await state.EmitAsync(new NhAssistantToolCompletedEvent(
            row.Id,
            row.Status,
            row.ResultCode,
            NhAssistantContent.ResultPreview(row.ResultJson, row.DataClassification)));
        if (workUpdates is not null)
        {
            await workUpdates.PublishAsync(
                placed.Thread.ConversationId,
                Live.NhAssistantAgentWorkReasons.Proposal,
                approval.Id,
                approval.RunId,
                CancellationToken.None);
        }

        foreach (var kind in new[] { NhAssistantAuditEventKind.ApprovalRequested, NhAssistantAuditEventKind.AgentProposalPlaced })
        {
            await RecordBusinessEventAsync(
                new NhAssistantAuditEvent(
                    kind,
                    placed.Thread.ConversationId,
                    state.Scope.TurnId,
                    state.Scope.OwnerActorId,
                    state.Scope.Agent.Id,
                    state.Scope.Agent.Version,
                    approval.CreatedAt)
                {
                    ToolId = descriptor.Id,
                    ToolVersion = descriptor.Version,
                    InvocationId = approval.ToolInvocationId,
                    ResultCode = kind == NhAssistantAuditEventKind.ApprovalRequested
                        ? NhAiToolFailureCodes.ApprovalRequired
                        : NhAssistantErrorCodes.ProposalPlaced,
                    ApprovalId = approval.Id,
                    ProposalId = approval.ProposalId,
                    ObjectId = kind == NhAssistantAuditEventKind.AgentProposalPlaced
                        ? state.Conversation.Id.ToString("D")
                        : null,
                    StartedAt = row.StartedAt
                },
                cancellationToken,
                subject);
        }

        return new NhAssistantRecordedProposal(approval.Id, approval.ProposalId, mode, shadow)
        {
            Superseded = superseded,
            PlacedAt = subject
        };
    }

    /// <summary>
    /// The call as it waits in the thread at the object: the same tool, arguments and identity, in
    /// that thread's conversation and outside its message history.
    /// </summary>
    private static AssistantToolInvocation CopyForPlacement(AssistantToolInvocation row, Guid conversationId)
    {
        return new AssistantToolInvocation
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TurnId = row.TurnId,
            MessageId = Guid.Empty,
            CallId = row.CallId,
            FunctionName = row.FunctionName,
            ToolId = row.ToolId,
            ToolVersion = row.ToolVersion,
            ContractHash = row.ContractHash,
            DisplayName = row.DisplayName,
            Status = NhAssistantToolCallStatuses.AwaitingApproval,
            ArgumentsJson = row.ArgumentsJson,
            DataClassification = row.DataClassification,
            RetentionCategory = row.RetentionCategory,
            StartedAt = row.StartedAt
        };
    }

    /// <summary>
    /// Ends the tool call of a proposal that an open or recently rejected one made unnecessary:
    /// nothing is recorded, the call reads <c>not-executed</c> and the audit names the existing
    /// proposal.
    /// </summary>
    private async Task<NhAssistantRecordedProposal> SkipDuplicateAsync(
        NhAiToolDescriptor descriptor,
        AssistantToolInvocation row,
        NhAssistantProposeOutcome outcome,
        AssistantApproval existing,
        NhAiApprovalMode mode,
        bool shadow,
        CancellationToken cancellationToken)
    {
        var code = outcome == NhAssistantProposeOutcome.RecentlyRejected
            ? NhAssistantErrorCodes.ProposalRecentlyRejected
            : NhAssistantErrorCodes.ProposalAlreadyOpen;
        row.Status = NhAssistantToolCallStatuses.NotExecuted;
        row.ResultCode = code;
        row.CompletedAt = DateTimeOffset.UtcNow;
        if (state.Scope.Consultation is null)
        {
            await store.UpdateToolInvocationAsync(row, cancellationToken);
            await state.EmitAsync(new NhAssistantToolCompletedEvent(row.Id, row.Status, row.ResultCode, null));
        }

        await RecordBusinessEventAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AgentProposalDeduplicated,
                state.Conversation.Id,
                state.Scope.TurnId,
                state.Scope.OwnerActorId,
                state.Scope.Agent.Id,
                state.Scope.Agent.Version,
                DateTimeOffset.UtcNow)
            {
                ToolId = descriptor.Id,
                ToolVersion = descriptor.Version,
                InvocationId = row.Id,
                ResultCode = code,
                ApprovalId = existing.Id,
                ProposalId = existing.ProposalId,
                StartedAt = row.StartedAt
            },
            cancellationToken);
        return new NhAssistantRecordedProposal(existing.Id, existing.ProposalId, mode, shadow)
        {
            Outcome = outcome,
            Existing = existing
        };
    }

    /// <summary>
    /// Runs the guards of the tool that check at proposal time (<see cref="NhAiProposalGuardStages.Proposal"/>)
    /// and returns their stale result, or <see langword="null"/> when the proposal is needed. A guard
    /// that fails keeps the proposal, as at the other stages: the decision and the execution check it
    /// again.
    /// </summary>
    private async Task<NhAiPreconditionResult?> CheckAtProposalAsync(
        AssistantThread? thread,
        string toolId,
        NhAiProposal proposal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (proposalGuards is null || !proposalGuards.HasGuards(toolId, NhAiProposalGuardStages.Proposal))
        {
            return null;
        }

        var check = await proposalGuards.CheckAsync(
            new NhAiProposalGuardContext(
                proposal.ProposalId,
                toolId,
                proposal.Arguments,
                thread is null ? null : new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
                thread?.ScopeKey,
                now),
            NhAiProposalGuardStages.Proposal,
            cancellationToken);
        return check.Stale;
    }

    /// <summary>
    /// Ends the tool call of a proposal that a guard found not needed: nothing is recorded, the call
    /// reads <c>not-executed</c> with <see cref="NhAssistantErrorCodes.ProposalNotNeeded"/>, and its
    /// result carries the guard's reason and explanation in the culture the turn works in.
    /// </summary>
    private async Task<NhAssistantRecordedProposal> SkipNotNeededAsync(
        AssistantToolInvocation row,
        Guid proposalId,
        NhAiPreconditionResult stale,
        NhAiApprovalMode mode,
        CancellationToken cancellationToken)
    {
        var reason = NhAssistantProposalGuards.ReasonCodeOf(stale);
        var kind = NhAssistantStaleKinds.From(stale.Kind);
        // The same texts per supported UI culture as a stale proposal stores, for rules that write
        // their result in each reader's language; the run's own culture for the model and the thread.
        IReadOnlyDictionary<string, string> explanations = texts is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : texts.RenderTexts(stale.Explain);
        var explanation = NhAssistantStaleExplanations.PickFrom(explanations, CultureInfo.CurrentUICulture);
        var result = NotNeededResult(reason, kind, explanation);
        row.Status = NhAssistantToolCallStatuses.NotExecuted;
        row.ResultCode = NhAssistantErrorCodes.ProposalNotNeeded;
        row.ResultJson = NhAssistantContent.SerializeResult(result);
        row.CompletedAt = DateTimeOffset.UtcNow;
        if (state.Scope.Consultation is null)
        {
            await store.UpdateToolInvocationAsync(row, cancellationToken);
            await state.EmitAsync(new NhAssistantToolCompletedEvent(
                row.Id,
                row.Status,
                row.ResultCode,
                NhAssistantContent.ResultPreview(row.ResultJson, row.DataClassification)));
        }

        return new NhAssistantRecordedProposal(Guid.Empty, proposalId, mode, false)
        {
            Outcome = NhAssistantProposeOutcome.NotNeeded,
            NotNeededReason = reason,
            NotNeededKind = kind,
            NotNeededStaleKind = stale.Kind,
            NotNeededExplanation = explanation,
            NotNeededExplanations = explanations
        };
    }

    /// <summary>
    /// The application objects an agent proposal changes, as its guards name them, so the application
    /// can revalidate it by object and recognize a newer proposal for the same objects.
    /// </summary>
    private async Task<IReadOnlyList<NhAiProposalKey>> GuardKeysAsync(
        AssistantThread thread,
        string toolId,
        NhAiProposal proposal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (proposalGuards is null || state.AgentTurn is null)
        {
            return [];
        }

        return await proposalGuards.GetKeysAsync(
            new NhAiProposalGuardContext(
                proposal.ProposalId,
                toolId,
                proposal.Arguments,
                new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
                thread.ScopeKey,
                now),
            cancellationToken);
    }

    /// <summary>
    /// The result the model receives when a guard found its proposal not needed.
    /// </summary>
    private static JsonElement NotNeeded(NhAssistantRecordedProposal notNeeded)
    {
        return NotNeededResult(notNeeded.NotNeededReason!, notNeeded.NotNeededKind!, notNeeded.NotNeededExplanation);
    }

    private static JsonElement NotNeededResult(string reason, string kind, string? explanation)
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = NhAssistantErrorCodes.ProposalNotNeeded,
                ["reason"] = reason,
                ["kind"] = kind,
                ["explanation"] = explanation,
                ["message"] = "The application found that this proposal is not needed: the work is already done or no longer applies. "
                    + "Nothing was proposed. Do not propose it again; tell people why, using the explanation."
            },
            NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// The result the model receives when its proposal was not recorded because the same one is open
    /// or was rejected recently.
    /// </summary>
    private static JsonElement Duplicate(NhAssistantRecordedProposal skipped)
    {
        var rejected = skipped.Outcome == NhAssistantProposeOutcome.RecentlyRejected;
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = rejected ? NhAssistantErrorCodes.ProposalRecentlyRejected : NhAssistantErrorCodes.ProposalAlreadyOpen,
                ["proposalId"] = skipped.ProposalId,
                ["status"] = skipped.Existing?.Status,
                ["rejectedAt"] = rejected ? skipped.Existing?.DecidedAt : null,
                ["message"] = rejected
                    ? "People rejected the same proposal recently. Nothing was recorded. Do not propose it again now; mention it in your answer."
                    : "The same proposal already waits for a decision or its execution. Nothing new was recorded. Do not propose it again; mention it in your answer."
            },
            NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// The result the model receives for a proposal that waits for people: the action has not
    /// happened yet, and the model must not retry it.
    /// </summary>
    private static JsonElement ProposalRecorded(Guid proposalId)
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = ProposalRecordedCode,
                ["proposalId"] = proposalId,
                ["message"] = "The proposal was recorded and waits for a person's approval. The action has not happened. Do not retry it; continue with other work and mention it in your answer."
            },
            NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// The result the consulted model receives for a proposal it recorded, which names who decides it.
    /// </summary>
    private static JsonElement ConsultationProposalRecorded(Guid proposalId, string route)
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = ProposalRecordedCode,
                ["proposalId"] = proposalId,
                ["route"] = route,
                ["message"] = route switch
                {
                    NhAssistantConsultationRoutes.AskerDecides =>
                        "The proposal was recorded and the person who asked decides it right away in their own conversation. The action has not happened. Do not retry it; finish your answer and say what you proposed.",
                    NhAssistantConsultationRoutes.SubjectAgentRights =>
                        "The proposal was recorded and waits for the subject's approvers other than the person who asked; after their approval it executes with your own rights. The action has not happened. Do not retry it; finish your answer and say who decides it.",
                    _ =>
                        "The proposal was recorded and waits for the subject's approvers. The action has not happened. Do not retry it; finish your answer and say who decides it."
                }
            },
            NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// Ends the consulted agent's call when no route of the consultation applies: nothing is recorded,
    /// the audit names the reason, and the model describes the change instead.
    /// </summary>
    private async Task<NhAssistantRecordedProposal> RefuseConsultationProposalAsync(
        NhAiToolDescriptor descriptor,
        AssistantToolInvocation row,
        string reason,
        CancellationToken cancellationToken)
    {
        row.Status = NhAssistantToolCallStatuses.NotExecuted;
        row.ResultCode = NhAssistantErrorCodes.ConsultProposalRefused;
        row.CompletedAt = DateTimeOffset.UtcNow;
        await RecordBusinessEventAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.ToolInvoked,
                state.Conversation.Id,
                state.Scope.TurnId,
                state.Scope.OwnerActorId,
                state.Scope.Agent.Id,
                state.Scope.Agent.Version,
                DateTimeOffset.UtcNow)
            {
                ToolId = descriptor.Id,
                ToolVersion = descriptor.Version,
                InvocationId = row.Id,
                ResultCode = NhAssistantErrorCodes.ConsultProposalRefused,
                ConsultationRefusalReason = reason,
                StartedAt = row.StartedAt,
                CompletedAt = row.CompletedAt
            },
            cancellationToken);
        return new NhAssistantRecordedProposal(Guid.Empty, Guid.Empty, NhAiApprovalMode.Deferred, false)
        {
            Outcome = NhAssistantProposeOutcome.Refused,
            ConsultationRefusal = reason
        };
    }

    /// <summary>
    /// Lets the turn wait for its person's decision of the proposal a consultation left (option A): the
    /// consult call waits with the consultation's answer as its result, the message keeps an approval
    /// part that names the consultation, and the stream asks for the decision as for the turn's own
    /// inline proposal.
    /// </summary>
    private async Task<object?> AwaitAskerDecisionAsync(
        AssistantToolInvocation row,
        NhAssistantToolExecution execution,
        NhAssistantAskerDecision decision,
        CancellationToken cancellationToken)
    {
        var approval = await store.FindApprovalAsync(decision.ApprovalId, cancellationToken)
            ?? throw new InvalidOperationException("The proposal a consultation left for its asker is missing.");
        row.Status = NhAssistantToolCallStatuses.AwaitingApproval;
        row.ResultCode = null;
        row.ResultJson = NhAssistantContent.SerializeResult(execution.Result);
        row.ProposalId = approval.ProposalId;
        row.ApprovalId = approval.Id;
        if (execution.Call.AuditRecords.Count > 0 && execution.Call.AuditRecords[^1].GrantSource != NhAiGrantSource.None)
        {
            row.GrantSource = execution.Call.AuditRecords[^1].GrantSource;
        }
        await store.UpdateToolInvocationAsync(row, cancellationToken);

        var view = NhAssistantConversationReader.ToView(approval, _applicationClock.GetUtcNow()) with
        {
            Consultation = new NhAssistantApprovalConsultationView(
                decision.ConsultationId,
                decision.AgentId,
                decision.Subject.Type,
                decision.Subject.Id,
                decision.SubjectDisplayName,
                decision.LeaveRoute)
            {
                Answer = NhAssistantConversationReader.ConsultationAnswerOf(row)
            }
        };
        state.PendingApproval = view;
        state.AddPart(new NhAssistantStoredPart(NhAssistantStoredPart.ApprovalType, ApprovalId: approval.Id));
        await FlushAsync(cancellationToken);
        await state.EmitAsync(new NhAssistantApprovalRequiredEvent(view));
        return execution.Result;
    }

    /// <summary>
    /// Lets the turn wait for the answer to the question its <c>ask_question</c> call asked (LS21): the
    /// call waits as <c>awaiting-answer</c>, the agent's message keeps the question with its choices,
    /// its addressee and the end of its wait on the application's clock, the people of the
    /// conversation see it, and the audit records it without its text.
    /// </summary>
    private async Task<object?> AwaitAnswerAsync(
        AssistantToolInvocation row,
        NhAssistantAskedQuestion asked,
        CancellationToken cancellationToken)
    {
        var now = _applicationClock.GetUtcNow();
        row.Status = NhAssistantToolCallStatuses.AwaitingAnswer;
        row.ResultCode = null;
        row.ResultJson = null;
        await store.UpdateToolInvocationAsync(row, cancellationToken);

        var part = NhAssistantQuestions.PartOf(asked, row.Id, now.Add(state.Scope.Limits.ApprovalLifetime));
        var view = NhAssistantQuestions.ViewOf(part, row, now)
            ?? throw new InvalidOperationException("A recorded question has no view.");
        state.PendingQuestion = view;
        state.AddPart(part);
        await FlushAsync(cancellationToken);
        await state.EmitAsync(new NhAssistantQuestionAskedEvent(view));
        await RecordBusinessEventAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.QuestionAsked,
                state.Conversation.Id,
                state.Scope.TurnId,
                state.Scope.OwnerActorId,
                state.Scope.Agent.Id,
                state.Scope.Agent.Version,
                DateTimeOffset.UtcNow)
            {
                ToolId = row.ToolId,
                ToolVersion = row.ToolVersion,
                InvocationId = row.Id,
                ObjectId = asked.AddresseeActorId,
                StartedAt = row.StartedAt
            },
            cancellationToken);
        return NhAssistantQuestions.WaitingResult();
    }

    /// <summary>
    /// The result the model receives for a proposal placed at another object: it waits there for that
    /// object's approvers, the action has not happened, and the run continues.
    /// </summary>
    private static JsonElement ProposalPlaced(Guid proposalId, NhAiSubjectRef placedAt)
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = NhAssistantErrorCodes.ProposalPlaced,
                ["proposalId"] = proposalId,
                ["subjectType"] = placedAt.Type,
                ["subjectId"] = placedAt.Id,
                ["message"] = "The proposal was recorded at that object and waits for its approvers. The action has not happened. Do not retry it; continue with other work and mention it in your answer."
            },
            NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// The result the model receives when a proposal for another object was not placed there.
    /// </summary>
    private static JsonElement NotPlaced(string reason, NhAiSubjectRef? named)
    {
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = NhAssistantErrorCodes.ProposalNotPlaced,
                ["reason"] = reason,
                ["subjectType"] = named?.Type,
                ["subjectId"] = named?.Id,
                ["message"] = reason switch
                {
                    NhAssistantPlacementReasons.DoesNotWorkOn => "Nothing was proposed: this agent does not work on that object or not in its scope. Do not retry it.",
                    NhAssistantPlacementReasons.NoAccess => "Nothing was proposed: the account this agent answers to may not read that object. Do not retry it.",
                    NhAssistantPlacementReasons.NoAccountableOwner => "Nothing was proposed: nobody answers for this agent at that object, so its proposal cannot wait there. Do not retry it; mention it in your answer.",
                    NhAssistantPlacementReasons.Limit => "Nothing was proposed: this run placed as many proposals at other objects as it may. Do not propose more; mention the rest in your answer.",
                    _ => "Nothing was proposed: that object was not found. Check the id; do not retry the same call."
                }
            },
            NhAssistantContent.JsonOptions);
    }

    /// <summary>
    /// Finds an offered memory function of this turn behind Agent Framework middleware wrappers.
    /// </summary>
    private NhAssistantMemoryFunction? FindMemory(AIFunction function)
    {
        var memory = function as NhAssistantMemoryFunction
            ?? function.GetService(typeof(NhAssistantMemoryFunction)) as NhAssistantMemoryFunction;
        return memory is not null && state.ExtraFunctions.Contains(memory) ? memory : null;
    }

    /// <summary>
    /// Finds the governed function behind Agent Framework middleware wrappers.
    /// </summary>
    private static INhAiGovernedAIFunction? FindGoverned(AIFunction function)
    {
        AIFunction? current = function;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            if (current is INhAiGovernedAIFunction governed)
            {
                return governed;
            }
            if (current.GetService(typeof(INhAiGovernedAIFunction)) is INhAiGovernedAIFunction service)
            {
                return service;
            }
            current = null;
        }
        return null;
    }

    private static NhAssistantToolOutcome ClassifyResult(object? result)
    {
        if (result is JsonElement { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.True)
        {
            return new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Succeeded, "succeeded");
        }
        return new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Failed, NhAiToolFailureCodes.Failed);
    }

    private static JsonElement Refusal(string code)
    {
        // The call never reached the tool; the model still receives the shared tool result.
        return NhAiToolResult.Failed(code, null);
    }

    private static readonly Action<ILogger, string, int, Guid?, string, Exception?> UnexpectedToolException =
        LoggerMessage.Define<string, int, Guid?, string>(
            LogLevel.Warning,
            new EventId(1, "AssistantToolUnexpectedException"),
            "Assistant tool {ToolId} v{ToolVersion} failed unexpectedly in turn {TurnId} with {ExceptionType}.");

    private static void LogUnexpectedToolException(
        ILogger logger,
        string toolId,
        int toolVersion,
        Guid? turnId,
        string exceptionType)
    {
        UnexpectedToolException(logger, toolId, toolVersion, turnId, exceptionType, null);
    }

    private static string EffectCode(NhAiToolEffect effect)
    {
        return effect switch
        {
            NhAiToolEffect.ReadOnly => "read-only",
            NhAiToolEffect.IdempotentMutation => "idempotent-mutation",
            NhAiToolEffect.Mutation => "mutation",
            NhAiToolEffect.ExternalSideEffect => "external-side-effect",
            NhAiToolEffect.Destructive => "destructive",
            _ => "unknown"
        };
    }
}

internal sealed record NhAssistantRecordedProposal(
    Guid ApprovalId,
    Guid ProposalId,
    NhAiApprovalMode Mode,
    bool Shadow)
{
    public NhAssistantProposeOutcome Outcome { get; init; } = NhAssistantProposeOutcome.Created;

    /// <summary>
    /// The open or rejected proposal that made a new one unnecessary.
    /// </summary>
    public AssistantApproval? Existing { get; init; }

    /// <summary>
    /// The route of a proposal a consultation recorded, one of <see cref="NhAssistantConsultationRoutes"/>.
    /// </summary>
    public string? ConsultationRoute { get; init; }

    /// <summary>
    /// Why a consultation recorded nothing: one of <see cref="NhAssistantConsultationRefusalReasons"/>.
    /// </summary>
    public string? ConsultationRefusal { get; init; }

    /// <summary>
    /// The pending proposals a newly recorded one replaced.
    /// </summary>
    public IReadOnlyList<Guid> Superseded { get; init; } = [];

    /// <summary>
    /// The object the proposal waits at, when the run placed it at another object than its own.
    /// </summary>
    public NhAiSubjectRef? PlacedAt { get; init; }

    /// <summary>
    /// Why a proposal for another object was not placed after all: one of
    /// <see cref="Runs.NhAssistantPlacementReasons"/>.
    /// </summary>
    public string? NotPlacedReason { get; init; }

    /// <summary>
    /// For <see cref="NhAssistantProposeOutcome.NotNeeded"/>: the guard's reason code, the kind of its
    /// stale result (one of <see cref="NhAssistantStaleKinds"/>) and its explanation.
    /// </summary>
    public string? NotNeededReason { get; init; }

    public string? NotNeededKind { get; init; }

    /// <summary>The kind of the guard's stale result, as the guard returned it.</summary>
    public NhAiStaleKind? NotNeededStaleKind { get; init; }

    public string? NotNeededExplanation { get; init; }

    /// <summary>
    /// The guard's explanation per supported UI culture, by culture name, the run's culture first;
    /// empty when the guard gives none.
    /// </summary>
    public IReadOnlyDictionary<string, string> NotNeededExplanations { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The agent's thread at the object a run's proposal is placed at, with that object as its resolver
/// described it.
/// </summary>
internal sealed record NhAssistantPlacedThread(AssistantThread Thread, NhAiSubjectInfo Subject);
