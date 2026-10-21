using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace NewHeap.Platform.AI.AspNet;

/// <summary>
/// Runs the AI startup checks of a web host after its application pipeline is configured and
/// before its server starts (see <see cref="NhAiStartupCheck"/>). A host without a server keeps
/// running them as hosted services.
/// </summary>
internal static class NhAiAspNetStartupChecks
{
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Every web host has a server, and its web host service builds the pipeline through the
        // startup filters, so the checks wait for the filter instead of running before it.
        services.TryAddSingleton(new NhAiStartupCheckDeferral(provider => provider.GetService<IServer>() is not null));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, NhAiStartupCheckFilter>());
    }
}

/// <summary>
/// Runs every <see cref="NhAiStartupCheck"/> once the rest of the pipeline is configured, which
/// includes registering the application's endpoints. A failed check fails the host's start with
/// its own exception.
/// </summary>
internal sealed class NhAiStartupCheckFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return application =>
        {
            next(application);

            var checks = application.ApplicationServices
                .GetServices<IHostedService>()
                .OfType<NhAiStartupCheck>();
            foreach (var check in checks)
            {
                // Startup filters are synchronous. The web host builds the pipeline without a
                // synchronization context, so waiting for the check cannot deadlock.
                check.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        };
    }
}
