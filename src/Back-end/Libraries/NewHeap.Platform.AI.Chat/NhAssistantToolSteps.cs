using System.Text.Json;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The step runtime of compositions and scripts. A composition, or the script tool of
/// <c>NewHeap.Platform.AI.Scripting</c>, opens a session from inside its own governed call and runs each
/// step through it: a call of a registered governed tool by its function name or id, with JSON
/// arguments. Every step runs through the shared invoker with the caller's context (the person or
/// agent, the tenant, the conversation, the turn and the channel) and gets exactly the decision the same
/// call gets on its own: rights, <see cref="NhAiToolEffect"/> and <see cref="NhAiApprovalRequirement"/>,
/// the approval policy with <see cref="NhAiApprovalMode.Inline"/> or <see cref="NhAiApprovalMode.Deferred"/>,
/// proposal guards, the budget (per step) and the audit (per step, under the call that runs it). The
/// runtime adds no rule of its own apart from the limits the caller passes in.
/// </summary>
/// <remarks>
/// <para>
/// In an assistant turn (a person's chat, an agent run, a consultation or a hand-off) a step may call
/// only a tool the same agent is offered in that turn; outside a turn, such as over MCP, only a tool the
/// caller may discover through the same exposure. Neither may call a composition, the script tool or a
/// consultation tool. Each step is a call of the turn with a tool invocation of its own, which the
/// agent's activity shows under the call that ran it; the message shows no separate card for it.
/// </para>
/// <para>
/// A step that waits for a person: with <see cref="NhAiApprovalMode.Deferred"/> it returns the same
/// "proposal recorded" result the model would get (<see cref="NhAssistantToolStepStatus.Proposed"/>) and
/// the chain may continue; with <see cref="NhAiApprovalMode.Inline"/> it returns
/// <see cref="NhAssistantToolStepStatus.AwaitingApproval"/>, the session stops
/// (<see cref="NhAssistantToolStepSession.Stopped"/>) and the turn waits for the decision as it does for
/// a call of the model. A shadow run never executes a mutation: every effect becomes a shadow proposal.
/// </para>
/// <para>
/// The invoker marks every step's <see cref="NhAiInvocationContext.Origin"/> and audit record, and the
/// assistant the <see cref="NhAiProposalGuardContext.Origin"/> of a proposal a step makes, with the
/// composition's or script tool's id, the script's hash and the call that runs the step.
/// </para>
/// </remarks>
public interface INhAssistantToolSteps
{
    /// <summary>
    /// Opens the session of the composition or script call that runs in the current asynchronous flow:
    /// call it from inside that tool's governed function. The call itself is authenticated through the
    /// application's invocation gate; a refusal returns its code and runs no step.
    /// </summary>
    /// <exception cref="ArgumentException">The request is invalid.</exception>
    ValueTask<TaskResult<NhAssistantToolStepSession>> BeginAsync(
        NhAssistantToolStepRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a composition or script call asks of the step runtime.
/// </summary>
public sealed record NhAssistantToolStepRequest
{
    /// <param name="caller">
    /// The descriptor of the composition or script tool whose call runs the steps; it is never a step
    /// itself.
    /// </param>
    /// <param name="kind">Whether a composition or a script runs the steps.</param>
    public NhAssistantToolStepRequest(NhAiToolDescriptor caller, NhAiToolCallOriginKind kind)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Caller = caller;
        Kind = kind;
    }

    /// <summary>The descriptor of the composition or script tool whose call runs the steps.</summary>
    public NhAiToolDescriptor Caller { get; }

    /// <summary>Whether a composition or a script runs the steps.</summary>
    public NhAiToolCallOriginKind Kind { get; }

    /// <summary>
    /// The hash the steps' origin carries (<see cref="NhAiToolCallOrigin.Hash"/>): for a script the
    /// SHA-256 of its text as lowercase hexadecimal; <see langword="null"/> uses the caller's contract hash.
    /// </summary>
    public string? Hash { get; init; }

    /// <summary>
    /// The most steps the session runs, refused ones included; a further step is refused with
    /// <see cref="NhAssistantToolStepCodes.StepLimitReached"/>. From 1 to <see cref="NhAssistantToolStepLimits.MaxSteps"/>.
    /// </summary>
    public int MaxSteps { get; init; } = NhAssistantToolStepLimits.DefaultMaxSteps;

    /// <summary>
    /// The most steps of a tool whose effect is not <see cref="NhAiToolEffect.ReadOnly"/>, whether they
    /// execute or become proposals; a further one is refused with
    /// <see cref="NhAssistantToolStepCodes.MutationLimitReached"/>. 0 makes the session read-only.
    /// </summary>
    public int MaxMutationSteps { get; init; } = NhAssistantToolStepLimits.DefaultMaxMutationSteps;

    /// <summary>
    /// Narrows the tools a step may call, for example to leave out destructive tools that did not opt
    /// in; <see langword="null"/> keeps every tool the runtime allows. It never widens them.
    /// </summary>
    public Func<NhAiToolDescriptor, bool>? Tools { get; init; }
}

/// <summary>
/// The limits of the step runtime.
/// </summary>
public static class NhAssistantToolStepLimits
{
    /// <summary>The default <see cref="NhAssistantToolStepRequest.MaxSteps"/>.</summary>
    public const int DefaultMaxSteps = 16;

