namespace NewHeap.Platform.Common.Events;

public interface INhEventPublisher
{
    Task PublishAsync<TEvent>(TEvent evt) 
        where TEvent : INhEvent;
}