using System.Collections.Concurrent;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Test;

public sealed class NhAiTestBudgetManager : INhAiBudgetManager
{
    private readonly bool _allow;
    private readonly ConcurrentQueue<NhAiBudgetRequest> _requests = new();

    public NhAiTestBudgetManager(bool allow = true)
    {
        _allow = allow;
    }

    public IReadOnlyList<NhAiBudgetRequest> Requests => _requests.ToArray();

    public ValueTask<TaskResult<NhAiBudgetReservation>> ReserveAsync(
        NhAiBudgetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(request);
        if (!_allow)
        {
            return ValueTask.FromResult(
                TaskResult<NhAiBudgetReservation>.Failed("test-budget-denied"));
        }

        return ValueTask.FromResult(
            TaskResult<NhAiBudgetReservation>.Succeeded(
                new NhAiBudgetReservation(
                    $"test-{_requests.Count}",
                    new NhAiModelBudget(int.MaxValue, int.MaxValue, int.MaxValue, null),
                    DateTimeOffset.UtcNow.AddMinutes(5))));
    }
}

public sealed class NhAiTestCapabilityResolver : INhAiCapabilityResolver
{
    public ValueTask<NhAiCapabilityResolution> ResolveAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new NhAiCapabilityResolution(
                true,
                "capabilities-granted",
                []));
    }
}
