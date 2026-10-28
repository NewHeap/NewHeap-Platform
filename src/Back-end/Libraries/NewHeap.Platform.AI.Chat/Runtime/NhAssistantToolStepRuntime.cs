using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// One step as the interceptor runs it: its position, the call id of its row and the origin the invoker
/// puts on its context.
/// </summary>
/// <param name="UsesCallerCount">
/// Whether the step counts against the turn with the count of the call that runs it: the first step that
/// reaches its tool does, so a composition counts as the tool calls it ran, and at least one.
/// </param>
internal sealed record NhAssistantStepCall(int Index, string CallId, NhAiToolCallOrigin Origin, bool UsesCallerCount);

/// <summary>
/// A call the interceptor runs: a call of the model, or a step of a composition or script.
/// </summary>
internal sealed record NhAssistantCallRequest(
    NhAiToolDescriptor Descriptor,
    AIFunction Function,
    string CallId,
    string FunctionName,
    AIFunctionArguments Arguments,
    Func<CancellationToken, ValueTask<object?>> Invoke)
{
    public NhAssistantStepCall? Step { get; init; }
}

/// <summary>
/// How a call ended in the interceptor: what the model receives and whether its loop ends, and for a
/// step how the chain goes on.
/// </summary>
internal sealed record NhAssistantCallOutcome(object? Result, NhAssistantToolStepStatus Status)
{
    public bool Terminate { get; init; }

    public string? Code { get; init; }

    public Guid? ProposalId { get; init; }

    public Guid? InvocationId { get; init; }

    public static NhAssistantCallOutcome Refused(object? result, string code, bool terminate = false)
    {
        return new NhAssistantCallOutcome(result, NhAssistantToolStepStatus.Refused)
        {
            Code = code,
            Terminate = terminate
        };
    }

    public static NhAssistantCallOutcome Failed(object? result, string code, Guid invocationId)
    {
        return new NhAssistantCallOutcome(result, NhAssistantToolStepStatus.Failed)
        {
            Code = code,
            InvocationId = invocationId
        };
    }

    public static NhAssistantCallOutcome Proposed(object? result, NhAssistantRecordedProposal proposal, Guid invocationId)
    {
        return new NhAssistantCallOutcome(result, NhAssistantToolStepStatus.Proposed)
        {
            Code = CodeOf(result),
            ProposalId = proposal.ProposalId == Guid.Empty ? null : proposal.ProposalId,
            InvocationId = invocationId
        };
    }

    /// <summary>
    /// The result as JSON, as the model receives it.
    /// </summary>
    public static JsonElement ToJson(object? result)
    {
        return result switch
        {
            JsonElement element => element.Clone(),
            null => NhAiToolResult.Failed(NhAiToolFailureCodes.Failed, null),
            _ => JsonSerializer.SerializeToElement(result, result.GetType(), NhAssistantContent.JsonOptions)
        };
    }

    private static string? CodeOf(object? result)
    {
        var json = ToJson(result);
        return json.ValueKind == JsonValueKind.Object
               && json.TryGetProperty(NhAiToolResult.CodePropertyName, out var code)
               && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;
    }
}

/// <summary>
/// How the chain a call runs ended, which decides how that call itself ends in the interceptor.
/// </summary>
internal interface INhAssistantStepChain
{
    /// <summary>The chain stopped at a step, so the call ends the model's loop.</summary>
    bool Stopped { get; }

    /// <summary>How the call ends, given how its result classified it.</summary>
    NhAssistantToolOutcome ParentOutcome(NhAssistantToolOutcome classified);
}

/// <summary>
/// Where a session runs its steps: through the interceptor of an assistant turn, or directly through
/// the governed functions for a caller outside a turn.
/// </summary>
internal interface INhAssistantStepHost
{
    Task<NhAssistantCallOutcome> RunAsync(
        NhAiToolDescriptor descriptor,
        JsonElement arguments,
        NhAssistantStepCall step,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(NhAssistantToolStepSession session, JsonElement result, CancellationToken cancellationToken);
}

/// <summary>
/// The call ids of step rows. A step's row is a call of the turn whose call id names the call that ran
/// it, so the activity finds the steps of a composition or script call without a column of its own:
/// <c>nh-composition:{parent}:{index}</c> or <c>nh-script:{parent}:{index}</c>, with the parent's
/// invocation id in format <c>N</c>.
/// </summary>
internal static class NhAssistantToolStepCallIds
{
    public const string CompositionPrefix = "nh-composition:";

