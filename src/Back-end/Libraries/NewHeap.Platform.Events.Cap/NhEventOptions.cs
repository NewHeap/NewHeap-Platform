namespace NewHeap.Platform.Events.Cap;

public class NhEventOptions
{
    public MessageProcessingType DefaultProcessingType { get; set; } = MessageProcessingType.PerInstance;
}