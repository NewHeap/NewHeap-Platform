namespace NewHeap.Media;

public class NhMediaContext
{
    public Dictionary<string, object> Values { get; } = new();

    public T Get<T>(string key)
    {
        if (!Values.TryGetValue(key, out var value))
        {
            return default!;
        }
        return (T)value;
    }
}