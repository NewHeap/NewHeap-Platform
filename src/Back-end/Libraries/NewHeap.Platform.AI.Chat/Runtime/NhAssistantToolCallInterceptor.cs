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
    /// The application map entries the acting person may read, loaded once per turn of an agent that
    /// uses the map.
    /// </summary>
    public IReadOnlyList<NhAiApplicationMapEntry>? ApplicationMapEntries { get; set; }

    /// <summary>
    /// The retrieved context of the turn and its hash, retrieved once before the first model call.
    /// </summary>
    public (string Block, string Hash)? RetrievedContext { get; set; }

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
    NhAssistantStaleExplanations? texts = null)
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
        if (state.Scope.Consultation is { MayPropose: false } && descriptor.Effect != NhAiToolEffect.ReadOnly)
        {
            // Such tools are not offered; a model that names one anyway changes nothing.
            return Refusal(NhAssistantErrorCodes.ConsultReadOnly);
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
        var execution = await ExecuteAsync(
            descriptor,
            row,
            null,
            null,
            token => next(context, token),
            cancellationToken,
            logger);
        if (execution.Outcome.Kind == NhAssistantToolOutcomeKind.ApprovalMissing)
        {
            var requested = await RequestApprovalAsync(
                descriptor,
                row,
                execution.Call,
                presentationCulture,
                cancellationToken);
            _lastProposal = requested;
            if (requested is { Outcome: not NhAssistantProposeOutcome.Created } skipped)
            {
                // The same proposal is open or was rejected recently; nothing new waits for people.
                return Duplicate(skipped);
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

        await CompleteToolCallAsync(row, execution, cancellationToken);
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
        return execution.Result;
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
                var code = descriptor.Id.StartsWith("mcp.", StringComparison.Ordinal)
                    ? NhAssistantMcpFailures.Classify(exception)
                    : NhAiToolFailureCodes.Failed;
                return new NhAssistantToolExecution(
                    Refusal(code),
                    new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Failed, code),
                    call)
                {
                    // An MCP request stopped before it left the host cannot have changed anything.
                    Threw = !NhAssistantMcpFailures.StoppedBeforeSending(code)
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
                // The call failed before it became a proposal, for example on its arguments or rights.
                return NhAssistantRuleProposal.Refused(CodeOf(result) ?? NhAssistantErrorCodes.ProposalRefused);
            }

            return new NhAssistantRuleProposal(
                recorded.Outcome,
                recorded.ApprovalId,
                recorded.Superseded,
                recorded.Outcome == NhAssistantProposeOutcome.RecentlyRejected ? recorded.Existing?.DecidedAt : null,
                null);
        }
        finally
        {
            _rationale = null;
            _lastProposal = null;
        }
    }

    private static string? CodeOf(object? result)
    {
        return result is JsonElement { ValueKind: JsonValueKind.Object } json
               && json.TryGetProperty("code", out var code)
               && code.ValueKind == JsonValueKind.String
            ? code.GetString()
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

    public async Task RecordBusinessEventAsync(NhAssistantAuditEvent evt, CancellationToken cancellationToken)
    {
        if (state.AgentTurn is { } agentTurn)
        {
            evt = evt with
            {
                RunId = agentTurn.RunId,
                TriggerId = agentTurn.TriggerId,
                SubjectType = agentTurn.Thread.SubjectType,
                SubjectId = agentTurn.Thread.SubjectId,
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
        CancellationToken cancellationToken)
    {
        if (call.CapturedArguments is null
            || call.CapturedContext is not { RunId: not null, AccountableOwnerId: not null } context)
        {
            // Another evidence provider answered; the assistant cannot bind a proposal to this call.
            return null;
        }

        var agentTurn = state.AgentTurn;
        var consultation = state.Scope.Consultation;
        var policy = agentTurn is not null && approvalPolicies is not null
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
        // rule turn has no model to continue with a decision, so its proposals always wait deferred.
        var mode = policy is null || (agentTurn!.IsRequestTurn && consultation is null)
            ? NhAiApprovalMode.Inline
            : state.RuleTurn
                ? NhAiApprovalMode.Deferred
                : policy.Mode;
        // A run in a shadow thread proposes only shadow proposals, which never execute.
        var shadow = agentTurn is { RunId: not null } && agentTurn.Thread.Shadow;
        var bindPrompt = policy is null || policy.BindTo == NhAiApprovalBinding.ToolContractAndPrompt;
        var lifetime = policy?.Lifetime ?? state.Scope.Limits.ApprovalLifetime;

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(lifetime);
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
            context.AccountableOwnerId,
            descriptor,
            call.CapturedArguments,
            targets,
            intent,
            [EffectCode(descriptor.Effect)],
            new Dictionary<string, string>(StringComparer.Ordinal),
            new NhAiActionBudget(1),
            now,
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
        // Proposals of a person's own turn wait in the conversation; only agent proposals can be
        // revalidated or deduplicated.
        var agentProposal = agentTurn is not null && !shadow && !(mode == NhAiApprovalMode.Inline && agentTurn.RunId is null);
        var keys = agentProposal
            ? await GuardKeysAsync(approvalId, descriptor.Id, proposal, now, cancellationToken)
            : [];
        var duplicate = agentProposal && duplicates is not null && policy is not null
            ? await duplicates.CheckAsync(agentTurn!.Thread, descriptor.Id, argumentsHash, keys, policy, cancellationToken)
            : NhAssistantDuplicateCheck.None;
        if (duplicate is { Outcome: not NhAssistantProposeOutcome.Created, Existing: { } existing })
        {
            return await SkipDuplicateAsync(descriptor, row, duplicate.Outcome, existing, mode, cancellationToken);
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
        var approval = new AssistantApproval
        {
            Id = approvalId,
            ConversationId = state.Conversation.Id,
            TurnId = state.Scope.TurnId,
            ToolInvocationId = row.Id,
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
            PolicyId = policy?.Id,
            Mode = NhAssistantApprovalModes.From(mode),
            PolicyJson = policy is null ? null : NhAssistantApprovalPolicySnapshot.Serialize(policy),
            InitiatorActorId = agentTurn is null ? state.Scope.OwnerActorId : agentTurn.InitiatorActorId,
            SubjectVersion = agentTurn?.Subject.Version,
            GrantSource = context.GrantSource,
            ConsultationId = consultation?.ConsultationId,
            NextReminderAt = policy is not null && mode == NhAiApprovalMode.Deferred && !shadow
                ? NhAssistantApprovalReminders.Next(policy, now, 0, expiresAt)
                : null,
            ArgumentsHash = argumentsHash,
            RationalesJson = _rationale is null ? null : texts?.Render(_rationale),
            ConcurrencyStamp = Guid.NewGuid()
        };
        if (consultation is not null)
        {
            // The execution of an approved proposal replays the stored invocation.
            await store.AddToolInvocationAsync(row, cancellationToken);
        }

        await store.AddApprovalAsync(
            approval,
            keys.Select(key => new AssistantApprovalKey
                {
                    ApprovalId = approval.Id,
                    ScopeKey = agentTurn!.Thread.ScopeKey,
                    KeyType = key.Type,
                    KeyId = key.Id
                })
                .ToArray(),
            cancellationToken);
        var superseded = new List<Guid>();
        foreach (var older in duplicate.Superseded)
        {
            // The newer proposal for the same objects replaces the pending one.
            if (invalidation is not null
                && await invalidation.SupersedeAsync(older, agentTurn!.Thread, approval.Id, state.Scope.Agent.ActorId, CancellationToken.None) is not null)
            {
                superseded.Add(older.Id);
            }
        }

        row.Status = NhAssistantToolCallStatuses.AwaitingApproval;
        row.ProposalId = proposal.ProposalId;
        row.ApprovalId = approval.Id;
        row.GrantSource = context.GrantSource;
        await store.UpdateToolInvocationAsync(row, cancellationToken);
        state.ProposalCount++;

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
                StartedAt = row.StartedAt
            },
            cancellationToken);
        return new NhAssistantRecordedProposal(approval.Id, proposal.ProposalId, mode, shadow)
        {
            Superseded = superseded
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
        return new NhAssistantRecordedProposal(existing.Id, existing.ProposalId, mode, false)
        {
            Outcome = outcome,
            Existing = existing
        };
    }

    /// <summary>
    /// The application objects an agent proposal changes, as its guards name them, so the application
    /// can revalidate it by object and recognize a newer proposal for the same objects.
    /// </summary>
    private async Task<IReadOnlyList<NhAiProposalKey>> GuardKeysAsync(
        Guid approvalId,
        string toolId,
        NhAiProposal proposal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (proposalGuards is null || state.AgentTurn is not { } agentTurn)
        {
            return [];
        }

        var thread = agentTurn.Thread;
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
    /// The pending proposals a newly recorded one replaced.
    /// </summary>
    public IReadOnlyList<Guid> Superseded { get; init; } = [];
}
