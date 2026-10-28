using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AgentFramework;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Actors;

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
        services.TryAddScoped<NhAssistantMcpOAuthStore>();
        services.TryAddScoped<NhAssistantAgentCatalog>();
        services.TryAddScoped<NhAssistantShadowModes>();
        // The run configuration defined as data and the snapshot that puts it into effect.
        services.TryAddSingleton<NhAssistantRunConfigurationStore>();
        services.TryAddSingleton<NhAssistantRunConfigurationValidator>();
        // Instructions from the application's text assets (UseTextAssetSource), verified and kept per
        // asset id and version.
        services.TryAddSingleton<NhAssistantInstructionAssets>();
        services.TryAddSingleton<NhAssistantRunConfigurations>();
        // Agent packages: the packages this host imported, the links of their parts and the locks
        // every save path enforces.
        services.TryAddSingleton<NhAssistantAgentPackageStore>();
        services.TryAddSingleton<NhAssistantPackageLockCheck>();
        services.TryAddScoped<NhAssistantAgentAdministration>();
        // Agent metadata: the application's own entries about agents, stored and checked, never read
        // by NewHeap itself.
        services.TryAddScoped<NhAssistantAgentMetadataStore>();
        services.TryAddScoped<NhAssistantAgentMetadataService>();
        services.TryAddScoped<INhAssistantAgentMetadata>(provider => provider.GetRequiredService<NhAssistantAgentMetadataService>());
        services.TryAddSingleton<NhAssistantWorksOnRule>();
        // The tenant rule of agent work, which warns about callers without a tenant.
        services.TryAddSingleton<NhAssistantTenancy>();
        services.TryAddScoped<NhAssistantAgentKnownSubjectTypes>();
        services.TryAddScoped<NhAssistantPersonalization>();
        services.TryAddScoped<NhAssistantTurnContextCollector>();
        services.TryAddScoped<NhAssistantToolPresentationResolver>();
        services.TryAddScoped<INhAssistantMcpToolSource, NhAssistantNoMcpToolSource>();
        services.TryAddSingleton<NhAssistantTurnCancellationRegistry>();
        services.TryAddScoped<INhAssistantTurnRunner, NhAssistantTurnRunner>();
        // The step runtime of compositions and scripts.
        services.TryAddScoped<INhAssistantToolSteps, NhAssistantToolSteps>();
        services.TryAddScoped<NhAssistantConversationReader>();
        services.TryAddScoped<INhAssistantConversationSnapshotProvider, NhAssistantConversationSnapshotProvider>();
        services.TryAddScoped<INhAssistantTitleGenerator, NhAssistantFirstLineTitleGenerator>();
        // Compositions and the script tool: the application's switch (configuration by default) under
        // the ceiling in code.
        services.TryAddScoped<INhAssistantCompositionSwitch, NhAssistantConfigurationCompositionSwitch>();
        services.TryAddScoped<NhAssistantCompositionGate>();

        // Collaboration: sharing, read state, live updates and notifications. The ASP.NET package
        // replaces the transport, the notifier and the token protector.
        services.TryAddSingleton<NhAssistantConversationAudience>();
        services.TryAddSingleton<INhAssistantLiveUpdateTransport, NhAssistantNoLiveUpdateTransport>();
        // Who may follow a conversation when something is delivered without their request; the
        // ASP.NET package asks the application's principal factory.
        services.TryAddScoped<INhAssistantRecipientRules, NhAssistantEveryoneReceives>();
        services.TryAddScoped<NhAssistantConversationReaders>();
        services.TryAddScoped<NhAssistantLivePublisher>();
        // Forgetting a user removes their push subscriptions, notification choice, MCP connection
        // notices and participant rows.
        services.AddNewHeapRetentionParticipant<NhAssistantRetentionParticipant>();
        services.TryAddScoped<INhAssistantNotifier, NhAssistantNoNotifier>();
        services.TryAddScoped<NhAssistantNotificationStore>();
        services.TryAddScoped<NhAssistantCollaboration>();
        // The application posts its own messages into conversations (SPM-312), and notifies of them in
        // its inbox when it turned on in-app agent notifications.
        services.TryAddScoped<INhAssistantConversationMessages, NhAssistantConversationMessages>();
        services.TryAddScoped<NhAssistantInAppApplicationMessageNotifier>();
        services.TryAddScoped<INhAssistantDisplayNameResolver, NhAssistantClaimsDisplayNameResolver>();

        // Agent runs: subject threads, member snapshots and approval policies.
        services.TryAddScoped<NhAssistantRunStore>();
        services.TryAddScoped<NhAssistantHandOffStore>();
        services.TryAddScoped<NhAssistantEvaluationStore>();
        services.TryAddScoped<Runtime.NhAssistantApplicationMapService>();
        services.TryAddScoped<Runtime.NhAssistantRetrievalService>();
        services.TryAddScoped<Runtime.NhAssistantTurnFiles>();
        services.TryAddScoped<Runtime.NhAssistantUploads>();
        // Reading tools: the application's own, then MCP servers set up as readers, which the ASP.NET
        // integration supplies.
        services.TryAddScoped<Runtime.NhAssistantFileReaders>();
        services.TryAddScoped<Runtime.INhAssistantMcpFileReader, Runtime.NhAssistantNoMcpFileReader>();
        // The texts readers made, kept per conversation while a protector exists; the ASP.NET
        // integration registers one on ASP.NET Data Protection.
        services.TryAddScoped<Runtime.NhAssistantReaderTexts>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAiContextTraceSink, Runtime.NhAssistantContextTraceSink>());
        services.TryAddScoped<NhAssistantSubjectAccessService>();
        services.TryAddScoped<INhAiSubjectAccess>(provider => provider.GetRequiredService<NhAssistantSubjectAccessService>());
        services.TryAddScoped<NhAssistantSubjectSearchService>();
        services.TryAddScoped<NhAssistantThreadService>();
        services.TryAddScoped<NhAssistantApprovalPolicies>();
        services.TryAddScoped<INhAssistantAgentNotificationPolicy, NhAssistantDefaultAgentNotificationPolicy>();
        services.TryAddScoped<NhAssistantAgentNotifier>();
        services.TryAddScoped<NhAssistantMcpConnectionNotices>();
        services.TryAddScoped<NhAssistantAgentEvents>();
        services.TryAddScoped<NhAssistantMemoryService>();
        services.TryAddScoped<NhAssistantBriefingService>();
        services.TryAddScoped<NhAssistantRunSummaries>();
        services.TryAddScoped<NhAssistantRunResultWriter>();
        services.TryAddScoped<NhAssistantRunResultCultures>();
        services.TryAddScoped<NhAssistantArtifactStore>();
        services.TryAddScoped<NhAssistantProposalFeedbackStore>();
        services.TryAddScoped<NhAssistantBoardStore>();
        services.TryAddScoped<Live.NhAssistantAgentWorkUpdates>();
        services.TryAddScoped<INhAiArtifacts, NhAssistantArtifacts>();
        services.TryAddScoped<NhAssistantConsultationService>();
        services.TryAddScoped<NhAssistantConsultationProposals>();
        // Proposal guards and revalidation.
        services.TryAddScoped<NhAssistantProposalGuards>();
        services.TryAddScoped<NhAssistantStaleExplanations>();
        services.TryAddScoped<NhAssistantProposalInvalidation>();
        services.TryAddScoped<NhAssistantProposalDuplicates>();
        services.TryAddScoped<NhAssistantProposalPlacement>();
        services.TryAddScoped<INhAssistantProposalRevalidation, NhAssistantProposalRevalidation>();
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
