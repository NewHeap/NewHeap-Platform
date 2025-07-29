using DotNetCore.CAP;
using DotNetCore.CAP.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Events;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.Events.Cap;

public class NhConsumerSelector : IConsumerServiceSelector
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CapOptions _capOptions;
    private static readonly ConcurrentDictionary<string, ConsumerExecutorDescriptor> _descriptors = []; 

    private readonly ConcurrentDictionary<string, List<RegexExecuteDescriptor<ConsumerExecutorDescriptor>>> _cacheList =
        [];

    public NhConsumerSelector(IServiceProvider serviceProvider, IOptions<CapOptions> capOptions)
    {
        _serviceProvider = serviceProvider;
        _capOptions = capOptions.Value;
    }

    public IReadOnlyList<ConsumerExecutorDescriptor> SelectCandidates()
    {
        using var scope = _serviceProvider.CreateScope();

        IEnumerable<ServiceDescriptor> sc = scope.ServiceProvider.GetRequiredService<IServiceCollection>();

        sc = sc.Where(o =>
            o.IsKeyedService == true && (o.KeyedImplementationType != null || o.KeyedImplementationFactory != null)
            && o.ServiceKey?.ToString() == "nh-cap" 
            )
            .ToList();
        List<ConsumerExecutorDescriptor> descriptors = [];

        foreach (var service in sc)
        {
            if (service.KeyedImplementationType?.IsAssignableTo(typeof(INhEventConsumerInternal)) != true)
            {
                continue;
            }

            var descriptor = GetDescriptor(service);
            if (descriptor == null)
            {
                continue;
            }
            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    private ConsumerExecutorDescriptor? GetDescriptor(ServiceDescriptor service)
    {
        var serviceType = service.KeyedImplementationType;
        if (serviceType == null)
        {
            return null;
        }

        if (_descriptors.ContainsKey(serviceType.FullName!))
        {
            return _descriptors[serviceType.FullName!];
        }
        
        var intf = serviceType.GetInterfaces()
            .FirstOrDefault(x => 
                (x.IsGenericType && x.GetGenericTypeDefinition() == typeof(INhEventConsumer<>))
                || x == typeof(INhCustomTopicEventConsumer)
                );
        if (intf == null)
        {
            return null;
        }

        string topic;
        Type methodParams;
        
        if (intf == typeof(INhCustomTopicEventConsumer))
        {
            topic = serviceType.GetProperty(nameof(INhCustomTopicEventConsumer.Topic), BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!.ToString()!;
            methodParams = typeof(JsonElement);
        }
        else
        {
            var field = intf.GenericTypeArguments[0]
                .GetProperty(nameof(INhEvent.Topic), BindingFlags.Public | BindingFlags.Static);
            topic = (string)field!.GetValue(null)!;
            methodParams = intf.GenericTypeArguments[0];
        }
        
        var method = serviceType.GetMethod(nameof(INhEventConsumer<NoEvent>.HandleAsync),
            BindingFlags.Public | BindingFlags.Instance);

        var descriptor = new ConsumerExecutorDescriptor()
        {
            Attribute = new CapSubscribeAttribute(topic)
            {
                Group = topic
            },
            Parameters =
                [
                    new ParameterDescriptor() { IsFromCap = false, ParameterType = methodParams, Name = "event" },
                    new ParameterDescriptor() { IsFromCap = true, ParameterType = typeof(CancellationToken), Name = "cancellationToken" },
                ],
            TopicNamePrefix = _capOptions.TopicNamePrefix,
            ClassAttribute = null,
            ServiceTypeInfo = serviceType.GetTypeInfo(),
            ImplTypeInfo = serviceType.GetTypeInfo(),
            
            MethodInfo = method!,
        };
        _descriptors.TryAdd(serviceType.FullName!, descriptor);
        return descriptor;
    }

    public ConsumerExecutorDescriptor? SelectBestCandidate(string key,
        IReadOnlyList<ConsumerExecutorDescriptor> executeDescriptor)
    {
        if (executeDescriptor.Count == 0)
        {
            return null;
        }

        var result = MatchUsingName(key, executeDescriptor);
        if (result != null)
        {
            return result;
        }

        return MatchWildcardUsingRegex(key, executeDescriptor);
    }

    private static ConsumerExecutorDescriptor? MatchUsingName(string key,
        IReadOnlyList<ConsumerExecutorDescriptor> executeDescriptor)
    {
        ArgumentNullException.ThrowIfNull(key);

        return executeDescriptor.FirstOrDefault(x =>
            x.TopicName.Equals(key, StringComparison.InvariantCultureIgnoreCase));
    }

    private ConsumerExecutorDescriptor? MatchWildcardUsingRegex(string key,
        IReadOnlyList<ConsumerExecutorDescriptor> executeDescriptor)
    {
        var group = executeDescriptor[0].Attribute.Group;
        if (!_cacheList.TryGetValue(group, out var tmpList))
        {
            tmpList = executeDescriptor.Select(x => new RegexExecuteDescriptor<ConsumerExecutorDescriptor>
            {
                Name = Helper.WildcardToRegex(x.TopicName), Descriptor = x
            }).ToList();
            _cacheList.TryAdd(group, tmpList);
        }

        foreach (var red in tmpList)
            if (Regex.IsMatch(key, red.Name, RegexOptions.Singleline))
                return red.Descriptor;

        return null;
    }

    private class RegexExecuteDescriptor<T>
    {
        public string Name { get; set; } = default!;

        public T Descriptor { get; set; } = default!;
    }
}