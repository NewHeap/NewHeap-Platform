using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NewHeapAspNetObservabilityExtensionsTests
{
    [Fact]
    public void AddNewHeapAspNetObservability_IsIdempotent()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.AddNewHeapAspNetObservability();
        builder.AddNewHeapAspNetObservability();

        builder.Services.Count(descriptor =>
                descriptor.ServiceType.Name == "NewHeapAspNetObservabilityRegistrationMarker")
            .Should().Be(1);
        builder.Services.Count(descriptor =>
                descriptor.ServiceType.Name == "NewHeapObservabilityRegistrationMarker")
            .Should().Be(1);
    }
}
