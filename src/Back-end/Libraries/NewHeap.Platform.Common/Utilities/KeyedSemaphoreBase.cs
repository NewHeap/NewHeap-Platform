using KeyedSemaphores;

namespace NewHeap.Platform.Common.Utilities;

public abstract partial class KeyedSemaphoreBase<T> where T : notnull
{
    private static readonly KeyedSemaphoresCollection<T> Collection = new();

    protected KeyedSemaphoreBase()
    {
        throw new InvalidOperationException();
    }


    /// <summary>
    ///     Asynchronously gets or creates a keyed semaphore with the provided key and immediately acquires a lock on it.
    /// </summary>
    /// <param name="key">The unique key of this keyed semaphore</param>
    /// <param name="cancellationToken">A cancellation token that will interrupt trying to acquire the lock</param>
    /// <returns>
    ///     An instance of <see cref="IKeyedSemaphore{TKey}" /> that has already acquired a lock on the inner
    ///     <see cref="SemaphoreSlim" />
    /// </returns>
    public static ValueTask<IDisposable> LockAsync(T key, CancellationToken cancellationToken = default)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        return Collection.LockAsync(key, cancellationToken);
    }

    /// <summary>
    ///     Synchronously gets or creates a keyed semaphore with the provided key and immediately acquires a lock on it.
    /// </summary>
    /// <remarks>
    ///     This method will block the current thread until the keyed semaphore lock is acquired.
    ///     If possible, consider using the asynchronous <see cref="LockAsync" /> method which does not block the thread
    /// </remarks>
    /// <param name="key">The unique key of this keyed semaphore</param>
    /// <param name="cancellationToken">A cancellation token that will interrupt trying to acquire the lock</param>
    /// <returns>
    ///     An instance of <see cref="IKeyedSemaphore{TKey}" /> that has already acquired a lock on the inner
    ///     <see cref="SemaphoreSlim" />
    /// </returns>
    public static IDisposable Lock(T key, CancellationToken cancellationToken = default)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        return Collection.Lock(key, cancellationToken);
    }
}