    public const string ScriptPrefix = "nh-script:";

    public static string Of(NhAiToolCallOriginKind kind, Guid parentInvocationId, int index)
    {
        return PrefixOf(kind) + parentInvocationId.ToString("N") + ":" + index.ToString(CultureInfo.InvariantCulture);
    }

    public static string PrefixOf(NhAiToolCallOriginKind kind)
    {
        return kind == NhAiToolCallOriginKind.Script ? ScriptPrefix : CompositionPrefix;
    }

    /// <summary>The call id prefix of every step of one call.</summary>
    public static string PrefixOf(NhAiToolCallOriginKind kind, Guid parentInvocationId)
    {
        return PrefixOf(kind) + parentInvocationId.ToString("N") + ":";
    }

    public static bool IsStep(string? callId)
    {
        return callId is not null
               && (callId.StartsWith(CompositionPrefix, StringComparison.Ordinal)
                   || callId.StartsWith(ScriptPrefix, StringComparison.Ordinal));
    }

    public static bool TryParse(string? callId, out NhAiToolCallOriginKind kind, out Guid parentInvocationId, out int index)
    {
        kind = default;
        parentInvocationId = default;
        index = -1;
        if (callId is null)
        {
            return false;
        }

        string rest;
        if (callId.StartsWith(CompositionPrefix, StringComparison.Ordinal))
        {
            kind = NhAiToolCallOriginKind.Composition;
            rest = callId[CompositionPrefix.Length..];
        }
        else if (callId.StartsWith(ScriptPrefix, StringComparison.Ordinal))
        {
            kind = NhAiToolCallOriginKind.Script;
            rest = callId[ScriptPrefix.Length..];
        }
        else
        {
            return false;
        }

        var separator = rest.IndexOf(':', StringComparison.Ordinal);
        return separator == 32
               && Guid.TryParseExact(rest[..separator], "N", out parentInvocationId)
               && int.TryParse(rest[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }
}

/// <summary>
/// The tools that run chains of other tools: compositions and the script tool. No step calls one, and
/// a turn counts their calls by the steps they run.
/// </summary>
internal static class NhAssistantToolChains
{
    /// <summary>
    /// Whether the tool is a composition or the script tool (<see cref="NhAiToolDescriptor.CompositionFeature"/>).
    /// </summary>
    public static bool IsChain(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.CompositionFeature != NhAiCompositionFeature.None;
    }

    /// <summary>
    /// Whether a step may call the tool: no chain, no consultation or request of another agent, and not
    /// the caller itself.
    /// </summary>
    public static bool MayBeStep(NhAiToolDescriptor descriptor, NhAiToolDescriptor caller)
    {
        return !string.Equals(descriptor.Id, caller.Id, StringComparison.Ordinal)
               && !IsChain(descriptor)
               && !NhAssistantConsultationTools.IsConsultationTool(descriptor.Id);
    }
}

/// <summary>
/// The step runtime (<see cref="INhAssistantToolSteps"/>).
/// </summary>
internal sealed class NhAssistantToolSteps(
    IServiceProvider services,
    IEnumerable<INhAiToolCatalog> catalogs,
    INhAiToolInvocationGate gate,
    INhAiToolDiscoveryService discovery,
    IEnumerable<INhAiAuditSink> auditSinks) : INhAssistantToolSteps
{
    private readonly IReadOnlyList<INhAiAuditSink> _auditSinks = auditSinks.ToArray();

    public async ValueTask<TaskResult<NhAssistantToolStepSession>> BeginAsync(
        NhAssistantToolStepRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxSteps is < 1 or > NhAssistantToolStepLimits.MaxSteps)
        {
            throw new ArgumentException(
                $"A step session runs from 1 to {NhAssistantToolStepLimits.MaxSteps} steps.",
                nameof(request));
        }
        if (request.MaxMutationSteps < 0 || request.MaxMutationSteps > request.MaxSteps)
        {
            throw new ArgumentException(
                "A step session's mutation steps are from 0 to its steps.",
                nameof(request));
        }
        if (request.Hash is { } hash && (hash.Length is 0 or > NhAiToolCallOrigin.MaxHashLength || !hash.All(char.IsAsciiLetterOrDigit)))
        {
            throw new ArgumentException(
                $"The hash of a step session is at most {NhAiToolCallOrigin.MaxHashLength} ASCII letters or digits.",
                nameof(request));
        }

        var caller = request.Caller;
        var turn = NhAssistantExecutionScope.CurrentTurn;
        var call = NhAssistantExecutionScope.CurrentCall;
        if (turn is not null && call is not { Interceptor: not null, Row: not null })
        {
            // An approved proposal that executes, or a call outside the model's loop, runs no chain.
            return TaskResult<NhAssistantToolStepSession>.Failed(
                NhAssistantToolStepCodes.HostUnavailable,
                "Steps run only from a call the model makes in an assistant turn.");
        }
        if (call is not null && !string.Equals(call.Descriptor.Id, caller.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The step session of '{caller.Id}' is opened from the call of another tool.");
        }

        // The call is authenticated as any call is; every step is authorized on its own.
        var authorized = await gate.AuthorizeAsync(caller, cancellationToken);
        if (!authorized.Success)
        {
            var denied = TaskResult<NhAssistantToolStepSession>.Failed(authorized);
            await WriteAuditAsync(
                caller,
                null,
                call?.InvocationId ?? Guid.NewGuid(),
                NhAiOutcomeKind.AuthorizationDenied,
                NhAiToolResult.GetCode(denied),
                null,
                cancellationToken);
            return denied;
        }

        var context = authorized.Data;
        var parentInvocationId = call?.InvocationId ?? context.InvocationId;
        var origin = new NhAiToolCallOrigin(request.Kind, caller.Id, request.Hash ?? HashOf(caller))
        {
            ParentInvocationId = parentInvocationId
        };

        INhAssistantStepHost host;
        IReadOnlyList<NhAiToolDescriptor> offered;
        if (call is { Interceptor: { } interceptor })
        {
            offered = interceptor.OfferedTools;
            host = new TurnHost(this, interceptor, context, parentInvocationId);
        }
        else
        {
            var exposure = ExposureOfCall();
            offered = await discovery.DiscoverAsync(new NhAiToolDiscoveryRequest(context, exposure), cancellationToken);
            host = new DirectHost(this, context, parentInvocationId);
        }

        var tools = offered
            .Where(descriptor => NhAssistantToolChains.MayBeStep(descriptor, caller))
            .Where(descriptor => request.Tools?.Invoke(descriptor) ?? true)
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Version)
            .ToArray();
        var session = new NhAssistantToolStepSession(request, origin, tools, host);
        if (call is not null)
        {
            // The interceptor ends the call as the chain ends.
            call.Chain = session;
        }

        return TaskResult<NhAssistantToolStepSession>.Succeeded(session);
    }

    /// <summary>
    /// The exposure a call outside a turn came through: an agent's outside the assistant, an MCP client's,
    /// or the application's own.
    /// </summary>
    private static NhAiToolExposure ExposureOfCall()
    {
        if (NhAiAgentInvocationScopes.Current is not null)
        {
            return NhAiToolExposure.Agent;
        }

        return string.Equals(NhAiBudgetPurposeScope.Current, NhAiBudgetPurposes.Mcp, StringComparison.Ordinal)
            ? NhAiToolExposure.Mcp
            : NhAiToolExposure.Local;
    }

    private static string? HashOf(NhAiToolDescriptor caller)
    {
        var hash = caller.ContractHash;
        return hash.Length is > 0 and <= NhAiToolCallOrigin.MaxHashLength && hash.All(char.IsAsciiLetterOrDigit)
            ? hash
            : null;
    }

    /// <summary>
    /// The governed function of a step's tool, from the catalog view that holds its descriptor.
    /// </summary>
    private AIFunction? FunctionOf(NhAiToolDescriptor descriptor)
    {
        foreach (var catalog in catalogs.Select(NhAiToolCatalogViews.Of))
        {
            if (!catalog.Descriptors.Any(candidate => Matches(candidate, descriptor)))
            {
                continue;
            }

            foreach (var function in catalog.CreateFunctions(services))
            {
                if (function is INhAiGovernedAIFunction governed && Matches(governed.Descriptor, descriptor))
                {
                    return function;
                }
            }
        }

        return null;
    }

    private static bool Matches(NhAiToolDescriptor candidate, NhAiToolDescriptor descriptor)
    {
        return string.Equals(candidate.Id, descriptor.Id, StringComparison.Ordinal)
               && candidate.Version == descriptor.Version
               && string.Equals(candidate.ContractHash, descriptor.ContractHash, StringComparison.Ordinal);
    }

    private AIFunctionArguments ArgumentsOf(JsonElement arguments)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return new AIFunctionArguments(values)
        {
            Services = services
        };
    }

