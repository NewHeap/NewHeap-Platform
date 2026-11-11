using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Mcp;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The proposals an exported tool call of an MCP client recorded for a person to decide, collected in
/// the call's asynchronous flow: proposals of <c>INhAssistantProposals.ProposeForAsync</c> and those of a
/// consultation that wait for the subject's approvers.
/// </summary>
internal static class NhAssistantMcpCallProposals
{
    private static readonly AsyncLocal<Collector?> Current = new();

    /// <summary>
    /// Collects the proposals recorded in this flow until the returned collector is disposed.
    /// </summary>
    public static Collector Begin()
    {
        var collector = new Collector(Current.Value);
        Current.Value = collector;
        return collector;
    }

    /// <summary>
    /// Notes a proposal that waits for a person, made for <paramref name="initiatorActorId"/>; nothing
    /// outside an exported call's flow.
    /// </summary>
    public static void Record(Guid approvalId, string initiatorActorId)
    {
        Current.Value?.Add(approvalId, initiatorActorId);
    }

    internal sealed class Collector(Collector? previous) : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<(Guid ApprovalId, string InitiatorActorId)> _recorded = [];

        public IReadOnlyList<(Guid ApprovalId, string InitiatorActorId)> Recorded
        {
            get
            {
                lock (_sync)
                {
                    return _recorded.ToArray();
                }
            }
        }

        public void Add(Guid approvalId, string initiatorActorId)
        {
            lock (_sync)
            {
                if (!_recorded.Any(item => item.ApprovalId == approvalId))
                {
                    _recorded.Add((approvalId, initiatorActorId));
                }
            }
        }

        public void Dispose()
        {
            Current.Value = previous;
        }
    }
}

/// <summary>
/// Sends the person of an MCP client to the application's approval page when an exported tool call
/// left proposals for a person to decide (<see cref="NhAssistantMcpOptions.ApprovalLinkTemplate"/>):
/// the call answers <c>input_required</c> with a URL elicitation of the first open proposal and a
/// protected request state that names only the proposals, the caller and the tool. The client's retry
/// never runs the tool again: the state is unprotected and checked against the retry's own caller and
/// tool, every proposal must still be one made for that caller, and the retry returns their decision.
/// The decision itself stays on the approval page, bound to the proposal's hash; an accepted
/// elicitation is the person's word that they looked, never an approval.
/// <para>
/// Without the template, for a client that does not support multi round-trip requests and link
/// requests, and for a call that left no proposal, the tool's result goes back unchanged.
/// </para>
/// </summary>
internal sealed partial class NhAssistantMcpApprovalElicitation : INhAiMcpCallStep
{
    private readonly IOptionsMonitor<NhAssistantOptions> options;
    private readonly IDataProtectionProvider dataProtection;
    private readonly Func<Guid, CancellationToken, Task<AssistantApproval?>> findApproval;
    private readonly ILogger<NhAssistantMcpApprovalElicitation> logger;

    public NhAssistantMcpApprovalElicitation(
        IOptionsMonitor<NhAssistantOptions> options,
        IDataProtectionProvider dataProtection,
        INhAssistantStore store,
        ILogger<NhAssistantMcpApprovalElicitation> logger)
        : this(options, dataProtection, store.FindApprovalAsync, logger)
    {
    }

    /// <summary>
    /// The step with its own lookup of a proposal, for a test that decides the proposal itself.
    /// </summary>
    internal NhAssistantMcpApprovalElicitation(
        IOptionsMonitor<NhAssistantOptions> options,
        IDataProtectionProvider dataProtection,
        Func<Guid, CancellationToken, Task<AssistantApproval?>> findApproval,
        ILogger<NhAssistantMcpApprovalElicitation> logger)
    {
        this.options = options;
        this.dataProtection = dataProtection;
        this.findApproval = findApproval;
        this.logger = logger;
    }

    /// <summary>The prefix of the request states this step issues.</summary>
    internal const string StatePrefix = "nh-approval.v1.";

    /// <summary>The key of the one input request this step sends.</summary>
    internal const string InputKey = "approval";

    /// <summary>The key of the proposals' decisions in a retry's result metadata.</summary>
    internal const string ResultMetaKey = "newheap/approvals";

