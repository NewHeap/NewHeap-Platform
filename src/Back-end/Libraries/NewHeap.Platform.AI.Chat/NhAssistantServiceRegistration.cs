using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AgentFramework;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Provider-neutral service registration shared by <c>AddNewHeapAssistant</c>.
/// </summary>
internal static class NhAssistantServiceRegistration
{
    /// <summary>
    /// Returns the registration state, registering the core services the first time. Subsequent
    /// calls return the same state so repeated registration is idempotent.
    /// </summary>
    public static NhAssistantRegistrationState EnsureRegistered(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var state = services
            .Where(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<NhAssistantRegistrationState>()
            .SingleOrDefault();
        if (state is not null)
        {
            return state;
        }

        state = new NhAssistantRegistrationState();
        services.AddSingleton(state);

        // Make sure the Platform defaults exist, then keep the managers the application registered
        // so requests outside assistant turns keep their existing behavior.
        services.AddNewHeapPlatformAI();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(INhAiAgentFrameworkAdapter)))
        {
            services.AddNewHeapPlatformAIAgentFramework();
        }
        PreserveAsFallback<INhAiBudgetManager>(services);
        PreserveAsFallback<INhAiApprovalEvidenceProvider>(services);
        services.AddNewHeapPlatformAI(ai => ai
            .UseBudgetManager<NhAssistantBudgetManager>()
            .UseIdempotencyManager<NhAssistantIdempotencyManager>()
            .UseApprovalEvidenceProvider<NhAssistantApprovalEvidenceProvider>()
            .AddAuditSink<NhAssistantAuditRelay>());
        DecorateInvocationGate(services);

        services.TryAddScoped<INhAssistantStore, NhAssistantStore>();
        services.TryAddSingleton<NhAssistantAgentRegistry>();
        services.TryAddSingleton<NhAssistantSeeder>();
        services.TryAddScoped<NhAssistantAdminStore>();
        services.TryAddScoped<NhAssistantAgentCatalog>();
        services.TryAddScoped<NhAssistantAgentAdministration>();
        services.TryAddScoped<NhAssistantPersonalization>();
        services.TryAddScoped<INhAssistantMcpToolSource, NhAssistantNoMcpToolSource>();
        services.TryAddSingleton<NhAssistantTurnCancellationRegistry>();
        services.TryAddScoped<INhAssistantTurnRunner, NhAssistantTurnRunner>();
        services.TryAddScoped<NhAssistantConversationReader>();
        services.TryAddScoped<INhAssistantTitleGenerator, NhAssistantFirstLineTitleGenerator>();
        return state;
    }

    /// <summary>
    /// True when the application's invocation gate is wrapped by the assistant gate.
    /// </summary>
    public static bool IsInvocationGateDecorated(IServiceCollection services)
    {
        return services.Any(descriptor => descriptor.ServiceType == typeof(NhAssistantGateMarker));
    }

    private static void PreserveAsFallback<TService>(IServiceCollection services)
        where TService : class
    {
        var existing = services.LastOrDefault(descriptor =>
            descriptor.ServiceType == typeof(TService) && !descriptor.IsKeyedService);
        if (existing is null)
        {
            throw new InvalidOperationException(
                $"The NewHeap AI default for {typeof(TService).Name} is missing.");
        }
        services.Add(ToKeyed(existing, NhAssistantFallbacks.ServiceKey));
    }

    private static void DecorateInvocationGate(IServiceCollection services)
    {
        var gate = services.LastOrDefault(descriptor =>
            descriptor.ServiceType == typeof(INhAiToolInvocationGate) && !descriptor.IsKeyedService);
        if (gate is null)
        {
            // Startup validation reports the missing AddNewHeapPlatformAIAspNet registration.
            return;
        }

        services.Remove(gate);
        services.Add(new ServiceDescriptor(
            typeof(INhAiToolInvocationGate),
            provider => new NhAssistantInvocationGate((INhAiToolInvocationGate)CreateInner(provider, gate)),
            gate.Lifetime));
        services.AddSingleton<NhAssistantGateMarker>();
    }

    private static ServiceDescriptor ToKeyed(ServiceDescriptor descriptor, object key)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            return new ServiceDescriptor(descriptor.ServiceType, key, instance);
        }
        if (descriptor.ImplementationFactory is { } factory)
        {
            return new ServiceDescriptor(
                descriptor.ServiceType,
                key,
                (provider, _) => factory(provider),
                descriptor.Lifetime);
        }
        return new ServiceDescriptor(
            descriptor.ServiceType,
            key,
            descriptor.ImplementationType!,
            descriptor.Lifetime);
    }

    private static object CreateInner(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance;
        }
        if (descriptor.ImplementationFactory is { } factory)
        {
            return factory(provider);
        }
        return ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    private sealed class NhAssistantGateMarker;
}
