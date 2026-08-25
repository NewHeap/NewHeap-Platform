using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NewHeap.Platform.Common;

public static class NewHeapObservabilityExtensions
{
    private static readonly object HostBuilderRegistrationKey = new();

    public static TBuilder AddNewHeapObservability<TBuilder>(
        this TBuilder builder,
        Action<NewHeapObservabilityOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        var options = CreateOptions(configure);

        if (!builder.Services.HasNewHeapObservabilityRegistration())
        {
            builder.Logging.ConfigureNhCommonLogging(options);
            builder.Services.AddNewHeapObservabilityCore(
                builder.Configuration,
                builder.Environment.ApplicationName,
                options);
        }

        return builder;
    }

    public static IHostBuilder AddNewHeapObservability(
        this IHostBuilder builder,
        Action<NewHeapObservabilityOptions>? configure = null)
    {
        if (builder.Properties.ContainsKey(HostBuilderRegistrationKey))
        {
            return builder;
        }

        builder.Properties[HostBuilderRegistrationKey] = true;
        var options = CreateOptions(configure);

        builder.ConfigureLogging(logging => logging.ConfigureNhCommonLogging(options));
        builder.ConfigureServices((context, services) =>
        {
            if (!services.HasNewHeapObservabilityRegistration())
            {
                services.AddNewHeapObservabilityCore(
                    context.Configuration,
                    context.HostingEnvironment.ApplicationName,
                    options);
            }
        });

        return builder;
    }

    internal static bool HasNewHeapObservabilityRegistration(this IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(NewHeapObservabilityRegistrationMarker));

    internal static bool HasNewHeapOtlpExporterRegistration(this IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(NewHeapOtlpExporterRegistrationMarker));

    internal static void MarkNewHeapOtlpExporterRegistered(this IServiceCollection services) =>
        services.AddSingleton<NewHeapOtlpExporterRegistrationMarker>();

    private static NewHeapObservabilityOptions CreateOptions(Action<NewHeapObservabilityOptions>? configure)
    {
        var options = new NewHeapObservabilityOptions();
        configure?.Invoke(options);
        return options;
    }

    private static void AddNewHeapObservabilityCore(
        this IServiceCollection services,
        IConfiguration configuration,
        string applicationName,
        NewHeapObservabilityOptions options)
    {
        services.AddSingleton<NewHeapObservabilityRegistrationMarker>();

        var serviceName = options.ServiceName
            ?? configuration["OTEL_SERVICE_NAME"]
            ?? applicationName;
        var serviceVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

        var openTelemetry = services
            .AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource.AddService(serviceName, serviceVersion: serviceVersion);
                options.ConfigureResource?.Invoke(resource);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddRuntimeInstrumentation()
                    .AddHttpClientInstrumentation();
                options.ConfigureMetrics?.Invoke(metrics);
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(applicationName)
                    .AddHttpClientInstrumentation();
                options.ConfigureTracing?.Invoke(tracing);
            });

        if (ShouldUseOtlpExporter(configuration, options) && !services.HasNewHeapOtlpExporterRegistration())
        {
            openTelemetry.UseOtlpExporter();
            services.MarkNewHeapOtlpExporterRegistered();
        }
    }

    private static bool ShouldUseOtlpExporter(
        IConfiguration configuration,
        NewHeapObservabilityOptions options) =>
        options.OtlpExporterMode switch
        {
            NewHeapOtlpExporterMode.Enabled => true,
            NewHeapOtlpExporterMode.Disabled => false,
            _ => !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])
        };

    private sealed class NewHeapObservabilityRegistrationMarker;

    private sealed class NewHeapOtlpExporterRegistrationMarker;
}