    /// <summary>
    /// Audits the composition or script call itself: content-free, with the outcome of the chain and the
    /// code of the step that failed it. Its steps carry their own records, under this one.
    /// </summary>
    private async ValueTask WriteAuditAsync(
        NhAiToolDescriptor caller,
        NhAiInvocationContext? context,
        Guid invocationId,
        NhAiOutcomeKind outcome,
        string? resultCode,
        string? approvalCode,
        CancellationToken cancellationToken)
    {
        if (_auditSinks.Count == 0)
        {
            return;
        }

        var record = new NhAiAuditRecord(
            invocationId,
            caller.Id,
            caller.Version,
            context?.ActorId,
            NhAiNames.IsSegment(context?.Purpose) ? context!.Purpose : null,
            outcome,
            DateTimeOffset.UtcNow)
        {
            RunId = context?.RunId,
            CorrelationId = context?.CorrelationId,
            ApprovalCode = approvalCode,
            ResultCode = resultCode,
            ActorKind = context?.ActorKind ?? NhAiActorKind.Human,
            AgentId = context?.AgentId,
            AccountableOwnerId = context?.AccountableOwnerId,
            GrantSource = context?.GrantSource ?? NhAiGrantSource.None
        };
        foreach (var sink in _auditSinks)
        {
            await sink.WriteAsync(record, cancellationToken);
        }
    }

