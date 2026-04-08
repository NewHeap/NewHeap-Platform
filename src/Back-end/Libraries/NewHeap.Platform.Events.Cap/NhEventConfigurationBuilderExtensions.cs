using DotNetCore.CAP;
using DotNetCore.CAP.Internal;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Builders;
using NewHeap.Platform.Common.Events;

namespace NewHeap.Platform.Events.Cap;

public static class NhEventConfigurationBuilderExtensions
{
    public static NhEventConfigurationBuilder AddCap(this NhEventConfigurationBuilder builder,
        Action<NhCapEventBuilder> configure)
    {
        var capBuilder = new NhCapEventBuilder(builder.ServiceCollection);
        configure(capBuilder);
        return builder;
    }
}

public class NhCapEventBuilder
{
    private readonly IServiceCollection _serviceCollection;

    public NhCapEventBuilder AddSubscriber<TSubscriber, TEvent>()
        where TSubscriber : INhEventConsumer<TEvent>
        where TEvent : INhEvent
    {
        _serviceCollection.AddKeyedTransient(typeof(INhEventConsumerInternal),"nh-cap", typeof(TSubscriber));
        return this;
    }

    public NhCapEventBuilder AddCustomTopicSubscriber<TSubscriber>()
    where TSubscriber : INhCustomTopicEventConsumer
    {
        _serviceCollection.AddKeyedTransient(typeof(INhEventConsumerInternal),"nh-cap", typeof(TSubscriber));
        return this;
    }

    public NhCapEventBuilder WithPublishing()
    {
        _serviceCollection.AddTransient<INhEventPublisher, NhCapEventPublisher>();
        return this;
    }

    public NhCapEventBuilder WithOptions(Action<CapOptions> configure)
    {
        _serviceCollection.AddCap(configure);
        return this;
    }

    internal NhCapEventBuilder(IServiceCollection serviceCollection)
    {
        _serviceCollection = serviceCollection;
        _serviceCollection.AddSingleton<IConsumerServiceSelector, NhConsumerSelector>();
        _serviceCollection.AddOptions<NhEventOptions>();
    }
}