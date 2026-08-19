using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Utilities;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-209: startup work that belongs after NewHeap has configured its
/// defaults. Do not use this extension point to append request middleware:
/// the platform pipeline has already mapped its endpoints at this stage.
/// </summary>
public sealed class SampleStartupConfiguration : IStartupConfiguration
{
    public void Configure(IApplicationBuilder app, IServiceProvider serviceProvider)
    {
        serviceProvider.GetRequiredService<SampleStartupState>().MarkConfigured();
    }
}

public sealed class SampleStartupState
{
    public DateTimeOffset? ConfiguredAtUtc { get; private set; }

    public void MarkConfigured() => ConfiguredAtUtc = DateTimeOffset.UtcNow;
}