    /// <summary>The largest <see cref="NhAssistantToolStepRequest.MaxSteps"/>.</summary>
    public const int MaxSteps = 64;

    /// <summary>The default <see cref="NhAssistantToolStepRequest.MaxMutationSteps"/>.</summary>
    public const int DefaultMaxMutationSteps = 4;
}

/// <summary>
/// The stable codes of the step runtime. A step refused with one of them never reached its tool.
/// </summary>
public static class NhAssistantToolStepCodes
{
    /// <summary>
    /// The step names a tool that is not offered here: not to this agent in this turn, not to this caller
    /// through this exposure, or a composition, script or consultation tool, which no step calls.
    /// </summary>
    public const string ToolUnavailable = "assistant-step-tool-unavailable";

    /// <summary>The session ran as many steps as <see cref="NhAssistantToolStepRequest.MaxSteps"/> allows.</summary>
    public const string StepLimitReached = "assistant-step-limit-reached";

    /// <summary>The session ran as many mutation steps as <see cref="NhAssistantToolStepRequest.MaxMutationSteps"/> allows.</summary>
    public const string MutationLimitReached = "assistant-step-mutation-limit-reached";

    /// <summary>
    /// The session stopped at an earlier step, such as one that waits for a person's inline decision or
    /// one that reached a limit of the turn, so no further step runs in this call.
    /// </summary>
    public const string ChainStopped = "assistant-step-chain-stopped";

    /// <summary>The step's arguments are no JSON object.</summary>
    public const string ArgumentsInvalid = "assistant-step-arguments-invalid";

    /// <summary>
    /// The call that opens the session runs in an assistant turn that cannot run steps, such as the
    /// execution of an approved proposal.
    /// </summary>
    public const string HostUnavailable = "assistant-step-host-unavailable";
}

/// <summary>
/// How one step ended.
/// </summary>
public enum NhAssistantToolStepStatus
{
    /// <summary>The tool ran and returned its data.</summary>
    Succeeded = 0,

    /// <summary>
    /// The call became a proposal that waits for people while the chain may continue: deferred, in
    /// shadow, placed at another object or answered by a consultation's route, or an open or recently
    /// rejected identical proposal made a new one unnecessary. Nothing executed; later steps cannot
    /// depend on its outcome.
    /// </summary>
    Proposed = 1,

    /// <summary>A proposal guard found the work already done or no longer applicable; nothing was proposed.</summary>
    NotNeeded = 2,

    /// <summary>
    /// The call waits for a person's inline decision. The session stops and the turn waits, as for a
    /// call of the model; after the decision the model continues.
    /// </summary>
    AwaitingApproval = 3,

    /// <summary>The call was checked or ran and failed: rights, effect, approval, budget or the tool itself.</summary>
    Failed = 4,

