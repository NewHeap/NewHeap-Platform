namespace NewHeap.Platform.Common.Events;

public interface INhEvent
{
    static abstract string Topic { get; }
}