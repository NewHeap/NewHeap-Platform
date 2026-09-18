using NewHeap.Platform.AI.AspNet.Mvc;

namespace NewHeap.Platform.AI.Test;

/// <summary>Reusable contract assertions for consumer API bridge integration tests.</summary>
public static class NhAiBridgeContractAssertions
{
    public static void AssertBoundedIdentifiers(
        NhAiMvcBridgeToolCatalog catalog,
        int maximumLength = 64)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        var duplicates = catalog.Descriptors
            .GroupBy(descriptor => descriptor.ExportName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        var oversized = catalog.Descriptors
            .Where(descriptor => descriptor.ExportName.Length > maximumLength)
            .Select(descriptor => descriptor.ExportName)
            .ToArray();
        var manifest = catalog.Manifest.Tools.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var mismatched = catalog.Descriptors
            .Where(descriptor => !manifest.TryGetValue(descriptor.Id, out var item)
                || !string.Equals(item.ExportName, descriptor.ExportName, StringComparison.Ordinal))
            .Select(descriptor => descriptor.Id)
            .ToArray();
        if (duplicates.Length > 0 || oversized.Length > 0 || mismatched.Length > 0)
        {
            throw new InvalidOperationException(
                $"AI bridge identifier contract failed. Duplicates: [{string.Join(", ", duplicates)}]. Oversized: [{string.Join(", ", oversized)}]. Manifest mismatches: [{string.Join(", ", mismatched)}].");
        }
    }

    public static void AssertGatewayResources(
        NhAiMvcBridgeToolCatalog catalog,
        params string[] expectedResources)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(expectedResources);
        var actual = catalog.GatewayResources.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var expected = expectedResources.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"AI bridge gateway resources differ. Expected: [{string.Join(", ", expected)}]. Actual: [{string.Join(", ", actual)}].");
        }
    }
}
