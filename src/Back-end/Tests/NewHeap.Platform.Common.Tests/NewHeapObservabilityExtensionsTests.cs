using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models.Options;
using OpenTelemetry.Logs;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public sealed class NewHeapObservabilityExtensionsTests
{
    [Fact]
    public void AddNewHeapObservability_IsIdempotentAndConfiguresStructuredLogging()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.AddNewHeapObservability();
        builder.AddNewHeapObservability();

        builder.Services.Count(descriptor =>
                descriptor.ServiceType.Name == "NewHeapObservabilityRegistrationMarker")
            .Should().Be(1);

        using var host = builder.Build();
        var loggingOptions = host.Services
            .GetRequiredService<IOptions<OpenTelemetryLoggerOptions>>()
            .Value;

        loggingOptions.IncludeFormattedMessage.Should().BeTrue();
        loggingOptions.IncludeScopes.Should().BeTrue();
        loggingOptions.ParseStateValues.Should().BeTrue();
    }

    [Fact]
    public void AddNewHeapObservability_DoesNotEnableOtlpWithoutExplicitConfiguration()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;

        builder.AddNewHeapObservability(options =>
            options.OtlpExporterMode = NewHeapOtlpExporterMode.Auto);

        builder.Services.Should().NotContain(descriptor =>
            descriptor.ServiceType.Name == "NewHeapOtlpExporterRegistrationMarker");
    }

    [Fact]
    public void AddNewHeapObservability_CanExplicitlyEnableOtlpOnce()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.AddNewHeapObservability(options =>
            options.OtlpExporterMode = NewHeapOtlpExporterMode.Enabled);
        builder.AddNewHeapObservability(options =>
            options.OtlpExporterMode = NewHeapOtlpExporterMode.Enabled);

        builder.Services.Count(descriptor =>
                descriptor.ServiceType.Name == "NewHeapOtlpExporterRegistrationMarker")
            .Should().Be(1);
    }

    [Fact]
    public void AddNewHeapObservability_IsIdempotentForLegacyHostBuilder()
    {
        using var host = Host.CreateDefaultBuilder()
            .AddNewHeapObservability(options =>
                options.OtlpExporterMode = NewHeapOtlpExporterMode.Disabled)
            .AddNewHeapObservability(options =>
                options.OtlpExporterMode = NewHeapOtlpExporterMode.Disabled)
            .Build();

        host.Services.GetServices<ILoggerProvider>()
            .Count(provider => provider.GetType().Name == "OpenTelemetryLoggerProvider")
            .Should().Be(1);
    }
}
