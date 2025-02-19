namespace NewHeap.Platform.Common.Utilities;

public class SemaphoreLocker
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task LockAsync(Func<Task> worker)
    {
        await _semaphore.WaitAsync();
        try
        {
            await worker();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

public class SemaphoreSlimAsync
{
    private readonly SemaphoreSlim _semaphore;

    public SemaphoreSlimAsync(int initialCount)
    {
        _semaphore = new SemaphoreSlim(initialCount);
    }

    public SemaphoreSlimAsync(int initialCount, int maxCount)
    {
        _semaphore = new SemaphoreSlim(initialCount, maxCount);
    }

    public async Task LockAsync(Func<Task> worker)
    {
        await _semaphore.WaitAsync();
        try
        {
            await worker();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}