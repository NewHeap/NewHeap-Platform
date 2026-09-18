using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Governance;

namespace NewHeap.Platform.AI.Chat.AspNet;

public static class NhAssistantServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NewHeap assistant: durable conversations, the agent turn runner, approvals
    /// with exact NewHeap proposals, durable budget and idempotency managers, content-free audit
    /// relay and the options bound from <c>NewHeap:AI:Assistant</c>. Call it after
    /// <c>AddNewHeapPlatformAIAspNet</c> and after the application's own AI registrations, and map
    /// the endpoints with <c>MapNewHeapAssistant</c>. Repeated calls are idempotent.
    /// </summary>
    public static IServiceCollection AddNewHeapAssistant(
        this IServiceCollection services,
        Action<NhAssistantBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var firstRegistration = !services.Any(descriptor =>
            descriptor.ServiceType == typeof(NhAssistantRegistrationState));
        var state = NhAssistantServiceRegistration.EnsureRegistered(services);
        if (firstRegistration)
        {
            services.AddOptions<NhAssistantOptions>().BindConfiguration(NhAssistantOptions.SectionName);
            services.TryAddScoped<NhAssistantAgentAccess>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAssistantStartupValidator>());
        }

        configure(new NhAssistantBuilder(services, state));
        return services;
    }
}

/// <summary>
/// Evaluates the agent and access policies for the current user.
/// </summary>
internal sealed class NhAssistantAgentAccess(
    NhAssistantAgentCatalog catalog,
    IAuthorizationService authorizationService)
{
    public async Task<IReadOnlyList<NhAssistantAgentDefinition>> GetVisibleAgentsAsync(
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var visible = new List<NhAssistantAgentDefinition>();
        foreach (var agent in await catalog.ListAsync(includeDisabled: false, cancellationToken))
        {
            if (await CanUseAsync(user, agent.Definition))
            {
                visible.Add(agent.Definition);
            }
        }
        return visible;
    }

    public async Task<bool> CanUseAsync(
        System.Security.Claims.ClaimsPrincipal user,
        NhAssistantAgentDefinition agent)
    {
        return agent.RequiredPolicy is null
            || (await authorizationService.AuthorizeAsync(user, agent.RequiredPolicy)).Succeeded;
    }
}

/// <summary>
/// Fails host start when the assistant registration is incomplete: missing ASP.NET AI integration,
/// managers replaced after the assistant, unknown policies or profiles, or invalid agents.
/// </summary>
internal sealed class NhAssistantStartupValidator(
    IServiceScopeFactory scopeFactory,
    NhAssistantRegistrationState state,
    NhAssistantAgentRegistry registry,
    IAuthorizationPolicyProvider policyProvider,
    INhAiModelProfileRegistry profiles,
    IOptions<NhAssistantOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        if (provider.GetService<INhAiAuthenticatedInvocationContextResolver>() is null)
        {
            throw new InvalidOperationException(
                "The assistant requires AddNewHeapPlatformAIAspNet. Register it before AddNewHeapAssistant.");
        }
        if (provider.GetService<INhAiToolInvocationGate>() is not NhAssistantInvocationGate)
        {
            throw new InvalidOperationException(
                "The assistant must wrap the ASP.NET AI invocation gate. Call AddNewHeapAssistant after AddNewHeapPlatformAIAspNet and do not replace the gate afterwards.");
        }
        if (provider.GetService<INhAiBudgetManager>() is not NhAssistantBudgetManager
            || provider.GetService<INhAiIdempotencyManager>() is not NhAssistantIdempotencyManager
            || provider.GetService<INhAiApprovalEvidenceProvider>() is not NhAssistantApprovalEvidenceProvider)
        {
            throw new InvalidOperationException(
                "The assistant budget, idempotency or approval-evidence manager was replaced. Call AddNewHeapAssistant after the application's own AI registrations.");
        }

        var accessPolicy = NhAssistantEndpointOptions.ResolveAccessPolicy(state, options.Value);
        if (await policyProvider.GetPolicyAsync(accessPolicy) is null)
        {
            throw new InvalidOperationException(
                $"The assistant access policy '{accessPolicy}' is not registered.");
        }

        await registry.ValidateAsync(
            profiles,
            async policy => await policyProvider.GetPolicyAsync(policy) is not null,
            cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal static class NhAssistantEndpointOptions
{
    public static string ResolveAccessPolicy(NhAssistantRegistrationState state, NhAssistantOptions options)
    {
        return state.AccessPolicy
            ?? (string.IsNullOrWhiteSpace(options.AccessPolicy)
                ? NhAssistantOptions.DefaultAccessPolicy
                : options.AccessPolicy);
    }
}
