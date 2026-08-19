using NewHeap.Platform.Common.Test;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public sealed class ReusableTestPackageBoundaryTest
{
    [Fact]
    public void ReusableTestPackageDoesNotContainLibrarySelfTests()
    {
        var testMethods = typeof(NhTestingContext).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods())
            .Where(method => method.GetCustomAttributes(inherit: true).Any(attribute =>
                attribute is FactAttribute || attribute is TheoryAttribute))
            .ToArray();

        Assert.Empty(testMethods);
    }
}