    /// <summary>
    /// Audits a completed session: a chain that ended without a failure succeeded, also one that stopped
    /// at a step that waits for a decision; otherwise it failed with the code of the step that ended it.
    /// </summary>
    private async ValueTask CompleteAsync(
        NhAssistantToolStepSession session,
        NhAiInvocationContext context,
        Guid invocationId,
        JsonElement result,
        CancellationToken cancellationToken)
    {
        var success = result.ValueKind == JsonValueKind.Object
                      && result.TryGetProperty(NhAiToolResult.SuccessPropertyName, out var flag)
                      && flag.ValueKind == JsonValueKind.True;
        if (success || session.AwaitingApproval is not null)
        {
            await WriteAuditAsync(
                session.Caller,
                context,
                invocationId,
                NhAiOutcomeKind.Succeeded,
                null,
                session.AwaitingApproval is null ? null : "step-awaiting-approval",
                cancellationToken);
            return;
        }

        var code = result.ValueKind == JsonValueKind.Object
                   && result.TryGetProperty(NhAiToolResult.CodePropertyName, out var named)
                   && named.ValueKind == JsonValueKind.String
                   && NhAiNames.IsSegment(named.GetString())
            ? named.GetString()
            : NhAiToolFailureCodes.Failed;
        await WriteAuditAsync(
            session.Caller,
            context,
            invocationId,
            NhAiOutcomeKind.TerminalFailure,
            code,
            null,
            cancellationToken);
    }

