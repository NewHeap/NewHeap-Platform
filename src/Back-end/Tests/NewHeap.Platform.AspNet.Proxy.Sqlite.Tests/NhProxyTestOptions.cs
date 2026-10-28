namespace NewHeap.Platform.AspNet.Proxy.Sqlite.Tests;

internal static class NhProxyTestOptions
{
    /// <summary>A redirect resolution budget that only matters when a test fails.</summary>
    public const int GenerousRedirectBudgetMilliseconds = 60_000;

    /// <summary>
    /// Gives a test host, and the draft tester that shares its options, a generous redirect resolution budget.
    /// The runtime measures the 50 ms default on the wall clock across the chain preflight and the redirect, so a
    /// request descheduled under parallel load would return 503 without a Location. No host test here is about the
    /// budget; the Proxy tests cover it with a manual clock.
    /// </summary>
    public static void UseGenerousRedirectBudget(this NhProxyOptions options)
    {
        options.Limits.RedirectResolutionTimeoutMilliseconds = GenerousRedirectBudgetMilliseconds;
    }
}
