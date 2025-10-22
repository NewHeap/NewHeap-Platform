using System.Text.Json;

namespace NewHeap.Platform.Common.Events;

public interface INhEventConsumerInternal
{
    
}

public interface INhCustomTopicEventConsumer : INhEventConsumerInternal
{
    Task HandleAsync(JsonElement @event, CancellationToken cancellationToken);
    
    static abstract string Topic { get; }
}


public interface INhEventConsumer<in TEvent> : INhEventConsumerInternal
where TEvent : INhEvent
{
    public Task HandleAsync(TEvent @event, CancellationToken cancellationToken);
}


