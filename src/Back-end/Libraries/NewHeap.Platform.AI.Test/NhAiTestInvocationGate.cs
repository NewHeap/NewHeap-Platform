using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Test;

public sealed class NhAiTestInvocationGate : INhAiToolInvocationGate
{
    private readonly Func<NhAiToolDescriptor, CancellationToken, ValueTask<TaskResult<NhAiInvocationContext>>> _authorize;

    public NhAiTestInvocationGate(
        Func<NhAiToolDescriptor, CancellationToken, ValueTask<TaskResult<NhAiInvocationContext>>> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        _authorize = authorize;
    }

    public ValueTask<TaskResult<NhAiInvocationContext>> AuthorizeAsync(
        NhAiToolDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        return _authorize(descriptor, cancellationToken);
    }

    public static NhAiTestInvocationGate Authorized(NhAiInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new NhAiTestInvocationGate(
            (_, _) => ValueTask.FromResult(TaskResult<NhAiInvocationContext>.Succeeded(context)));
    }

    public static NhAiTestInvocationGate Denied(string error)
    {
        return new NhAiTestInvocationGate(
            (_, _) => ValueTask.FromResult(TaskResult<NhAiInvocationContext>.Failed(error)));
    }
}
