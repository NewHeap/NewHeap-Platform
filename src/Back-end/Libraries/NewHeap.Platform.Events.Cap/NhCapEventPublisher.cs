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
        await _publisher.PublishAsync(topic, evt);
    }
}