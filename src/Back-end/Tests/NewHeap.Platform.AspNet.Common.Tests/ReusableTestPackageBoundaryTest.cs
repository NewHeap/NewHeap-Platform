using NewHeap.Platform.AspNet.Common.Test;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class ReusableTestPackageBoundaryTest
{
    [Fact]
    public void ReusableTestPackageDoesNotContainLibrarySelfTests()
    {
        var testMethods = typeof(NhDbContextTestingContext<>).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods())
            .Where(method => method.GetCustomAttributes(inherit: true).Any(attribute =>
                attribute is FactAttribute || attribute is TheoryAttribute))
            .ToArray();

        Assert.Empty(testMethods);
    }
}