    /// <summary>The runtime or the turn refused the step before it reached its tool.</summary>
    Refused = 5
}

/// <summary>
/// One step of a session and how it ended.
/// </summary>
/// <param name="Index">The step's position in the session, from 0, refused steps included.</param>
/// <param name="ToolId">The id of the tool the step called, or the name it gave when no tool matched.</param>
/// <param name="Status">How the step ended.</param>
/// <param name="Result">
/// What the model would have received for the same call on its own: the shared tool result
/// (<see cref="NhAiToolResult"/>) or the assistant's result for a proposal or refusal.
/// </param>
public sealed record NhAssistantToolStep(
    int Index,
    string ToolId,
    NhAssistantToolStepStatus Status,
    JsonElement Result)
{
    /// <summary>The function name the tool is offered by; empty when no tool matched.</summary>
    public string FunctionName { get; init; } = string.Empty;

    /// <summary>The effect of the step's tool.</summary>
    public NhAiToolEffect Effect { get; init; }

    /// <summary>The stable code of a step that did not succeed.</summary>
    public string? Code { get; init; }

    /// <summary>The proposal the step made or met, when it is <see cref="NhAssistantToolStepStatus.Proposed"/> or <see cref="NhAssistantToolStepStatus.AwaitingApproval"/>.</summary>
    public Guid? ProposalId { get; init; }

    /// <summary>The step's tool invocation in the turn; <see langword="null"/> outside a turn and for a refused step.</summary>
    public Guid? InvocationId { get; init; }

    /// <summary>Whether the step reached its tool: every status but <see cref="NhAssistantToolStepStatus.Refused"/>.</summary>
    public bool Ran => Status != NhAssistantToolStepStatus.Refused;

    /// <summary>
    /// The data of a succeeded step: the <c>data</c> of its shared tool result.
    /// </summary>
    public bool TryGetData(out JsonElement data)
    {
        if (Status == NhAssistantToolStepStatus.Succeeded
            && Result.ValueKind == JsonValueKind.Object
            && Result.TryGetProperty(NhAiToolResult.DataPropertyName, out data))
        {
            return true;
        }

        data = default;
        return false;
    }
}

/// <summary>
/// What a composition or script call ran, as the agent's activity shows it.
/// </summary>
/// <param name="ToolCalls">The steps that reached their tool, in order.</param>
/// <param name="RoundsSaved">The model rounds the call saved: the tool calls minus one, at least 0.</param>
/// <param name="ResultCharacters">The size of the result returned to the model, in characters of JSON.</param>
public sealed record NhAssistantToolStepActivity(
    IReadOnlyList<NhAssistantToolStep> ToolCalls,
    int RoundsSaved,
    int ResultCharacters);

/// <summary>
/// The steps of one composition or script call. Steps run one after the other; a step started while
/// another runs waits for it.
/// </summary>
public sealed class NhAssistantToolStepSession : INhAssistantStepChain
{
    private readonly NhAssistantToolStepRequest _request;
    private readonly INhAssistantStepHost _host;
    private readonly IReadOnlyDictionary<string, NhAiToolDescriptor> _byName;
    private readonly List<NhAssistantToolStep> _steps = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _mutationSteps;
    private bool _callerCountUsed;
    private bool _completed;

    internal NhAssistantToolStepSession(
        NhAssistantToolStepRequest request,
        NhAiToolCallOrigin origin,
        IReadOnlyList<NhAiToolDescriptor> tools,
        INhAssistantStepHost host)
    {
        _request = request;
        _host = host;
        Origin = origin;
        Tools = tools;
        var byName = new Dictionary<string, NhAiToolDescriptor>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!string.IsNullOrWhiteSpace(tool.ExportName))
            {
                byName.TryAdd(tool.ExportName, tool);
            }
        }
        foreach (var tool in tools)
        {
            byName.TryAdd(tool.Id, tool);
        }

