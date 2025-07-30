using DotNetCore.CAP;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.Common.Events;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebAPI.Consumers;

public class ExampleProducer : BackgroundService
{
    private readonly INhEventPublisher _publisher;
    private readonly ICapPublisher _capPublisher;

    public ExampleProducer(INhEventPublisher publisher, ICapPublisher capPublisher)
    {
        _publisher = publisher;
        _capPublisher = capPublisher;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var ev = new ExampleEvent();
                    await _publisher.PublishAsync(ev);
                    await _publisher.PublishAsync(new ExampleCustomEvent());
                }
                catch(NullReferenceException)
                {
                    // Publisher hasn't finished initializing yet
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (Exception e)
        {
            ;
        }
        
    }
}

public class Example
{
    public string Message { get; set; } = "Hello world";
}