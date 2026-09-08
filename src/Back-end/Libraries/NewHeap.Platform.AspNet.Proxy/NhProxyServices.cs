using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>
/// Administration orchestration. Validate, commit, then publish only the changed engine.
/// After commit, publication survives request cancellation. Managed rewrite orchestration is pending.
/// </summary>
public interface INhProxyConfigurationService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<NhProxyRewriteConfiguration> GetRewritesAsync(CancellationToken cancellationToken = default);
    Task<NhProxyRedirectConfiguration> GetRedirectsAsync(CancellationToken cancellationToken = default);
    Task<TaskResult<NhProxySaveResult>> SaveRewritesAsync(NhProxyRewriteSaveRequest request, CancellationToken cancellationToken = default);
    Task<TaskResult<NhProxySaveResult>> SaveRedirectsAsync(NhProxyRedirectSaveRequest request, CancellationToken cancellationToken = default);
    NhProxyStatus GetStatus();
    Task<TaskResult<NhProxyEngineStatus>> RetryActivationAsync(NhProxyEngine engine, CancellationToken cancellationToken = default);
}

/// <summary>Storage never publishes runtime state. Compare-and-swap writes return conflicts as failed results.</summary>
public interface INhProxyConfigurationStore : IAsyncDisposable
{
    /// <summary>Create/upgrade storage and acquire ownership; corrupt or incompatible state throws.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<NhProxyRewriteConfiguration> LoadRewritesAsync(CancellationToken cancellationToken = default);
    Task<NhProxyRedirectConfiguration> LoadRedirectsAsync(CancellationToken cancellationToken = default);
    Task<TaskResult<NhProxyRewriteConfiguration>> SaveRewritesAsync(NhProxyRewriteSaveRequest request, CancellationToken cancellationToken = default);
    Task<TaskResult<NhProxyRedirectConfiguration>> SaveRedirectsAsync(NhProxyRedirectSaveRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Shared save/dry-run validation, including host policies and destination restrictions.</summary>
public interface INhProxyConfigurationValidator
{
    Task<TaskResult> ValidateRewritesAsync(NhProxyRewriteSaveRequest request, CancellationToken cancellationToken = default);
    Task<TaskResult> ValidateRedirectsAsync(NhProxyRedirectSaveRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Independent activation; implementations wrap YARP and redirect snapshots, not a custom YARP provider.</summary>
public interface INhProxyRuntime
{
    NhProxyStatus GetStatus();
    Task<TaskResult<NhProxyEngineStatus>> PublishRewritesAsync(NhProxyRewriteConfiguration configuration, CancellationToken cancellationToken = default);
    Task<TaskResult<NhProxyEngineStatus>> PublishRedirectsAsync(NhProxyRedirectConfiguration configuration, CancellationToken cancellationToken = default);
}
