namespace NewHeap.Platform.Common.Utilities;

/// <summary>
/// Resolves the Hangfire queue used by NewHeap producers and workers.
/// </summary>
public interface INhHangfireQueueNameResolver
{
    string GetQueueName(string? queue = null);
}

/// <summary>
/// Default queue resolver that preserves the existing <see cref="NhHangfireUtil"/>
/// development queue convention.
/// </summary>
public sealed class NhHangfireQueueNameResolver : INhHangfireQueueNameResolver
{
    public string GetQueueName(string? queue = null)
    {
        return string.IsNullOrWhiteSpace(queue)
            ? NhHangfireUtil.GetQueueName()
            : NhHangfireUtil.GetQueueName(queue);
    }
}
