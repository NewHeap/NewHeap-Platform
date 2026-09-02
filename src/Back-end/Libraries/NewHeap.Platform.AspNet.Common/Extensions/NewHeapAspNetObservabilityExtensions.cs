using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace NewHeap.Platform.AspNet.Common;

public static class NewHeapAspNetObservabilityExtensions
{
    public static TBuilder AddNewHeapAspNetObservability<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddNewHeapObservability();
        builder.Services.AddNewHeapAspNetObservabilityCore();
        return builder;
    }

    public static IHostBuilder AddNewHeapAspNetObservability(this IHostBuilder builder)
    {
        builder.AddNewHeapObservability();
        builder.ConfigureServices((_, services) => services.AddNewHeapAspNetObservabilityCore());
        return builder;
    }

    private static void AddNewHeapAspNetObservabilityCore(this IServiceCollection services)
    {
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(NewHeapAspNetObservabilityRegistrationMarker)))
        {
            return;
        }

        services.AddSingleton<NewHeapAspNetObservabilityRegistrationMarker>();
        services
            .AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddMeter(NhBackgroundOperationMetrics.MeterName))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation());
    }

    private sealed class NewHeapAspNetObservabilityRegistrationMarker;
}
