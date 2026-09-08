using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Commits redirect changes before publishing them to the running proxy.</summary>
public sealed class NhProxyConfigurationService(INhProxyConfigurationStore store, INhProxyRuntime runtime,
    ILogger<NhProxyConfigurationService> logger) : INhProxyConfigurationService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _desiredRevision;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await store.InitializeAsync(cancellationToken);
        var result = await RetryActivationAsync(NhProxyEngine.Redirect, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidDataException("The persisted redirect configuration could not be activated.");
        }
    }

    public Task<NhProxyRedirectConfiguration> GetRedirectsAsync(CancellationToken cancellationToken = default) => store.LoadRedirectsAsync(cancellationToken);
    public Task<NhProxyRewriteConfiguration> GetRewritesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<TaskResult<NhProxySaveResult>> SaveRewritesAsync(NhProxyRewriteSaveRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public NhProxyStatus GetStatus()
    {
        var status = runtime.GetStatus();
        var desired = Interlocked.Read(ref _desiredRevision);
        return status with { Redirect = status.Redirect with
        {
            DesiredRevision = desired,
            State = status.Redirect.ActiveRevision == desired ? NhProxyActivationState.Active : NhProxyActivationState.Pending
        } };
    }

    public async Task<TaskResult<NhProxySaveResult>> SaveRedirectsAsync(NhProxyRedirectSaveRequest request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var saved = await store.SaveRedirectsAsync(request, cancellationToken);
            if (!saved.Success)
            {
                return TaskResult<NhProxySaveResult>.Failed(saved);
            }

            var activation = await PublishAsync(saved.Data!);
            var data = new NhProxySaveResult(saved.Data!.Revision, activation.Data ?? GetStatus().Redirect);
            if (!activation.Success)
            {
                var failure = TaskResult<NhProxySaveResult>.Failed(activation);
                failure.Data = data;
                return failure;
            }

            return TaskResult<NhProxySaveResult>.Succeeded(data);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TaskResult<NhProxyEngineStatus>> RetryActivationAsync(NhProxyEngine engine, CancellationToken cancellationToken = default)
    {
        if (engine != NhProxyEngine.Redirect)
        {
            throw new NotImplementedException();
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await PublishAsync(await store.LoadRedirectsAsync(cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TaskResult<NhProxyEngineStatus>> PublishAsync(NhProxyRedirectConfiguration snapshot)
    {
        Interlocked.Exchange(ref _desiredRevision, snapshot.Revision);
        if (runtime.GetStatus().Redirect.ActiveRevision == snapshot.Revision)
        {
            return TaskResult<NhProxyEngineStatus>.Succeeded(GetStatus().Redirect);
        }

        try
        {
            // Publication must finish even if the HTTP request was cancelled after the commit.
            return await runtime.PublishRedirectsAsync(snapshot, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Redirect revision {Revision} was saved but could not be activated.", snapshot.Revision);
            return TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.ActivationFailed, NhProxyErrorCodes.ActivationFailed);
        }
    }
}
