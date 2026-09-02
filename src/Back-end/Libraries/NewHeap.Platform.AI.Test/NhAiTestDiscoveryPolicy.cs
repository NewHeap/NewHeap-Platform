namespace NewHeap.Platform.AI.Test;

public sealed class NhAiTestDiscoveryPolicy : INhAiToolDiscoveryPolicy
{
    private readonly Func<NhAiToolDescriptor, NhAiInvocationContext, bool> _decision;

    public NhAiTestDiscoveryPolicy(
        Func<NhAiToolDescriptor, NhAiInvocationContext, bool> decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _decision = decision;
    }

    public static NhAiTestDiscoveryPolicy Allowed()
    {
        return new NhAiTestDiscoveryPolicy((_, _) => true);
    }

    public static NhAiTestDiscoveryPolicy Denied()
    {
        return new NhAiTestDiscoveryPolicy((_, _) => false);
    }

    public ValueTask<bool> CanDiscoverAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_decision(descriptor, context));
    }
}
