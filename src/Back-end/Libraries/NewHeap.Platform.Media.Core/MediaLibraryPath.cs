namespace NewHeap.Media;

public static class MediaLibraryPath
{
    public static string Combine(params string?[] paths)
    {
        var path = NhMediaValues.DirectorySeparator;
        foreach (var p in paths)
        {
            var segment = p ?? "";
            if (segment.StartsWith(NhMediaValues.DirectorySeparator))
            {
                segment = segment[1..];
            }
            if (segment.EndsWith(NhMediaValues.DirectorySeparator))
            {
                segment = segment[..^1];
            }

            if (segment.Length <= 0)
            {
                continue;
            }

            if (!path.EndsWith(NhMediaValues.DirectorySeparator))
            {
                path += NhMediaValues.DirectorySeparator;
            }
            path += segment;
        }
        return path;
    }

    public static void Split(string input, out string? path, out string name)
    {
        var sep = input.LastIndexOf(NhMediaValues.DirectorySeparator, StringComparison.Ordinal);
        if (sep == -1)
        {
            path = NhMediaValues.DirectorySeparator;
            name = input;
        }
        
        path = input[..sep];
        if(!path.StartsWith(NhMediaValues.DirectorySeparator))
        {
            path = NhMediaValues.DirectorySeparator + path;
        }
        name = input[(sep+1)..];
    }
}