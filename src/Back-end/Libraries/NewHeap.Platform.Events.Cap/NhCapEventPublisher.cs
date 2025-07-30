using DotNetCore.CAP;
using NewHeap.Platform.Common.Events;

namespace NewHeap.Platform.Events.Cap;

public class NhCapEventPublisher : INhEventPublisher
{
    private readonly ICapPublisher _publisher;

    public NhCapEventPublisher(ICapPublisher publisher)
    {
        _publisher = publisher;
    }
    
    public async Task PublishAsync<TEvent>(TEvent evt) where TEvent : INhEvent
    {
        var topic = TEvent.Topic;
        await _publisher.PublishAsync(topic, evt);
    }
}