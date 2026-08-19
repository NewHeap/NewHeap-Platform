namespace NewHeap.Platform.AspNet.Caching;

public static class NhCacheKey
{
    public static string Create(params string[] keyParts)
    {
        return string.Join('_', keyParts);
    }
}