    /// <summary>How long a client may come back with a state.</summary>
    internal static readonly TimeSpan StateLifetime = TimeSpan.FromHours(24);

    /// <summary>The most times one call sends its person to the approval page.</summary>
    internal const int MaxRounds = 3;

    private const string Purpose = "NewHeap.Platform.AI.Chat.McpApprovalElicitation.v1";

    public async ValueTask<CallToolResult> InvokeAsync(
        NhAiMcpExportedCall call,
        Func<CancellationToken, ValueTask<CallToolResult>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(next);
        if (options.CurrentValue.Mcp.ApprovalLinkTemplate is null)
        {
            return await next(cancellationToken);
        }

        if (call.Request.Params?.RequestState is { } state && state.StartsWith(StatePrefix, StringComparison.Ordinal))
        {
            // A retry of a call that already left its proposals: never run the tool again.
            return await ResumeAsync(call, state, cancellationToken);
        }

        using var collected = NhAssistantMcpCallProposals.Begin();
        var result = await next(cancellationToken);
        var proposals = collected.Recorded
            .Where(item => string.Equals(item.InitiatorActorId, call.Invocation.ActorId, StringComparison.Ordinal))
            .Select(item => item.ApprovalId)
            .ToArray();
        if (result.IsError is true || proposals.Length == 0 || !SupportsLinks(call.Request))
        {
            return result;
        }

        throw Elicit(call, proposals, proposals[0], round: 1);
    }

    /// <summary>
    /// Whether the client of this request takes an <c>input_required</c> result with a link request.
    /// </summary>
    internal static bool SupportsLinks(RequestContext<CallToolRequestParams> request)
    {
        // A request of the 2026-07-28 revision declares the client's capabilities itself; an older
        // session declared them once.
        var capabilities = request.JsonRpcRequest?.Context?.ClientCapabilities ?? request.Server?.ClientCapabilities;
        return request.Server is { IsMrtrSupported: true } && capabilities?.Elicitation?.Url is not null;
    }

    private async Task<CallToolResult> ResumeAsync(NhAiMcpExportedCall call, string state, CancellationToken cancellationToken)
    {
        var payload = Read(state);
        if (payload is null
            || !string.Equals(payload.ActorId, call.Invocation.ActorId, StringComparison.Ordinal)
            || !string.Equals(payload.TenantId, call.Invocation.TenantId, StringComparison.Ordinal)
            || !string.Equals(payload.Tool, call.Descriptor.Id, StringComparison.Ordinal)
            || payload.ApprovalIds.Count == 0
            || payload.Round is < 1 or > MaxRounds)
        {
            LogStateRefused(call.Descriptor.Id);
            return Failure(NhAssistantErrorCodes.McpApprovalStateInvalid, "The request state does not belong to this call.", []);
        }

        var approvals = new List<AssistantApproval>(payload.ApprovalIds.Count);
        foreach (var approvalId in payload.ApprovalIds)
        {
            // Every proposal is read again and must still be one made for this caller.
            var approval = await findApproval(approvalId, cancellationToken);
            if (approval is null || !string.Equals(approval.InitiatorActorId, call.Invocation.ActorId, StringComparison.Ordinal))
            {
                LogStateRefused(call.Descriptor.Id);
                return Failure(NhAssistantErrorCodes.McpApprovalStateInvalid, "The request state does not belong to this call.", []);
            }

            approvals.Add(approval);
        }

        var open = approvals.FirstOrDefault(approval => !NhAssistantApprovalStatuses.IsClosed(approval.Status));
        if (open is null)
        {
            return Outcome(approvals);
        }

        // Still open: once more to the page when the person said they looked, otherwise it stays pending.
        if (AcceptedLink(call.Request.Params) && payload.Round < MaxRounds && SupportsLinks(call.Request))
        {
            throw Elicit(call, payload.ApprovalIds, open.Id, payload.Round + 1);
        }

        return Failure(
            NhAiToolFailureCodes.ApprovalRequired,
            "The proposal waits for a person's decision in the application.",
            approvals);
    }

