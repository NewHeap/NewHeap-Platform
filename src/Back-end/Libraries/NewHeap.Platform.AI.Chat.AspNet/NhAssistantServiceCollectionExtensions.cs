using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Live;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.AspNet.Notifications;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Mcp;
using Microsoft.AspNetCore.DataProtection;

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
            services.AddDataProtection();
            services.AddNewHeapPlatformAIMcp();
            services.TryAddSingleton<NhAssistantMcpSecretProtector>();
            services.TryAddSingleton<NhAssistantMcpHostGuard>();
            services.TryAddSingleton<NhAssistantMcpContextBindings>();
            services.TryAddSingleton<INhAssistantMcpClientFactory, NhAssistantHttpMcpClientFactory>();
            services.TryAddSingleton<NhAssistantMcpConnectionCache>();
            services.TryAddScoped<NhAssistantMcpConnectionPlanner>();
            services.TryAddScoped<NhAssistantMcpAdministration>();
            services.Replace(ServiceDescriptor.Scoped<INhAssistantMcpToolSource, NhAssistantMcpToolSource>());

            // Live updates through a SignalR hub, invitation links protected with Data Protection and
            // Web Push sent directly after a turn; the core library keeps no-op defaults.
            services.AddSignalR();
            services.Replace(ServiceDescriptor.Singleton<INhAssistantLiveUpdateTransport, NhAssistantSignalRLiveUpdateTransport>());
            services.TryAddSingleton<INhAssistantShareTokenProtector, NhAssistantShareTokenProtector>();
            services.Replace(ServiceDescriptor.Scoped<INhAssistantNotifier, NhAssistantWebPushNotifier>());
            services.AddHttpClient(NhAssistantWebPushNotifier.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    // A push service answers directly; following a redirect would post elsewhere.
                    AllowAutoRedirect = false,
                    UseCookies = false
                });
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAssistantStartupValidator>());
            // Approvals are presented in every supported UI culture of the application.
            services.Replace(ServiceDescriptor.Singleton<Runtime.INhAssistantPresentationCultures, NhAssistantRequestPresentationCultures>());

            // Agent runs: triggers, headless runs as background operations, approval decisions,
            // subject access to the operations and the maintenance job.
            services.TryAddScoped<NhAssistantAgentTurnFactory>();
            services.TryAddScoped<NhAssistantAgentTriggers>();
            services.TryAddScoped<INhAssistantAgentTriggers>(provider => provider.GetRequiredService<NhAssistantAgentTriggers>());
            services.TryAddScoped<NhAssistantApproverCheck>();
            services.TryAddScoped<NhAssistantProposalDecisions>();
            services.TryAddScoped<NhAssistantProposalFeedbackService>();
            services.TryAddScoped<INhAssistantSubjectWork, NhAssistantSubjectWork>();
            services.TryAddScoped<NhAssistantProposalReads>();
            services.TryAddScoped<NhAssistantProposalExecutor>();
            services.TryAddScoped<INhAiProposalPrecondition, NhAssistantSubjectVersionPrecondition>();
            services.TryAddScoped<Runs.INhAssistantInlineRunWaker, NhAssistantInlineRunWaker>();
            services.TryAddScoped<NhAssistantRunCancellation>();
            services.TryAddScoped<NhAssistantAgentControl>();
            services.TryAddScoped<INhAssistantAgentControl>(provider => provider.GetRequiredService<NhAssistantAgentControl>());
            services.TryAddScoped<INhAiSubjectRetention, NhAssistantSubjectRetention>();
            services.TryAddScoped<NhAssistantScheduledTriggerJob>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAssistantScheduleRegistration>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<
                NewHeap.Platform.AspNet.Common.Services.BackgroundOperations.INhBackgroundOperationAccess,
                NhAssistantBackgroundOperationAccess>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAssistantRunMaintenance>());

            // Consultations: the generated consult and request tools of every AllowConsultation.
            services.TryAddScoped<NhAssistantConsultationToolHandler>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiToolCatalog, NhAssistantConsultationToolCatalog>());
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
    IAuthorizationService authorizationService,
    NhAssistantRegistrationState state,
    IOptionsMonitor<NhAssistantOptions> options)
{
    public async Task<bool> CanAdministerAsync(System.Security.Claims.ClaimsPrincipal user)
    {
        var policy = NhAssistantEndpointOptions.ResolveAdminPolicy(state, options.CurrentValue);
        return (await authorizationService.AuthorizeAsync(user, policy)).Succeeded;
    }

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

        // Invalid push keys or limits fail here instead of at the first notification.
        options.Value.Push.Validate();

        var accessPolicy = NhAssistantEndpointOptions.ResolveAccessPolicy(state, options.Value);
        if (await policyProvider.GetPolicyAsync(accessPolicy) is null)
        {
            throw new InvalidOperationException(
                $"The assistant access policy '{accessPolicy}' is not registered.");
        }

        var adminPolicy = NhAssistantEndpointOptions.ResolveAdminPolicy(state, options.Value);
        if (await policyProvider.GetPolicyAsync(adminPolicy) is null)
        {
            throw new InvalidOperationException(
                $"The assistant admin policy '{adminPolicy}' is not registered. Register it or call UseAdminPolicy.");
        }

        await registry.ValidateAsync(
            profiles,
            async policy => await policyProvider.GetPolicyAsync(policy) is not null,
            cancellationToken);

        state.ValidateAgentRuns();
        foreach (var (agentId, runOptions) in state.AgentRuns)
        {
            // A skill's own profile does what the agent's does; triage only needs chat.
            foreach (var skill in runOptions.Skills.Values.Where(skill => skill.ProfileName is not null))
            {
                if (!profiles.TryGet(skill.ProfileName!, out var profile)
                    || (profile.Capabilities & (NhAiModelCapability.Chat | NhAiModelCapability.FunctionCalling | NhAiModelCapability.Streaming))
                        != (NhAiModelCapability.Chat | NhAiModelCapability.FunctionCalling | NhAiModelCapability.Streaming))
                {
                    throw new InvalidOperationException(
                        $"Skill '{skill.Id}' of agent '{agentId}' references chat profile '{skill.ProfileName}', which is not registered with chat, function calling and streaming.");
                }
            }
            if (runOptions.TriageProfileName is { } triage
                && (!profiles.TryGet(triage, out var triageProfile) || (triageProfile.Capabilities & NhAiModelCapability.Chat) == 0))
            {
                throw new InvalidOperationException(
                    $"The triage of agent '{agentId}' references chat profile '{triage}', which is not registered with chat.");
            }
            if (runOptions.TriageProfileName is not null && runOptions.Skills.Count == 0)
            {
                throw new InvalidOperationException($"Agent '{agentId}' has a triage but no skills.");
            }
        }
        if (state.AgentRuns.Count > 0)
        {
            var operations = provider.GetService<NewHeap.Platform.AspNet.Common.Services.BackgroundOperations.NhBackgroundOperationRegistry>();
            if (operations is null
                || !operations.TryGetForOperationType(NhAssistantAgentRunOperationTypes.Run, out _)
                || !operations.TryGetForOperationType(NhAssistantAgentRunOperationTypes.ExecuteProposal, out _)
                || !operations.TryGetForOperationType(NhAssistantAgentRunOperationTypes.ExecuteProposalOnce, out _))
            {
                throw new InvalidOperationException(
                    "Agents configured with ConfigureAgent need background operations. Call AddAssistantAgentRuns inside WithBackgroundOperations.");
            }

            var subjectTypes = provider.GetRequiredService<NewHeap.Platform.AI.Chat.Runs.NhAssistantSubjectAccessService>();
            foreach (var trigger in state.Triggers.Values)
            {
                if (subjectTypes.SubjectTypes.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Trigger '{trigger.Id}' needs a subject access resolver. Register one with AddSubjectType.");
                }
            }

            await ValidateConsultationsAsync(provider, subjectTypes);
        }
    }

    /// <summary>
    /// Every consultation needs a resolver for its subject type and registered policies, and its
    /// generated catalog must attest that every tool runs through the shared invoker.
    /// </summary>
    private async Task ValidateConsultationsAsync(
        IServiceProvider provider,
        NewHeap.Platform.AI.Chat.Runs.NhAssistantSubjectAccessService subjectTypes)
    {
        var catalog = provider.GetServices<INhAiToolCatalog>().OfType<NhAssistantConsultationToolCatalog>().SingleOrDefault();
        if (catalog is null)
        {
            return;
        }

        foreach (var consultation in state.AgentRuns.Values.SelectMany(run => run.Consultations))
        {
            if (!subjectTypes.IsKnownType(consultation.SubjectType))
            {
                throw new InvalidOperationException(
                    $"Agent '{consultation.AgentId}' allows consultation about '{consultation.SubjectType}', which has no subject access resolver. Register one with AddSubjectType.");
            }
        }

        foreach (var policy in catalog.Descriptors.SelectMany(descriptor => descriptor.AuthorizationPolicies).Distinct(StringComparer.Ordinal))
        {
            if (await policyProvider.GetPolicyAsync(policy) is null)
            {
                throw new InvalidOperationException($"The consultation policy '{policy}' is not registered.");
            }
        }

        NhAiToolCatalogAttestation.Validate(catalog, provider);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal static class NhAssistantEndpointOptions
{
    public static string ResolveAdminPolicy(NhAssistantRegistrationState state, NhAssistantOptions options)
    {
        return state.AdminPolicy
            ?? (string.IsNullOrWhiteSpace(options.AdminPolicy)
                ? NhAssistantOptions.DefaultAdminPolicy
                : options.AdminPolicy);
    }

    public static string ResolveAccessPolicy(NhAssistantRegistrationState state, NhAssistantOptions options)
    {
        return state.AccessPolicy
            ?? (string.IsNullOrWhiteSpace(options.AccessPolicy)
                ? NhAssistantOptions.DefaultAccessPolicy
                : options.AccessPolicy);
    }
}
