
namespace NewHeap.Platform.Common;

/// <summary>
/// Extensions for dealing with <see cref="Dictionary{TKey,TValue}"/>
/// </summary>
public static partial class DictionaryExtensions
{
    public static TValue GetOrAddNew<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, TValue defaultValue = default!)
        where TKey : notnull
        where TValue : new()
        => dict.GetOrAdd(key, (_, _) => EqualityComparer<TValue>.Default.Equals(default, defaultValue) ? new() : defaultValue);

    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, TValue defaultValue = default!)
        where TKey : notnull
        => dict.GetOrAdd(key, (_, _) => defaultValue);

    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, Func<TValue> valueProvider)
        where TKey : notnull
        => dict.GetOrAdd(key, (_, _) => valueProvider());

    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, Func<TKey, TValue> valueProvider)
        where TKey : notnull
        => dict.GetOrAdd(key, (_, k) => valueProvider(k));

    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, Func<IDictionary<TKey, TValue>, TKey, TValue> valueProvider)
        where TKey : notnull
    {
        if (dict == null) throw new ArgumentNullException(nameof(dict));
        if (valueProvider == null) throw new ArgumentNullException(nameof(valueProvider));

        if (dict.TryGetValue(key, out var foundValue))
            return foundValue;

        dict[key] = valueProvider(dict, key);
        return dict[key];
    }

    #region Async

    public static ValueTask<TValue> GetOrAddAsync<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, Func<CancellationToken, Task<TValue>> valueProvider, CancellationToken cancellationToken = default)
        where TKey : notnull
        => dict.GetOrAddAsync(key, (_, _, ctx) => valueProvider(ctx), cancellationToken);

    public static ValueTask<TValue> GetOrAddAsync<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, Func<TKey, CancellationToken, Task<TValue>> valueProvider, CancellationToken cancellationToken = default)
        where TKey : notnull
        => dict.GetOrAddAsync(key, (_, k, ctx) => valueProvider(k, ctx), cancellationToken);

    public static async ValueTask<TValue> GetOrAddAsync<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, Func<IDictionary<TKey, TValue>, TKey, CancellationToken, Task<TValue>> valueProvider, CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        if (dict == null) throw new ArgumentNullException(nameof(dict));
        if (valueProvider == null) throw new ArgumentNullException(nameof(valueProvider));

        if (dict.TryGetValue(key, out var foundValue))
            return foundValue;

        dict[key] = await valueProvider(dict, key, cancellationToken);
        return dict[key];
    }

    #endregion
}
