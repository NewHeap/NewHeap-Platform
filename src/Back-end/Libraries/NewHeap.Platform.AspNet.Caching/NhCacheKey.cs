namespace NewHeap.Platform.AspNet.Caching;

public static class NhCacheKey
{
    public static string Create(params IEnumerable<Span<char>> keyParts)
    {
        return string.Join('_', keyParts);
    }
}