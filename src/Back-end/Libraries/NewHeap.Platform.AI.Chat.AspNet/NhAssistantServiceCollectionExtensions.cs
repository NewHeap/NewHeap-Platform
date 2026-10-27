using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            // The ASP.NET host runs agents whose run settings are data as well as those in code.
            state.DataAgentRunsEnabled = true;
            services.AddOptions<NhAssistantOptions>().BindConfiguration(NhAssistantOptions.SectionName);
            services.TryAddScoped<NhAssistantAgentAccess>();
            services.AddDataProtection();
            services.AddNewHeapPlatformAIMcp();
            services.TryAddSingleton<NhAssistantMcpSecretProtector>();
            services.TryAddSingleton<NhAssistantMcpHostGuard>();
            services.TryAddSingleton<NhAssistantMcpContextBindings>();
            services.TryAddSingleton<INhAssistantMcpClientFactory, NhAssistantHttpMcpClientFactory>();
            services.TryAddSingleton<NhAssistantMcpConnectionCache>();
            services.TryAddSingleton<NhAssistantMcpSecretSource>();
            services.TryAddSingleton<NhAssistantMcpClientCertificates>();
            services.TryAddSingleton<NhAssistantMcpAuthModeCatalog>();
            services.TryAddScoped<NhAssistantMcpConnectionService>();
            services.TryAddScoped<NhAssistantMcpDelegatedTokenLedger>();
            // OAuth connections: discovery, the host as client, flows and tokens (M3, M4).
            services.TryAddSingleton<NhAssistantMcpOAuthHttp>();
            services.TryAddSingleton<NhAssistantMcpDiscovery>();
            services.TryAddSingleton<NhAssistantMcpTokenProtector>();
            services.TryAddSingleton<NhAssistantMcpTokenCache>();
            services.TryAddSingleton<NhAssistantMcpRenewalGate>();
            services.TryAddScoped<NhAssistantMcpOAuthClient>();
            services.TryAddScoped<NhAssistantMcpOAuthConnections>();
            services.TryAddScoped<NhAssistantMcpAuthorizationFlows>();
            services.TryAddScoped<NhAssistantMcpOwnerSweep>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAssistantMcpMaintenance>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAssistantMcpCredentialProvider, NhAssistantMcpOAuthCredentialProvider>());
            // Delegated MCP tokens that this host mints are never accepted by the host itself.
            services.AddNewHeapPlatformAIMcpDelegatedTokens();
            // Credentials are asked per request; a cached connection keeps only singleton providers.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAssistantMcpCredentialProvider, NhAssistantMcpStaticCredentialProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAssistantMcpCredentialProvider, NhAssistantMcpForwardedTokenCredentialProvider>());
            services.TryAddScoped<NhAssistantMcpConnectionPlanner>();
            services.TryAddScoped<NhAssistantMcpAdministration>();
            // MCP servers, their tools and agent assignments for the application's own code (LS28).
            services.TryAddScoped<NhAssistantMcpServerService>();
            services.TryAddScoped<INhAssistantMcpServers>(provider => provider.GetRequiredService<NhAssistantMcpServerService>());
            services.Replace(ServiceDescriptor.Scoped<INhAssistantMcpToolSource, NhAssistantMcpToolSource>());
            // MCP servers set up as readers turn files into text with the same consent and credential
            // rules as the turn's MCP tools.
            services.TryAddScoped<NhAssistantMcpToolSource>();
            services.Replace(ServiceDescriptor.Scoped<INhAssistantMcpFileReader, NhAssistantMcpFileReader>());

            // Live updates through a SignalR hub, invitation links protected with Data Protection and
            // Web Push sent directly after a turn; the core library keeps no-op defaults.
            services.AddSignalR();
            services.Replace(ServiceDescriptor.Singleton<INhAssistantLiveUpdateTransport, NhAssistantSignalRLiveUpdateTransport>());
            // The NewHeap bearer setup reads the query token for the hub at its configured path (LS27).
            services.TryAddEnumerable(ServiceDescriptor.Singleton<NewHeap.Platform.AspNet.Common.Authentication.INhLibraryHubPath, NhAssistantHubPath>());
            services.TryAddSingleton<INhAssistantShareTokenProtector, NhAssistantShareTokenProtector>();
            // The text a reading tool made of a file is kept per conversation, protected with Data
            // Protection, and removed once it expired (D32).
            services.TryAddSingleton<INhAssistantReaderTextProtector, NhAssistantReaderTextProtector>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAssistantReaderTextMaintenance>());
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
            // The run or the waiting input of a trigger event, for the application (LS13).
            services.TryAddScoped<INhAssistantAgentRunLookup, NhAssistantAgentRunLookup>();
            services.TryAddScoped<NhAssistantHandOffService>();
            services.TryAddScoped<NhAssistantEvaluationService>();
            services.TryAddScoped<NhAssistantReplayService>();
            services.TryAddScoped<INhAssistantHandOffs>(provider => provider.GetRequiredService<NhAssistantHandOffService>());
            services.TryAddScoped<NhAssistantApproverCheck>();
            services.TryAddScoped<NhAssistantManualStarts>();
            services.TryAddScoped<NhAssistantObjectAgents>();
            services.TryAddScoped<NhAssistantRunHistory>();
            services.TryAddScoped<NhAssistantAccounts>();
            services.TryAddScoped<NhAssistantAgentGroups>();
            services.TryAddScoped<INhAssistantAgentGroups>(provider => provider.GetRequiredService<NhAssistantAgentGroups>());
            services.TryAddScoped<NhAssistantAgentBoards>();
            services.TryAddScoped<NhAssistantInboxVisits>();
            services.TryAddScoped<NhAssistantSystemApprovers>();
            services.TryAddSingleton<NhAssistantSystemRoleCache>();
            services.TryAddScoped<NhAssistantAgentWorkWatchers>();
            services.TryAddScoped<NhAssistantProposalDecisions>();
            // The same decision for the application's own channels, such as a messaging reply.
            services.TryAddScoped<INhAssistantProposalDecisionService, NhAssistantProposalDecisionService>();
            // A person's turns in their chat for the application's own channels, without their request (LS19).
            services.TryAddScoped<INhAssistantConversationTurns, NhAssistantConversationTurns>();
            // Creating, reading and stopping a person's chat for the same channels (FB5).
            services.TryAddScoped<INhAssistantConversations, NhAssistantConversations>();
            // The person of the turn, for the application's tools, policies and turn context providers.
            services.TryAddSingleton<INhAiTurnPerson, NewHeap.Platform.AI.AspNet.NhAiAspNetTurnPerson>();
            // What a person's inbox lists, counted per status for the application's digests (LS14).
            services.TryAddScoped<INhAssistantProposalCounts, NhAssistantProposalCounts>();
            services.TryAddScoped<NhAssistantProposalFeedbackService>();
            services.TryAddScoped<INhAssistantSubjectWork, NhAssistantSubjectWork>();
            services.TryAddScoped<NhAssistantProposalReads>();
            services.TryAddScoped<NhAssistantProposalExecution>();
            // Proposals of tools the application executes: the hand-off, the application's reports and
            // the report deadline.
            services.TryAddScoped<NhAssistantApplicationExecutions>();
            services.TryAddScoped<INhAssistantProposalExecutionReports>(provider => provider.GetRequiredService<NhAssistantApplicationExecutions>());
            // A person's inline approval of a tool declared with AlsoInline is handed off the same way (LS17).
            services.TryAddScoped<Runs.INhAssistantInlineHandOffs>(provider => provider.GetRequiredService<NhAssistantApplicationExecutions>());
            services.TryAddScoped<INhAiProposalPrecondition, NhAssistantSubjectVersionPrecondition>();
            services.TryAddScoped<Runs.INhAssistantInlineRunWaker, NhAssistantInlineRunWaker>();
            services.TryAddScoped<NhAssistantRunCancellation>();
            services.TryAddScoped<NhAssistantRunEnding>();
            services.TryAddScoped<NhAssistantServiceAccountBinding>();
            services.TryAddScoped<NhAssistantAgentControl>();
            services.TryAddScoped<INhAssistantAgentControl>(provider => provider.GetRequiredService<NhAssistantAgentControl>());
            services.TryAddScoped<INhAiSubjectRetention, NhAssistantSubjectRetention>();
            services.TryAddScoped<NhAssistantScheduledTriggerJob>();
            // The recurring jobs of schedules in code; a host with RunSchedules false adds none (LS11).
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAssistantScheduleRegistration>());

            // The whole application as a subject, for agents that work on it: app-wide, or per
            // division when the application lists its divisions.
            services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAiSubjectAccessResolver, NhAssistantSystemSubjectResolver>());
            state.AddSubjectType(NhAssistantSystemSubjectType.CreateOptions(typeof(NhAssistantSystemSubjectResolver)));

            // Run configuration as data: the administration service, the recurring jobs of data
            // schedules and the background refresh of the snapshot.
            services.TryAddScoped<NhAssistantAgentConfiguration>();
            services.TryAddScoped<INhAssistantAgentConfiguration>(provider => provider.GetRequiredService<NhAssistantAgentConfiguration>());
            // Hand-offs as data within UseHandOffsAsData and the shadow variants of an agent.
            services.TryAddScoped<NhAssistantDataHandOffs>();
            services.TryAddScoped<INhAssistantDataHandOffs>(provider => provider.GetRequiredService<NhAssistantDataHandOffs>());
            services.TryAddScoped<NhAssistantShadowVariants>();
            // Agent definitions changed from code and agents read as they are in effect (LS23, LS24).
            services.TryAddScoped<NhAssistantAgentDefinitions>();
            services.TryAddScoped<INhAssistantAgentDefinitions>(provider => provider.GetRequiredService<NhAssistantAgentDefinitions>());
            services.TryAddScoped<NhAssistantAgentDirectory>();
            services.TryAddScoped<INhAssistantAgentDirectory>(provider => provider.GetRequiredService<NhAssistantAgentDirectory>());
            // Agent packages: export, preview, import and unlink, and the links the administration shows.
            services.TryAddScoped<NhAssistantAgentPackageLinks>();
            services.TryAddScoped<NhAssistantAgentPackageService>();
            services.TryAddScoped<INhAssistantAgentPackages>(provider => provider.GetRequiredService<NhAssistantAgentPackageService>());
            // Which data skills and agents name a text asset of the application (LS30).
            services.TryAddScoped<INhAssistantInstructionAssetUsage, NhAssistantInstructionAssetUsageService>();
            services.TryAddSingleton<NhAssistantDataScheduleSync>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAssistantRunSnapshotListener, NhAssistantDataScheduleSync>(
                provider => provider.GetRequiredService<NhAssistantDataScheduleSync>()));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAssistantRunConfigurationRefresh>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<
                NewHeap.Platform.AspNet.Common.Services.BackgroundOperations.INhBackgroundOperationAccess,
                NhAssistantBackgroundOperationAccess>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NhAssistantRunMaintenance>());

            // Consultations: the generated consult and request tools of every AllowConsultation.
            services.TryAddScoped<NhAssistantConsultationToolHandler>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiToolCatalog, NhAssistantConsultationToolCatalog>());
            // They act for a person: an agent turn uses them only with a delegating initiator's rights.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiPersonRightsTools, NhAssistantConsultationPersonRightsTools>());
        }

        configure(new NhAssistantBuilder(services, state));
        if (state.ConsultationsAsData is not null)
        {
            AddDataConsultations(services);
        }

        return services;
    }

    /// <summary>
    /// Consultations defined as data: their catalog, which follows every snapshot, and the tool names
    /// it may not take. Their tools act for a person like those in code
    /// (<see cref="NhAssistantConsultationPersonRightsTools"/> names them by their prefixes).
    /// </summary>
    private static void AddDataConsultations(IServiceCollection services)
    {
        services.TryAddSingleton<NhAssistantDataConsultationToolCatalog>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiToolCatalog, NhAssistantDataConsultationToolCatalog>(
            provider => provider.GetRequiredService<NhAssistantDataConsultationToolCatalog>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAiChangingToolCatalog, NhAssistantDataConsultationToolCatalog>(
            provider => provider.GetRequiredService<NhAssistantDataConsultationToolCatalog>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INhAssistantRunSnapshotListener, NhAssistantDataConsultationToolCatalog>(
            provider => provider.GetRequiredService<NhAssistantDataConsultationToolCatalog>()));
        services.TryAddSingleton<INhAssistantReservedToolNames, NhAssistantReservedToolNames>();
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
            if (agent.OfferedInChat && await CanUseAsync(user, agent.Definition))
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
/// managers replaced after the assistant, unknown policies or profiles, a profile in use that permits
/// the execution region neither itself nor through a fallback, or invalid agents. It logs a warning for
/// an agent or skill in code whose data classification no profile of its chain permits.
/// </summary>
internal sealed class NhAssistantStartupValidator(
    IServiceScopeFactory scopeFactory,
    NhAssistantRegistrationState state,
    NhAssistantAgentRegistry registry,
    IAuthorizationPolicyProvider policyProvider,
    INhAiModelProfileRegistry profiles,
    IOptions<NhAssistantOptions> options,
    ILogger<NhAssistantStartupValidator> logger) : IHostedService
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
        if (state.ApplicationBudgetInTurns
            && !NhAssistantBudgetManager.IsApplicationManager(
                provider.GetRequiredKeyedService<INhAiBudgetManager>(NhAssistantFallbacks.ServiceKey)))
        {
            // Every call of every turn would be refused.
            throw new InvalidOperationException(
                "UseApplicationBudgetInTurns needs a budget manager of the application. Register one before AddNewHeapAssistant, "
                + "for example AddNewHeapPlatformAI(ai => ai.UseBudgetManager<TManager>()) or ai.UseInMemoryBudget(...).");
        }

        // Invalid push keys or limits fail here instead of at the first notification.
        options.Value.Push.Validate();
        var forwardedTokenLifetime = options.Value.Mcp.ForwardedTokenLifetime;
        if (forwardedTokenLifetime <= TimeSpan.Zero || forwardedTokenLifetime > NhAiMcpDelegatedTokenDefaults.MaxLifetime)
        {
            throw new InvalidOperationException(
                "NewHeap:AI:Assistant:Mcp:ForwardedTokenLifetime must be longer than zero and at most 5 minutes.");
        }
        // Loopback MCP servers are for development and test hosts, never for Production (LS29).
        var environment = scope.ServiceProvider.GetService<IHostEnvironment>();
        if (options.Value.Mcp.AllowLoopback && environment?.IsProduction() == true)
        {
            throw new InvalidOperationException(
                "NewHeap:AI:Assistant:Mcp:AllowLoopback is for development and test hosts; turn it off in Production.");
        }
        // Invalid OAuth URLs or limits fail here instead of at the first authorization. The redirect
        // URI and return origins may be http on loopback where MCP servers may use loopback.
        options.Value.Mcp.OAuth.Validate(
            NhAssistantMcpHostGuard.AllowsLoopbackIn(environment, options.Value.Mcp));

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

        foreach (var (kind, systemPolicy) in new[] { ("read", state.SystemReadPolicy), ("approve", state.SystemApprovePolicy) })
        {
            if (systemPolicy is not null && await policyProvider.GetPolicyAsync(systemPolicy) is null)
            {
                throw new InvalidOperationException(
                    $"The {kind} policy '{systemPolicy}' of the whole application (UseSystemSubject) is not registered.");
            }
        }
        // UseSingleTenant: the AI ASP.NET integration projects that tenant or none.
        NhAssistantSingleTenantChecks.Validate(state, provider.GetService<NhAiAspNetRegistrationState>());
        // ExecuteInApplication: approved proposals of such a tool go to the application's executor.
        if (state.ApplicationExecutions.Count > 0 && provider.GetService<INhAssistantProposalExecutor>() is null)
        {
            throw new InvalidOperationException(
                $"Tool '{state.ApplicationExecutions.Keys.Order(StringComparer.Ordinal).First()}' is executed by the application (ExecuteInApplication), "
                + "but no executor is registered. Register one with UseProposalExecutor<TExecutor>().");
        }

        await registry.ValidateAsync(
            profiles,
            async policy => await policyProvider.GetPolicyAsync(policy) is not null,
            cancellationToken);

        state.ValidateAgentRuns();
        // One resolver per subject type, no type in the platform's nh- prefix, and every configured
        // subject type with its resolver.
        NewHeap.Platform.AI.Chat.Runs.NhAssistantSubjectAccessService.Validate(
            provider.GetServices<INhAiSubjectAccessResolver>().ToArray(),
            state);
        await ValidateWorksOnAsync(provider);
        var sourceIds = provider.GetServices<INhAiContextSource>()
            .Select(source => source.Descriptor.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var context in state.AgentContexts.Values.Where(context => context.RetrievedContext is not null))
        {
            foreach (var sourceId in context.RetrievedContext!.Sources.Where(sourceId => !sourceIds.Contains(sourceId)))
            {
                throw new InvalidOperationException(
                    $"Agent '{context.AgentId}' retrieves context from source '{sourceId}', which is not registered with AddContextSource.");
            }
        }
        foreach (var (agentId, runOptions) in state.CodeAgentRuns)
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
            if (runOptions.ActWithAccountableRights && runOptions.Accountability == NhAiAgentAccountability.Agent)
            {
                throw new InvalidOperationException(
                    $"Agent '{agentId}' acts with the rights of whom it answers to but answers to itself; choose an accountable person or technical account.");
            }
            if (runOptions.AllowGatewayReads)
            {
                // Gateway reads use a person's rights: the agent must answer to one, and the host must
                // be able to issue that person's delegated read credential.
                if (runOptions.Accountability == NhAiAgentAccountability.Agent)
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' allows gateway reads but answers to itself; choose an accountable person or technical account.");
                }
                if (provider.GetService<INhAiDelegatedReadCredentialIssuer>() is null)
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' allows gateway reads, but no delegated read issuer is registered. Call AddNewHeapPlatformAIDelegatedReads.");
                }
                if (!provider.GetServices<INhAiDelegatedReadSurface>().Any())
                {
                    throw new InvalidOperationException(
                        $"Agent '{agentId}' allows gateway reads, but no API bridge gateway is registered. Call EnableGateway on AddNewHeapPlatformAIMvcBridge.");
                }
            }
        }
        // Every profile in use must run in the execution region, itself or through a fallback: without
        // one, every model call of its agent would be refused and no retry would change that.
        NhAssistantRegionChecks.Validate(state, profiles);
        // A classification that no profile of the chain permits is no reason to stop the host: the
        // profiles may change without a deploy of the agent. The model calls fail until then.
        NhAssistantClassificationChecks.LogCodeWarnings(state, profiles, logger);
        // A chat-only agent never runs, so it needs no background operations (FB8).
        if (state.CodeAgentRuns.Values.Any(run => !run.ChatOnly))
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
            // The platform's own whole application serves schedules without subjects; every other
            // trigger works on the application's objects.
            var applicationTypes = subjectTypes.SubjectTypes.Count(type => !NhAiSubjectRef.IsReservedType(type));
            foreach (var trigger in state.CodeTriggers.Values.Where(trigger => !trigger.IsSystemSchedule))
            {
                if (applicationTypes == 0)
                {
                    throw new InvalidOperationException(
                        $"Trigger '{trigger.Id}' needs a subject access resolver. Register one with AddSubjectType.");
                }
            }

            await ValidateConsultationsAsync(provider, subjectTypes);
        }
    }

    /// <summary>
    /// What agents in code work on names registered application types and policies, and works per
    /// division only when the application lists its divisions; triggers and trigger events that name
    /// their subject type up front name a registered one, and every policy listed for administrators
    /// is registered.
    /// </summary>
    private async Task ValidateWorksOnAsync(IServiceProvider provider)
    {
        var subjectTypes = provider.GetRequiredService<NewHeap.Platform.AI.Chat.Runs.NhAssistantSubjectAccessService>();
        var divisionsListed = provider.GetRequiredService<NhAssistantWorksOnRule>().DivisionsListed;
        foreach (var (agentId, runOptions) in state.CodeAgentRuns)
        {
            // Per division needs the divisions the application lists; without them everything runs
            // app-wide.
            if (runOptions.WorksOn.PerDivision && !divisionsListed)
            {
                throw new InvalidOperationException(
                    $"Agent '{agentId}' works per division, but the application lists no divisions. Use NewHeap Identity or register a division directory with AddNewHeapDivisionDirectory, or let the agent work app-wide.");
            }

            foreach (var subjectType in runOptions.WorksOn.SubjectTypes.Where(type => !subjectTypes.IsKnownType(type)))
            {
                throw new InvalidOperationException(
                    $"Agent '{agentId}' works on '{subjectType}', which has no subject access resolver. Register one with AddSubjectType.");
            }

            foreach (var policy in new[] { runOptions.WorksOn.RunPolicy, runOptions.WorksOn.ViewPolicy }.OfType<string>())
            {
                if (await policyProvider.GetPolicyAsync(policy) is null)
                {
                    throw new InvalidOperationException($"Agent '{agentId}' names policy '{policy}', which is not registered.");
                }
            }
        }

        foreach (var trigger in state.CodeTriggers.Values.Where(trigger => trigger.SubjectType is not null && !subjectTypes.IsKnownType(trigger.SubjectType)))
        {
            throw new InvalidOperationException(
                $"Trigger '{trigger.Id}' names subject type '{trigger.SubjectType}', which has no subject access resolver. Register one with AddSubjectType.");
        }

        foreach (var triggerEvent in state.TriggerEvents.Values.Where(item => item.SubjectType is not null && !subjectTypes.IsKnownType(item.SubjectType)))
        {
            throw new InvalidOperationException(
                $"Trigger event '{triggerEvent.Name}' names subject type '{triggerEvent.SubjectType}', which has no subject access resolver. Register one with AddSubjectType.");
        }

        foreach (var policy in state.AgentPolicies.Keys)
        {
            if (await policyProvider.GetPolicyAsync(policy) is null)
            {
                throw new InvalidOperationException($"The agent policy '{policy}' listed with AddAgentPolicy is not registered.");
            }
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

        foreach (var consultation in state.CodeAgentRuns.Values.SelectMany(run => run.Consultations))
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
