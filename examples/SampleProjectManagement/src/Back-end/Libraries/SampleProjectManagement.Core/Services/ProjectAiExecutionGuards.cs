using System.Collections.Concurrent;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.AI;

namespace SampleProjectManagement.Core.Services;

public sealed class ProjectAiStatusVerifier(
    IProjectAiMutationService projectMutationService) : INhAiToolVerifier
{
    public const string VerifierId = "project-status";

    public string Id => VerifierId;

    public async ValueTask<NhAiVerificationResult> VerifyAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        object? executionResult,
        CancellationToken cancellationToken = default)
    {
        if (arguments is not ProjectAiStatusChangeInput input
            || executionResult is not ProjectAiStatusChangeReport report
            || !context.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return new NhAiVerificationResult(false, "verification-input-invalid");
        }

        var actualStatus = await projectMutationService.GetStatusForAiAsync(
            divisionId,
            input.ProjectId,
            cancellationToken);
        var succeeded = actualStatus == input.Status
            && report.ProjectId == input.ProjectId
            && report.CurrentStatus == input.Status
            && report.Accepted;
        return new NhAiVerificationResult(
            succeeded,
            succeeded ? "verified" : "project-status-mismatch",
            $"project-status:{input.ProjectId}");
    }
}

public sealed class ProjectAiInMemoryIdempotencyManager : INhAiIdempotencyManager
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

    public ValueTask<NhAiIdempotencyLease> AcquireAsync(
        NhAiIdempotencyRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = $"{request.ActorId}:{request.ToolId}:{request.ToolVersion}:{request.IdempotencyKey}";
        var candidate = new Entry(request.ArgumentHash, request.FencingToken);
        var entry = _entries.GetOrAdd(key, candidate);
        if (!ReferenceEquals(entry, candidate))
        {
            var sameArguments = string.Equals(
                entry.ArgumentHash,
                request.ArgumentHash,
                StringComparison.Ordinal);
            var sameFence = string.Equals(
                entry.FencingToken,
                request.FencingToken,
                StringComparison.Ordinal);
            return ValueTask.FromResult(new NhAiIdempotencyLease(
                sameArguments && sameFence
                    ? NhAiIdempotencyDecisionKind.Duplicate
                    : NhAiIdempotencyDecisionKind.Conflict,
                sameArguments && sameFence ? "duplicate" : "idempotency-conflict"));
        }

        var leaseId = Guid.NewGuid().ToString("N");
        _leases[leaseId] = key;
        return ValueTask.FromResult(new NhAiIdempotencyLease(
            NhAiIdempotencyDecisionKind.Acquired,
            "acquired",
            leaseId));
    }

    public ValueTask CompleteAsync(
        NhAiIdempotencyLease lease,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lease.Decision != NhAiIdempotencyDecisionKind.Acquired
            || string.IsNullOrWhiteSpace(lease.LeaseId)
            || !_leases.TryRemove(lease.LeaseId, out _))
        {
            throw new InvalidOperationException("The AI idempotency lease is invalid or already completed.");
        }
        return ValueTask.CompletedTask;
    }

    private sealed record Entry(
        string ArgumentHash,
        string? FencingToken);
}

/// <summary>
/// SPM-247: what the sample's budget manager reserved, per purpose and agent, so the application
/// can report an agent's work apart from people's chats and MCP clients.
/// </summary>
public sealed class ProjectAiBudgetUsage
{
    private readonly ConcurrentDictionary<(string Purpose, string? AgentId), int> _calls = new();
    private readonly ConcurrentDictionary<(string? AgentId, string ProfileName), int> _profiles = new();

    public void Add(NhAiBudgetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _calls.AddOrUpdate(
            (request.Purpose ?? NhAiBudgetPurposes.Application, request.AgentId),
            request.RequestedCalls,
            (_, calls) => calls + request.RequestedCalls);
        // A model call reserves tokens on the profile that serves it; a tool call reserves none.
        if (request.RequestedInputTokens > 0 || request.RequestedOutputTokens > 0)
        {
            _profiles.AddOrUpdate(
                (request.AgentId, request.ProfileName),
                request.RequestedCalls,
                (_, calls) => calls + request.RequestedCalls);
        }
    }