        _byName = byName;
    }

    /// <summary>The descriptor of the composition or script tool whose call runs the steps.</summary>
    public NhAiToolDescriptor Caller => _request.Caller;

    /// <summary>
    /// The origin every step carries, without its <see cref="NhAiToolCallOrigin.StepIndex"/>: the kind,
    /// the caller's id, the hash and the invocation of the call that runs the steps.
    /// </summary>
    public NhAiToolCallOrigin Origin { get; }

    /// <summary>
    /// The tools a step may call here, in a stable order; for example to describe them to the model that
    /// writes a script.
    /// </summary>
    public IReadOnlyList<NhAiToolDescriptor> Tools { get; }

    /// <summary>Every step so far, in order.</summary>
    public IReadOnlyList<NhAssistantToolStep> Steps
    {
        get
        {
            lock (_steps)
            {
                return _steps.ToArray();
            }
        }
    }

    /// <summary>
    /// Whether the session stopped: a step waits for a person's inline decision, or the turn reached one
    /// of its limits. Every further step is refused with <see cref="NhAssistantToolStepCodes.ChainStopped"/>,
    /// and the call that runs the session ends the model's loop, so the turn waits or closes as it would
    /// after the same call of the model.
    /// </summary>
    public bool Stopped { get; private set; }

    /// <summary>The code of the step that stopped the session.</summary>
    public string? StopCode { get; private set; }

    /// <summary>The step that stopped the session at an inline approval, if one did.</summary>
    public NhAssistantToolStep? AwaitingApproval { get; private set; }

    /// <summary>
    /// The tool a step names: by the function name it is offered by, otherwise by its id;
    /// <see langword="null"/> when the session offers none by that name.
    /// </summary>
    public NhAiToolDescriptor? FindTool(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out var tool) ? tool : null;
    }

    /// <summary>
    /// Runs one step: the tool named <paramref name="tool"/> (its function name or id) with
    /// <paramref name="arguments"/>, a JSON object of the tool's input properties or the shared
    /// <c>{"input": {...}}</c> envelope. Expected outcomes are statuses, never exceptions.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session was completed.</exception>
    public async Task<NhAssistantToolStep> RunAsync(
        string tool,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_completed)
            {
                throw new InvalidOperationException("A completed step session runs no further steps.");
            }

            var index = Steps.Count;
            var descriptor = FindTool(tool);
            if (Stopped)
            {
                return Record(Refused(index, descriptor, tool, NhAssistantToolStepCodes.ChainStopped));
            }
            if (index >= _request.MaxSteps)
            {
                return Record(Refused(index, descriptor, tool, NhAssistantToolStepCodes.StepLimitReached));
            }
            if (descriptor is null)
            {
                return Record(Refused(index, null, tool, NhAssistantToolStepCodes.ToolUnavailable));
            }
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return Record(Refused(index, descriptor, tool, NhAssistantToolStepCodes.ArgumentsInvalid));
            }
            if (descriptor.Effect != NhAiToolEffect.ReadOnly)
            {
                if (_mutationSteps >= _request.MaxMutationSteps)
                {
                    return Record(Refused(index, descriptor, tool, NhAssistantToolStepCodes.MutationLimitReached));
                }

                _mutationSteps++;
            }

            var origin = Origin with { StepIndex = index };
            var call = new NhAssistantStepCall(
                index,
                NhAssistantToolStepCallIds.Of(_request.Kind, Origin.ParentInvocationId!.Value, index),
                origin,
                !_callerCountUsed);
            var outcome = await _host.RunAsync(descriptor, arguments, call, cancellationToken);
            if (outcome.InvocationId is not null)
            {
                _callerCountUsed = true;
            }

            var step = new NhAssistantToolStep(index, descriptor.Id, outcome.Status, NhAssistantCallOutcome.ToJson(outcome.Result))
            {
                FunctionName = descriptor.ExportName,
                Effect = descriptor.Effect,
                Code = outcome.Code,
                ProposalId = outcome.ProposalId,
                InvocationId = outcome.InvocationId
            };
            if (outcome.Terminate)
            {
                Stopped = true;
                StopCode = outcome.Code;
                if (outcome.Status == NhAssistantToolStepStatus.AwaitingApproval)
                {
                    AwaitingApproval = step;
                }
            }

            return Record(step);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends the session with the result the composition or script returns to the model, and records the
    /// call: its own audit record (<see cref="NhAiAuditRecord"/> of the caller, with the outcome and the
    /// failing step's code) and, in a turn, the activity the agent's administration shows. Call it once.
    /// </summary>
    /// <param name="result">The result returned to the model, in the shared tool result shape.</param>
    /// <exception cref="InvalidOperationException">The session was completed already.</exception>
    public async Task<NhAssistantToolStepActivity> CompleteAsync(
        JsonElement result,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_completed)
            {
                throw new InvalidOperationException("A step session is completed once.");
            }

            _completed = true;
            var ran = Steps.Where(step => step.Ran).ToArray();
            var activity = new NhAssistantToolStepActivity(
                ran,
                Math.Max(0, ran.Length - 1),
                result.ValueKind == JsonValueKind.Undefined ? 0 : result.GetRawText().Length);
            await _host.CompleteAsync(this, result, cancellationToken);
            return activity;
        }
        finally
        {
            _gate.Release();
        }
    }

    bool INhAssistantStepChain.Stopped => Stopped;

    NhAssistantToolOutcome INhAssistantStepChain.ParentOutcome(NhAssistantToolOutcome classified)
    {
        // A chain that stopped at a decision did what it could; the decision belongs to its step.
        return AwaitingApproval is not null
            ? new NhAssistantToolOutcome(NhAssistantToolOutcomeKind.Succeeded, "succeeded")
            : classified;
    }

    private NhAssistantToolStep Record(NhAssistantToolStep step)
    {
        lock (_steps)
        {
            _steps.Add(step);
        }

        return step;
    }

    private static NhAssistantToolStep Refused(int index, NhAiToolDescriptor? descriptor, string name, string code)
    {
        return new NhAssistantToolStep(index, descriptor?.Id ?? name, NhAssistantToolStepStatus.Refused, NhAiToolResult.Failed(code, RefusalMessage(code)))
        {
            FunctionName = descriptor?.ExportName ?? string.Empty,
            Effect = descriptor?.Effect ?? NhAiToolEffect.ReadOnly,
            Code = code
        };
    }

    private static string RefusalMessage(string code)
    {
        return code switch
        {
            NhAssistantToolStepCodes.ToolUnavailable => "No tool by that name is available here.",
            NhAssistantToolStepCodes.StepLimitReached => "This call ran as many steps as it may.",
            NhAssistantToolStepCodes.MutationLimitReached => "This call ran as many changing steps as it may.",
            NhAssistantToolStepCodes.ChainStopped => "An earlier step stopped this call; no further step runs.",
            NhAssistantToolStepCodes.ArgumentsInvalid => "The arguments of a step are a JSON object.",
            _ => "The step was refused."
        };
    }
}