    /// <summary>
    /// Runs steps through the interceptor of the turn's model loop, as calls of the model would run.
    /// </summary>
    private sealed class TurnHost(
        NhAssistantToolSteps runtime,
        NhAssistantToolCallInterceptor interceptor,
        NhAiInvocationContext context,
        Guid parentInvocationId) : INhAssistantStepHost
    {
        public async Task<NhAssistantCallOutcome> RunAsync(
            NhAiToolDescriptor descriptor,
            JsonElement arguments,
            NhAssistantStepCall step,
            CancellationToken cancellationToken)
        {
            var function = descriptor.Id.StartsWith("mcp.", StringComparison.Ordinal)
                ? interceptor.FindMcpFunction(descriptor.ExportName)
                : runtime.FunctionOf(descriptor);
            if (function is null)
            {
                return NhAssistantCallOutcome.Refused(
                    NhAiToolResult.Failed(NhAssistantToolStepCodes.ToolUnavailable, "No tool by that name is available here."),
                    NhAssistantToolStepCodes.ToolUnavailable);
            }

            return await interceptor.RunStepAsync(descriptor, function, runtime.ArgumentsOf(arguments), step, cancellationToken);
        }

        public async ValueTask CompleteAsync(NhAssistantToolStepSession session, JsonElement result, CancellationToken cancellationToken)
        {
            await runtime.CompleteAsync(session, context, parentInvocationId, result, cancellationToken);
        }
    }

    /// <summary>
    /// Runs steps directly through their governed functions for a caller outside a turn, such as an MCP
    /// client: the invoker decides each step as it decides the same call, and a step that needs an
    /// approval fails with <see cref="NhAiToolFailureCodes.ApprovalRequired"/>, as that call does there.
    /// </summary>
    private sealed class DirectHost(
        NhAssistantToolSteps runtime,
        NhAiInvocationContext context,
        Guid parentInvocationId) : INhAssistantStepHost
    {
        public async Task<NhAssistantCallOutcome> RunAsync(
            NhAiToolDescriptor descriptor,
            JsonElement arguments,
            NhAssistantStepCall step,
            CancellationToken cancellationToken)
        {
            var function = runtime.FunctionOf(descriptor);
            if (function is null)
            {
                return NhAssistantCallOutcome.Refused(
                    NhAiToolResult.Failed(NhAssistantToolStepCodes.ToolUnavailable, "No tool by that name is available here."),
                    NhAssistantToolStepCodes.ToolUnavailable);
            }

            // The invoker reports the step's outcome with the code its caller receives, as it does to the
            // protocol adapters.
            var capture = NhAiToolOutcomeCapture.Begin();
            object? result;
            using (NhAiToolCallOrigins.Begin(step.Origin))
            {
                result = await function.InvokeAsync(runtime.ArgumentsOf(arguments), cancellationToken);
            }

            var json = NhAssistantCallOutcome.ToJson(result);
            var succeeded = capture.Recorded
                ? capture.Succeeded
                : json.ValueKind == JsonValueKind.Object
                  && json.TryGetProperty(NhAiToolResult.SuccessPropertyName, out var flag)
                  && flag.ValueKind == JsonValueKind.True;
            if (succeeded)
            {
                return new NhAssistantCallOutcome(json, NhAssistantToolStepStatus.Succeeded);
            }

            var code = capture is { Recorded: true, Code: { } captured }
                ? captured
                : json.ValueKind == JsonValueKind.Object
                  && json.TryGetProperty(NhAiToolResult.CodePropertyName, out var named)
                  && named.ValueKind == JsonValueKind.String
                    ? named.GetString()
                    : NhAiToolFailureCodes.Failed;
            return new NhAssistantCallOutcome(json, NhAssistantToolStepStatus.Failed)
            {
                Code = code
            };
        }

        public async ValueTask CompleteAsync(NhAssistantToolStepSession session, JsonElement result, CancellationToken cancellationToken)
        {
            await runtime.CompleteAsync(session, context, parentInvocationId, result, cancellationToken);
        }
    }
}
