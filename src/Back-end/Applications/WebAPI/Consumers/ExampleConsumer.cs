using NewHeap.Platform.Common.Events;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WebAPI.Consumers;

public class ExampleConsumer : INhEventConsumer<ExampleEvent>
{
    public async Task HandleAsync(ExampleEvent @event, CancellationToken cancellationToken)
    {
        Console.WriteLine(@event.Id);
        ;
    }
}

public class ExampleCustomTopicConsumer : INhCustomTopicEventConsumer
{
    public async Task HandleAsync(JsonElement @event, CancellationToken cancellationToken)
    {
        ;
    }

    public static string Topic => "example";
}

public class ExampleEvent : INhEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public static string Topic => "example-event";
}

public class ExampleCustomEvent : INhEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public static string Topic => "example";
}