    /// <summary>
    /// The <c>input_required</c> result that sends the person to the page of <paramref name="shown"/>.
    /// </summary>
    private InputRequiredException Elicit(NhAiMcpExportedCall call, IReadOnlyList<Guid> approvalIds, Guid shown, int round)
    {
        var url = options.CurrentValue.Mcp.ApprovalLinkOf(shown)
            ?? throw new InvalidOperationException("NewHeap:AI:Assistant:Mcp:ApprovalLinkTemplate is not valid.");
        return new InputRequiredException(
            new Dictionary<string, InputRequest>(StringComparer.Ordinal)
            {
                [InputKey] = InputRequest.ForElicitation(new ElicitRequestParams
                {
                    Mode = "url",
                    ElicitationId = shown.ToString("N") + "-" + round,
                    Url = url,
                    Message = "This change waits for a person's approval in the application. Open the page to review and decide it there, then continue."
                })
            },
            StateOf(call.Invocation.ActorId, call.Invocation.TenantId, call.Descriptor.Id, approvalIds, round));
    }

    /// <summary>
    /// The protected request state that names the caller, the tool, the proposals and the round, and
    /// nothing else.
    /// </summary>
    internal string StateOf(string actorId, string? tenantId, string toolId, IReadOnlyList<Guid> approvalIds, int round)
    {
        var state = new ApprovalState(actorId, tenantId, toolId, approvalIds, round);
        return StatePrefix + Protector().Protect(JsonSerializer.Serialize(state), StateLifetime);
    }

    /// <summary>
    /// Whether the person's answer to the link request of the previous round was <c>accept</c>.
    /// </summary>
    private static bool AcceptedLink(CallToolRequestParams? parameters)
    {
        if (parameters?.InputResponses is not { } responses || !responses.TryGetValue(InputKey, out var response))
        {
            return false;
        }

        try
        {
            return response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)?.IsAccepted == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private ApprovalState? Read(string state)
    {
        try
        {
            var json = Protector().Unprotect(state[StatePrefix.Length..]);
            return JsonSerializer.Deserialize<ApprovalState>(json);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    private ITimeLimitedDataProtector Protector()
    {
        return dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    }

    /// <summary>
    /// The decision of closed proposals: success when every one executed or was resolved, otherwise the
    /// failure of the first that did not.
    /// </summary>
    private static CallToolResult Outcome(IReadOnlyList<AssistantApproval> approvals)
    {
        var failed = approvals.FirstOrDefault(approval => approval.Status is not (NhAssistantApprovalStatuses.Executed or NhAssistantApprovalStatuses.Resolved));
        if (failed is null)
        {
            var result = new CallToolResult
            {
                Content = [new TextContentBlock { Text = "The proposal was approved and executed." }],
                Meta = new JsonObject { [ResultMetaKey] = Decisions(approvals) }
            };
            return result;
        }

        return failed.Status == NhAssistantApprovalStatuses.Rejected
            ? Failure(NhAssistantErrorCodes.McpProposalRejected, "A person rejected the proposal.", approvals)
            : Failure(NhAssistantErrorCodes.McpProposalClosed, "The proposal closed without being executed.", approvals);
    }

    private static CallToolResult Failure(string code, string message, IReadOnlyList<AssistantApproval> approvals)
    {
        var result = NhAiMcpResultMetadata.CreateDatalessFailure(code, message, null, null);
        result.Meta ??= new JsonObject();
        result.Meta[ResultMetaKey] = Decisions(approvals);
        return result;
    }

    private static JsonArray Decisions(IReadOnlyList<AssistantApproval> approvals)
    {
        return new JsonArray(approvals
            .Select(approval => (JsonNode)new JsonObject
            {
                ["approvalId"] = approval.Id.ToString("D"),
                ["status"] = approval.Status,
                ["executionResultCode"] = approval.ExecutionResultCode
            })
            .ToArray());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An MCP retry of tool {ToolId} carried an approval state that does not belong to its call.")]
    private partial void LogStateRefused(string toolId);

    /// <summary>
    /// What a request state names: the caller, the tool and the proposals, never their content.
    /// </summary>
    private sealed record ApprovalState(string ActorId, string? TenantId, string Tool, IReadOnlyList<Guid> ApprovalIds, int Round);
}
