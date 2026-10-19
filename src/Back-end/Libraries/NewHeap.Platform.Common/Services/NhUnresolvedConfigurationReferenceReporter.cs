using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.Common.Services;

/// <summary>
/// Warns at startup about configuration values that still hold an unresolved <c>${…}</c> reference,
/// usually a secret that is missing from the secrets file. It names the keys, never the values.
/// Settings that must not run without their secret, such as the JWT signing key, refuse to start
/// instead.
/// </summary>
internal sealed partial class NhUnresolvedConfigurationReferenceReporter(
    IServiceProvider services,
    ILogger<NhUnresolvedConfigurationReferenceReporter> logger) : IHostedService
{
    internal const int MaxReportedKeys = 20;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IConfiguration>() is not { } configuration)
        {
            return Task.CompletedTask;
        }

        var keys = NhConfigurationSecrets.FindUnresolvedReferences(configuration);
        if (keys.Count > 0)
        {
            LogUnresolved(keys.Count, string.Join(", ", keys.Take(MaxReportedKeys)));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Count} configuration values still hold an unresolved ${{...}} reference, usually a secret missing from the secrets file: {Keys}.")]
    private partial void LogUnresolved(int count, string keys);
}
