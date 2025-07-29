using System.Text.Json;

namespace NewHeap.Platform.Common.Events;

public interface INhEventConsumerInternal
{
    
}

public interface INhCustomTopicEventConsumer : INhEventConsumerInternal
{
    Task HandleAsync(JsonElement @event);
    
    static abstract string Topic { get; }
}


public interface INhEventConsumer<in TEvent> : INhEventConsumerInternal
where TEvent : INhEvent
{
    public Task HandleAsync(TEvent @event);
}

public class NoEvent : INhEvent
{
    public static string Topic => throw new InvalidOperationException();
}