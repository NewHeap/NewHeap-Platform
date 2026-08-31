using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

[Collection(NhConfigurationEnvironmentCollection.CollectionName)]
public sealed class NewHeapObservabilityExtensionsTests
{
    private const string LegacyDeploymentEnvironmentAttributeName = "deployment.environment";
    private const string DeploymentEnvironmentNameAttributeName = "deployment.environment.name";

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

    [Fact]
    public void AddNewHeapObservability_AddsDeploymentEnvironmentAttributesToAllSignals()
    {
        var capturedResources = new ConcurrentBag<IReadOnlyDictionary<string, object>>();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Staging
        });

        builder.AddNewHeapObservability(options =>
        {
            options.OtlpExporterMode = NewHeapOtlpExporterMode.Disabled;
            options.ConfigureResource = resource => CaptureResource(resource, capturedResources);
        });

        using var host = builder.Build();
        StartObservabilityProviders(host);

        Assert.Equal(3, capturedResources.Count);
        Assert.All(capturedResources, resource =>
        {
            Assert.Equal(
                Environments.Staging,
                resource[LegacyDeploymentEnvironmentAttributeName]);
            Assert.Equal(
                "staging",
                resource[DeploymentEnvironmentNameAttributeName]);
        });
    }

    [Fact]
    public void AddNewHeapObservability_LegacyHostBuilderReceivesDeploymentEnvironmentAttributes()
    {
        var capturedResources = new ConcurrentBag<IReadOnlyDictionary<string, object>>();
        using var host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Staging)
            .AddNewHeapObservability(options =>
            {
                options.OtlpExporterMode = NewHeapOtlpExporterMode.Disabled;
                options.ConfigureResource = resource => CaptureResource(resource, capturedResources);
            })
            .Build();

        StartObservabilityProviders(host);

        Assert.Equal(3, capturedResources.Count);
        Assert.All(capturedResources, resource =>
        {
            Assert.Equal(
                Environments.Staging,
                resource[LegacyDeploymentEnvironmentAttributeName]);
            Assert.Equal(
                "staging",
                resource[DeploymentEnvironmentNameAttributeName]);
        });
    }

    [Fact]
    public void AddNewHeapObservability_PreservesExistingDeploymentEnvironmentAttributes()
    {
        var capturedResources = new ConcurrentBag<IReadOnlyDictionary<string, object>>();
        var builder = CreateStagingApplicationBuilder();
        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes(
            [
                new KeyValuePair<string, object>(
                    LegacyDeploymentEnvironmentAttributeName,
                    "ExistingLegacy"),
                new KeyValuePair<string, object>(
                    DeploymentEnvironmentNameAttributeName,
                    "existing-standard")
            ]));

        AddObservabilityAndCapture(builder, capturedResources);

        using var host = builder.Build();
        StartObservabilityProviders(host);

        Assert.Equal(3, capturedResources.Count);
        Assert.All(capturedResources, resource =>
        {
            Assert.Equal("ExistingLegacy", resource[LegacyDeploymentEnvironmentAttributeName]);
            Assert.Equal("existing-standard", resource[DeploymentEnvironmentNameAttributeName]);
        });
    }

    [Theory]
    [InlineData(LegacyDeploymentEnvironmentAttributeName, "ExistingLegacy")]
    [InlineData(DeploymentEnvironmentNameAttributeName, "existing-standard")]
    public void AddNewHeapObservability_AddsOnlyTheMissingDeploymentEnvironmentAttribute(
        string existingAttributeName,
        string existingAttributeValue)
    {
        var capturedResources = new ConcurrentBag<IReadOnlyDictionary<string, object>>();
        var builder = CreateStagingApplicationBuilder();
        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes(
            [
                new KeyValuePair<string, object>(existingAttributeName, existingAttributeValue)
            ]));

        AddObservabilityAndCapture(builder, capturedResources);

        using var host = builder.Build();
        StartObservabilityProviders(host);

        Assert.Equal(3, capturedResources.Count);
        Assert.All(capturedResources, resource =>
        {
            Assert.Equal(
                existingAttributeName == LegacyDeploymentEnvironmentAttributeName
                    ? existingAttributeValue
                    : Environments.Staging,
                resource[LegacyDeploymentEnvironmentAttributeName]);
            Assert.Equal(
                existingAttributeName == DeploymentEnvironmentNameAttributeName
                    ? existingAttributeValue
                    : "staging",
                resource[DeploymentEnvironmentNameAttributeName]);
        });
    }

    [Fact]
    public void AddNewHeapObservability_PreservesDeploymentEnvironmentFromOtelResourceAttributes()
    {
        using var environmentVariable = new EnvironmentVariableScope(
            "OTEL_RESOURCE_ATTRIBUTES",
            $"{DeploymentEnvironmentNameAttributeName}=from-environment");
        var capturedResources = new ConcurrentBag<IReadOnlyDictionary<string, object>>();
        var builder = CreateStagingApplicationBuilder();

        AddObservabilityAndCapture(builder, capturedResources);

        using var host = builder.Build();
        StartObservabilityProviders(host);

        Assert.Equal(3, capturedResources.Count);
        Assert.All(capturedResources, resource =>
        {
            Assert.Equal(
                Environments.Staging,
                resource[LegacyDeploymentEnvironmentAttributeName]);
            Assert.Equal(
                "from-environment",
                resource[DeploymentEnvironmentNameAttributeName]);
        });
    }

    [Fact]
    public void AddNewHeapObservability_ConfigureResourceCanOverrideDeploymentEnvironmentDefaults()
    {
        var capturedResources = new ConcurrentBag<IReadOnlyDictionary<string, object>>();
        var builder = CreateStagingApplicationBuilder();

        builder.AddNewHeapObservability(options =>
        {
            options.OtlpExporterMode = NewHeapOtlpExporterMode.Disabled;
            options.ConfigureResource = resource =>
            {
                resource.AddAttributes(
                [
                    new KeyValuePair<string, object>(
                        LegacyDeploymentEnvironmentAttributeName,
                        "ConsumerLegacy"),
                    new KeyValuePair<string, object>(
                        DeploymentEnvironmentNameAttributeName,
                        "consumer-standard")
                ]);
                CaptureResource(resource, capturedResources);
            };
        });

        using var host = builder.Build();
        StartObservabilityProviders(host);

        Assert.Equal(3, capturedResources.Count);
        Assert.All(capturedResources, resource =>
        {
            Assert.Equal("ConsumerLegacy", resource[LegacyDeploymentEnvironmentAttributeName]);
            Assert.Equal("consumer-standard", resource[DeploymentEnvironmentNameAttributeName]);
        });
    }

    private static HostApplicationBuilder CreateStagingApplicationBuilder() =>
        Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Staging
        });

    private static void AddObservabilityAndCapture(
        HostApplicationBuilder builder,
        ConcurrentBag<IReadOnlyDictionary<string, object>> capturedResources) =>
        builder.AddNewHeapObservability(options =>
        {
            options.OtlpExporterMode = NewHeapOtlpExporterMode.Disabled;
            options.ConfigureResource = resource => CaptureResource(resource, capturedResources);
        });

    private static void CaptureResource(
        ResourceBuilder resource,
        ConcurrentBag<IReadOnlyDictionary<string, object>> capturedResources) =>
        capturedResources.Add(resource.Build().Attributes.ToDictionary());

    private static void StartObservabilityProviders(IHost host)
    {
        host.Start();
        _ = host.Services.GetRequiredService<ILoggerFactory>();
        _ = host.Services.GetRequiredService<TracerProvider>();
        _ = host.Services.GetRequiredService<MeterProvider>();
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _key;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string key, string? value)
        {
            _key = key;
            _originalValue = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_key, _originalValue);
    }
}
