using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services.Api;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// Marker used to share one configured client between related endpoint services.
/// </summary>
public sealed class SampleProjectManagementApi;

public interface ISampleProjectManagementApiService : IBaseNhApiService
{
    Task<TaskResult<SampleApplicationInfoModel>> GetApplicationInfoAsync(
        CancellationToken cancellationToken = default);
}

public sealed class SampleProjectManagementApiService
    : BaseNhApiService<SampleProjectManagementApi>, ISampleProjectManagementApiService
{
    public SampleProjectManagementApiService(
        ILogger<SampleProjectManagementApiService> logger,
        INhApiHttpClientFactory<SampleProjectManagementApi> httpClientFactory)
        : base(logger, httpClientFactory)
    {
    }

    public Task<TaskResult<SampleApplicationInfoModel>> GetApplicationInfoAsync(
        CancellationToken cancellationToken = default)
    {
        return DoGetAsync<SampleApplicationInfoModel>("/", cancellationToken);
    }
}

public sealed class SampleApplicationInfoModel
{
    public string Application { get; set; } = string.Empty;

    public string Scalar { get; set; } = string.Empty;
}