    /// <summary>
    /// SPM-307: the model profiles that served the model calls of <paramref name="agentId"/>, as the
    /// profile resolution chose them for the calls' data classification, ordered by name.
    /// </summary>
    public IReadOnlyList<string> ProfilesOf(string agentId)
    {
        return _profiles.Keys
            .Where(key => key.AgentId == agentId)
            .Select(key => key.ProfileName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The calls reserved for <paramref name="purpose"/>, by <paramref name="agentId"/> or by every agent.
    /// </summary>
    public int Calls(string purpose, string? agentId = null)
    {
        return _calls
            .Where(item => item.Key.Purpose == purpose && (agentId is null || item.Key.AgentId == agentId))
            .Sum(item => item.Value);
    }
}

/// <summary>
/// The sample's budget manager. It bounds every call of the application itself and of MCP clients
/// per invocation. SPM-247: the assistant reserves the calls of its turns here too
/// (<c>UseApplicationBudgetInTurns</c>); its own durable daily budget bounds people's chats and
/// agents' work, so this manager only counts those, by purpose and agent.
/// </summary>
public sealed class ProjectAiInMemoryBudgetManager(ProjectAiBudgetUsage usage) : INhAiBudgetManager
{
    private const int MaxInputTokens = 4_096;
    private const int MaxOutputTokens = 1_024;
    private const int MaxCalls = 8;
    private const decimal MaxEstimatedCost = 1m;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Usage> _usage = [];

    public ValueTask<TaskResult<NhAiBudgetReservation>> ReserveAsync(
        NhAiBudgetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Purpose is NhAiBudgetPurposes.Chat
            or NhAiBudgetPurposes.AgentRun
            or NhAiBudgetPurposes.Consultation
            or NhAiBudgetPurposes.ProposalExecution)
        {
            usage.Add(request);
            return ValueTask.FromResult(
                TaskResult<NhAiBudgetReservation>.Succeeded(
                    new NhAiBudgetReservation(
                        Guid.NewGuid().ToString("N"),
                        new NhAiModelBudget(MaxInputTokens, MaxOutputTokens, MaxCalls, MaxEstimatedCost),
                        DateTimeOffset.UtcNow.AddMinutes(5))));
        }

        lock (_sync)
        {
            _usage.TryGetValue(request.InvocationId, out var current);
            var next = new Usage(
                current.InputTokens + request.RequestedInputTokens,
                current.OutputTokens + request.RequestedOutputTokens,
                current.Calls + request.RequestedCalls,
                current.EstimatedCost + (request.RequestedEstimatedCost ?? 0m));
            if (next.InputTokens > MaxInputTokens
                || next.OutputTokens > MaxOutputTokens
                || next.Calls > MaxCalls
                || next.EstimatedCost > MaxEstimatedCost)
            {
                return ValueTask.FromResult(
                    TaskResult<NhAiBudgetReservation>.Failed(
                        "sample-ai-budget-exhausted",
                        "The sample AI execution budget is exhausted."));
            }

            _usage[request.InvocationId] = next;
            usage.Add(request);
            return ValueTask.FromResult(
                TaskResult<NhAiBudgetReservation>.Succeeded(
                    new NhAiBudgetReservation(
                        Guid.NewGuid().ToString("N"),
                        new NhAiModelBudget(
                            MaxInputTokens - next.InputTokens,
                            MaxOutputTokens - next.OutputTokens,
                            MaxCalls - next.Calls,
                            MaxEstimatedCost - next.EstimatedCost),
                        DateTimeOffset.UtcNow.AddMinutes(5))));
        }
    }

    private readonly record struct Usage(
        int InputTokens,
        int OutputTokens,
        int Calls,
        decimal EstimatedCost);
}
