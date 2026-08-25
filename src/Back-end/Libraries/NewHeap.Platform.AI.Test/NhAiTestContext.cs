namespace NewHeap.Platform.AI.Test;

public sealed class NhAiTestContextSource : INhAiContextSource
{
    private readonly Func<NhAiContextRequest, CancellationToken, ValueTask<IReadOnlyList<NhAiContextItem>>> _retrieve;

    public NhAiTestContextSource(
        NhAiContextSourceDescriptor descriptor,
        Func<NhAiContextRequest, CancellationToken, ValueTask<IReadOnlyList<NhAiContextItem>>> retrieve)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(retrieve);
        Descriptor = descriptor;
        _retrieve = retrieve;
    }

    public NhAiContextSourceDescriptor Descriptor { get; }
    public int Calls { get; private set; }

    public async ValueTask<IReadOnlyList<NhAiContextItem>> RetrieveAsync(
        NhAiContextRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return await _retrieve(request, cancellationToken);
    }

    public static NhAiTestContextSource FromItems(
        string sourceId,
        params NhAiContextItem[] items)
    {
        return new NhAiTestContextSource(
            new NhAiContextSourceDescriptor(
                sourceId,
                "Deterministic test context source.",
                NhAiDataClassification.Restricted,
                Math.Max(items.Length, 1),
                64 * 1024),
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult<IReadOnlyList<NhAiContextItem>>(items);
            });
    }
}

public sealed class NhAiTestContextAuthorizationPolicy(
    Func<NhAiContextSourceDescriptor, NhAiInvocationContext, bool> authorize)
    : INhAiContextAuthorizationPolicy
{
    public ValueTask<bool> CanAccessSourceAsync(
        NhAiContextSourceDescriptor source,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(authorize(source, context));
    }

    public static NhAiTestContextAuthorizationPolicy Allowed()
    {
        return new NhAiTestContextAuthorizationPolicy((_, _) => true);
    }

    public static NhAiTestContextAuthorizationPolicy Denied()
    {
        return new NhAiTestContextAuthorizationPolicy((_, _) => false);
    }
}
