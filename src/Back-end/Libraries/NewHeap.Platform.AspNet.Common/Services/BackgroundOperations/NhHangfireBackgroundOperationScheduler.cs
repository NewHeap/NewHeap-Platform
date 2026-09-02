using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed class NhHangfireBackgroundOperationScheduler : INhBackgroundOperationScheduler
{
    private readonly IBackgroundJobClient _client;
    private readonly JobStorage _storage;

    public NhHangfireBackgroundOperationScheduler(IBackgroundJobClient client, JobStorage storage)
    {
        _client = client;
        _storage = storage;
    }

    public Task<NhBackgroundOperationScheduleResult> EnqueueAsync(
        Guid operationId,
        int dispatchGeneration,
        string queue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = Job.FromExpression<NhBackgroundOperationRunner>(
            runner => runner.RunAsync(operationId, dispatchGeneration));
        var jobId = _client.Create(job, new EnqueuedState(queue));
        return Task.FromResult(new NhBackgroundOperationScheduleResult(jobId));
    }

    public Task<bool> DeleteAsync(string schedulerJobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_client.ChangeState(schedulerJobId, new DeletedState(), null));
    }

    public Task<NhBackgroundOperationExecutionState?> GetStateAsync(
        string schedulerJobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _storage.GetConnection();
        var state = connection.GetStateData(schedulerJobId);
        return Task.FromResult(state is null
            ? null
            : new NhBackgroundOperationExecutionState(
                state.Name,
                string.Equals(state.Name, SucceededState.StateName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.Name, FailedState.StateName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.Name, DeletedState.StateName, StringComparison.OrdinalIgnoreCase)));
    }
}
