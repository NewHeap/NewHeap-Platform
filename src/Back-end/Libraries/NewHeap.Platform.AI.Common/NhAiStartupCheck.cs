using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NewHeap.Platform.AI;

/// <summary>
/// A startup check of the AI registration that reads tool catalogs. It runs once per host: while
/// the hosted services start, or, in a web host, after the application pipeline is configured and
/// before the server starts.
/// </summary>
/// <remarks>
/// A catalog built from ApiExplorer, such as the API bridge, must not be read before a web host's
/// pipeline has registered its endpoints. ApiExplorer keeps the descriptions it built for the life
/// of the process and would miss every minimal API endpoint, and the application-wide endpoint data
/// source would be initialized empty and build its endpoints again for every data source added
/// afterwards. The ASP.NET integration registers an <see cref="NhAiStartupCheckDeferral"/> and
/// runs the checks from its startup filter instead.
/// </remarks>
internal abstract class NhAiStartupCheck(IServiceProvider services) : IHostedService
{
    private readonly Lock _gate = new();
    private Task? _validation;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var deferral = services.GetService<NhAiStartupCheckDeferral>();
        if (deferral is not null && deferral.AppliesTo(services))
        {
            return Task.CompletedTask;
        }

        return RunAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates once; every later call returns the outcome of the first, failure included.
    /// </summary>
    internal Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _validation ??= ValidateOnceAsync(cancellationToken);
            return _validation;
        }
    }

    private async Task ValidateOnceAsync(CancellationToken cancellationToken)
    {
        // Awaited here, so a check that throws before it returns a task fails the task instead.
        await ValidateAsync(cancellationToken);
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> for an invalid registration.</summary>
    protected abstract Task ValidateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Leaves the <see cref="NhAiStartupCheck"/>s of a host to its application pipeline when
/// <paramref name="appliesTo"/> holds for the host's services.
/// </summary>
internal sealed class NhAiStartupCheckDeferral(Func<IServiceProvider, bool> appliesTo)
{
    public bool AppliesTo(IServiceProvider services)
    {
        return appliesTo(services);
    }
}
