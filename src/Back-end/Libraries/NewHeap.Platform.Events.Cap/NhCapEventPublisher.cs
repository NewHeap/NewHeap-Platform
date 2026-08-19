using DotNetCore.CAP;
using NewHeap.Platform.Common.Events;

namespace NewHeap.Platform.Events.Cap;

public class NhCapEventPublisher : INhEventPublisher
{
    private readonly ICapPublisher _publisher;
    private readonly CapTransactionScope _scope;

    public NhCapEventPublisher(ICapPublisher publisher, CapTransactionScope scope)
    {
        _publisher = publisher;
        _scope = scope;
    }
    
    public async Task PublishAsync<TEvent>(TEvent evt) where TEvent : INhEvent
    {
        var topic = TEvent.Topic;
        _publisher.Transaction = _scope.Current;
        if (_scope.IsCommitStarted)
        {
            throw new InvalidOperationException("Cannot publish events after the transaction commit has started, please make sure to include events before committing the transaction.");
        }
        await _publisher.PublishAsync(topic, evt);
    }
}
