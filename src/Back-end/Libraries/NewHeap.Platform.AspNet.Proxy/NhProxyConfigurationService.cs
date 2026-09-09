using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Commits managed rule changes before publishing them to the running proxy.</summary>
public sealed class NhProxyConfigurationService(INhProxyConfigurationStore store, INhProxyRuntime runtime,
    ILogger<NhProxyConfigurationService> logger) : INhProxyConfigurationService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _desiredRevision;
    private long _desiredRewriteRevision;
    private readonly SemaphoreSlim _rewriteGate = new(1, 1);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await store.InitializeAsync(cancellationToken);
        var rewrites = await RetryActivationAsync(NhProxyEngine.Rewrite, cancellationToken);
        if (!rewrites.Success)
        {
            throw new InvalidDataException("The persisted rewrite configuration could not be activated.");
        }

        var result = await RetryActivationAsync(NhProxyEngine.Redirect, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidDataException("The persisted redirect configuration could not be activated.");
        }
    }

    public Task<NhProxyRedirectConfiguration> GetRedirectsAsync(CancellationToken cancellationToken = default) => store.LoadRedirectsAsync(cancellationToken);
    public Task<NhProxyRewriteConfiguration> GetRewritesAsync(CancellationToken cancellationToken = default) => store.LoadRewritesAsync(cancellationToken);

    public async Task<TaskResult<NhProxySaveResult>> SaveRewritesAsync(NhProxyRewriteSaveRequest request, CancellationToken cancellationToken = default)
    {
        await _rewriteGate.WaitAsync(cancellationToken);
        try
        {
            var saved = await store.SaveRewritesAsync(request, cancellationToken);
            if (!saved.Success)
            {
                return TaskResult<NhProxySaveResult>.Failed(saved);
            }

            var activation = await PublishAsync(saved.Data!);
            var data = new NhProxySaveResult(saved.Data!.Revision, activation.Data ?? GetStatus().Rewrite);
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
            _rewriteGate.Release();
        }
    }

    public NhProxyStatus GetStatus()
    {
        var status = runtime.GetStatus();
        var desired = Interlocked.Read(ref _desiredRevision);
        var desiredRewrite = Interlocked.Read(ref _desiredRewriteRevision);
        var rewriteState = status.Rewrite.State;
        if (rewriteState == NhProxyActivationState.Active && status.Rewrite.ActiveRevision != desiredRewrite)
        {
            rewriteState = NhProxyActivationState.Pending;
        }

        return status with
        {
            Rewrite = status.Rewrite with { DesiredRevision = desiredRewrite, State = rewriteState },
            Redirect = status.Redirect with
            {
                DesiredRevision = desired,
                State = status.Redirect.ActiveRevision == desired ? NhProxyActivationState.Active : NhProxyActivationState.Pending
            }
        };
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
        if (!Enum.IsDefined(engine))
        {
            throw new ArgumentOutOfRangeException(nameof(engine));
        }

        var gate = engine == NhProxyEngine.Rewrite ? _rewriteGate : _gate;
        await gate.WaitAsync(cancellationToken);
        try
        {
            return engine == NhProxyEngine.Rewrite
                ? await PublishAsync(await store.LoadRewritesAsync(cancellationToken))
                : await PublishAsync(await store.LoadRedirectsAsync(cancellationToken));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TaskResult<NhProxyEngineStatus>> PublishAsync(NhProxyRewriteConfiguration snapshot)
    {
        Interlocked.Exchange(ref _desiredRewriteRevision, snapshot.Revision);
        if (runtime.GetStatus().Rewrite.ActiveRevision == snapshot.Revision)
        {
            return TaskResult<NhProxyEngineStatus>.Succeeded(GetStatus().Rewrite);
        }

        try
        {
            // Publication must finish even if the HTTP request was cancelled after the commit.
            return await runtime.PublishRewritesAsync(snapshot, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Rewrite revision {Revision} was saved but could not be activated.", snapshot.Revision);
            return TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.ActivationFailed, NhProxyErrorCodes.ActivationFailed);
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
