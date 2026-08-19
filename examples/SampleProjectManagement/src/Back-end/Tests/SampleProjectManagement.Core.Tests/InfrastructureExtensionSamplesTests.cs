using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Events.Cap;
using SampleProjectManagement.Api.Events;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-208–209: infrastructure extension points are registered by composition
/// root code; application code should configure them, never instantiate their
/// internal runtime collaborators directly.
/// </summary>
public sealed class InfrastructureExtensionSamplesTests
{
    [Fact]
    public void ProjectConsumerUsesOneStableApplicationDeliveryGroup()
    {
        var attribute = Assert.Single(typeof(ProjectEventConsumer)
            .GetCustomAttributesData(),
            item => item.AttributeType == typeof(NhMessageProcessingAttribute));

        Assert.Equal(
            (int)MessageProcessingType.PerApplication,
            Assert.IsType<int>(attribute.ConstructorArguments.Single().Value));
    }

    [Fact]
    public void StartupConfigurationRunsAppWideInitializationOutsideControllers()
    {
        var state = new SampleStartupState();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(state)
            .BuildServiceProvider();
        var app = new ApplicationBuilder(serviceProvider);

        new SampleStartupConfiguration().Configure(app, serviceProvider);

        Assert.NotNull(state.ConfiguredAtUtc);
    }
}
