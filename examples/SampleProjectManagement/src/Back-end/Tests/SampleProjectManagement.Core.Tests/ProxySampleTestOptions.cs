using NewHeap.Platform.AspNet.Proxy;

namespace SampleProjectManagement.Core.Tests;

internal static class ProxySampleTestOptions
{
    /// <summary>A redirect resolution budget that only matters when a test fails.</summary>
    public const int GenerousRedirectBudgetMilliseconds = 60_000;

    /// <summary>
    /// Gives a sample test host, and the draft tester that shares its options, a generous redirect resolution
    /// budget. The runtime measures the 50 ms default on the wall clock, so a request descheduled under parallel
    /// load returns 503 without a Location, although the saved rule is already active. Only the deadline proof in
    /// <see cref="ProxyLiteralRedirectSamplesTests"/> is about the budget; a deployed host keeps the default.
    /// </summary>
    public static void UseGenerousRedirectBudget(this NhProxyOptions options)
    {
        options.Limits.RedirectResolutionTimeoutMilliseconds = GenerousRedirectBudgetMilliseconds;
    }
}
