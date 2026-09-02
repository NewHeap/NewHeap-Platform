using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace NewHeap.Platform.AI;

public static class NhAiServiceCollectionExtensions
{
    public static IServiceCollection AddNewHeapPlatformAI(
        this IServiceCollection services,
        Action<NhAiBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var state = services
            .Where(descriptor => descriptor.ServiceType == typeof(NhAiRegistrationState))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<NhAiRegistrationState>()
            .SingleOrDefault();
        if (state is null)
        {
            state = new NhAiRegistrationState();
            services.AddSingleton(state);
            services.TryAddSingleton<INhAiModelProfileRegistry, NhAiModelProfileRegistry>();
            services.TryAddScoped<INhAiModelProfileResolver, NhAiModelProfileResolver>();
            services.TryAddScoped<INhAiChatExecutor, NhAiChatExecutor>();
            services.TryAddScoped<INhAiInvocationContextFactory, NhAiInvocationContextFactory>();
            services.TryAddScoped<INhAiToolInvoker, NhAiToolInvoker>();
            services.TryAddScoped<INhAiToolDiscoveryPolicy, NhAiDenyAllToolDiscoveryPolicy>();
            services.TryAddScoped<INhAiToolDiscoveryService, NhAiToolDiscoveryService>();
            services.TryAddScoped<INhAiContextResolver, NhAiContextResolver>();
            services.TryAddScoped<INhAiContextAuthorizationPolicy, NhAiDenyContextAuthorizationPolicy>();
            services.TryAddScoped<INhAiContextRanker, NhAiDeterministicContextRanker>();
            services.TryAddScoped<INhAiContextConflictResolver, NhAiDeterministicContextConflictResolver>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<INhAiContextTraceSink, NhAiNoOpContextTraceSink>());
            services.TryAddSingleton<INhAiContextFormatter, NhAiJsonContextFormatter>();
            services.TryAddSingleton<INhAiPromptAssembler, NhAiRoleSeparatedPromptAssembler>();
            services.TryAddScoped<INhAiIngestionPipeline, NhAiIngestionPipeline>();
            services.TryAddScoped<INhAiIngestionAuthorizationPolicy, NhAiDenyIngestionAuthorizationPolicy>();
            services.TryAddScoped<INhAiIngestionVersionManager, NhAiDenyIngestionVersionManager>();
            services.TryAddSingleton<INhAiDocumentChunker, NhAiDeterministicDocumentChunker>();
            services.TryAddScoped<INhAiCapabilityResolver, NhAiInvocationContextCapabilityResolver>();
            services.TryAddSingleton<INhAiToolConcurrencyLimiter, NhAiInProcessToolConcurrencyLimiter>();
            services.TryAddScoped<INhAiEffectPolicy, NhAiDefaultEffectPolicy>();
            services.TryAddScoped<INhAiApprovalEvidenceProvider, NhAiDenyApprovalEvidenceProvider>();
            services.TryAddScoped<INhAiIdempotencyManager, NhAiDenyIdempotencyManager>();
            services.TryAddScoped<INhAiBudgetManager, NhAiDenyBudgetManager>();
            services.TryAddSingleton<INhAiTaskResultMapper, NhAiTaskResultMapper>();
            services.TryAddSingleton<INhAiProposalFactory, NhAiProposalFactory>();
            services.TryAddSingleton<INhAiApprovalValidator, NhAiApprovalValidator>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, NhAiStartupValidator>());
        }

        configure?.Invoke(new NhAiBuilder(services, state));
        return services;
    }
}

public sealed class NhAiBuilder
{
    private readonly IServiceCollection _services;
    private readonly NhAiRegistrationState _state;

    internal NhAiBuilder(IServiceCollection services, NhAiRegistrationState state)
    {
        _services = services;
        _state = state;
    }

    public NhAiBuilder AddChatProfile(
        string name,
        Action<NhAiModelProfileBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new NhAiModelProfileBuilder(name);
        configure(builder);
        _state.AddProfile(builder.Build());
        return this;
    }

    public NhAiBuilder AddEmbeddingProfile(
        string name,
        Action<NhAiModelProfileBuilder> configure)
    {
        return AddChatProfile(name, configure);
    }

    public NhAiBuilder AddGeneratedToolCatalog<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCatalog>()
        where TCatalog : class, INhAiToolCatalog
    {
        _services.TryAddEnumerable(
            ServiceDescriptor.Singleton<INhAiToolCatalog, TCatalog>());
        return this;
    }

    public NhAiBuilder RequireProfile(
        string profileName,
        params NhAiModelCapability[] requiredCapabilities)
    {
        NhAiNames.ValidateSegment(profileName, nameof(profileName));
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        var capabilities = requiredCapabilities.Aggregate(
            NhAiModelCapability.None,
            (current, capability) => current | capability);
        _state.AddStartupRequirement(profileName, capabilities);
        return this;
    }

    public NhAiBuilder UseInvocationGate<TGate>()
        where TGate : class, INhAiToolInvocationGate
    {
        _services.Replace(
            ServiceDescriptor.Scoped<INhAiToolInvocationGate, TGate>());
        return this;
    }

    public NhAiBuilder UseDiscoveryPolicy<TPolicy>()
        where TPolicy : class, INhAiToolDiscoveryPolicy
    {
        _services.Replace(
            ServiceDescriptor.Scoped<INhAiToolDiscoveryPolicy, TPolicy>());
        return this;
    }

    public NhAiBuilder UseEffectPolicy<TPolicy>()
        where TPolicy : class, INhAiEffectPolicy
    {
        _services.Replace(
            ServiceDescriptor.Scoped<INhAiEffectPolicy, TPolicy>());
        return this;
    }

    public NhAiBuilder UseCapabilityResolver<TResolver>()
        where TResolver : class, INhAiCapabilityResolver
    {
        _services.Replace(
            ServiceDescriptor.Scoped<INhAiCapabilityResolver, TResolver>());
        return this;
    }

    public NhAiBuilder UseApprovalEvidenceProvider<TProvider>()
        where TProvider : class, INhAiApprovalEvidenceProvider
    {
        _services.Replace(
            ServiceDescriptor.Scoped<INhAiApprovalEvidenceProvider, TProvider>());
        return this;
    }

    public NhAiBuilder UseIdempotencyManager<TManager>(
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TManager : class, INhAiIdempotencyManager
    {
        _services.Replace(
            ServiceDescriptor.Describe(
                typeof(INhAiIdempotencyManager),
                typeof(TManager),
                lifetime));
        return this;
    }

    public NhAiBuilder AddVerifier<TVerifier>()
        where TVerifier : class, INhAiToolVerifier
    {
        _services.TryAddEnumerable(
            ServiceDescriptor.Scoped<INhAiToolVerifier, TVerifier>());
        return this;
    }

    public NhAiBuilder AddContextContributor<TContributor>()
        where TContributor : class, INhAiInvocationContextContributor
    {
        _services.TryAddEnumerable(
            ServiceDescriptor.Scoped<INhAiInvocationContextContributor, TContributor>());
        return this;
    }

    public NhAiBuilder AddAuditSink<TSink>()
        where TSink : class, INhAiAuditSink
    {
        _services.TryAddEnumerable(
            ServiceDescriptor.Scoped<INhAiAuditSink, TSink>());
        return this;
    }

    public NhAiBuilder AddUsageSink<TSink>()
        where TSink : class, INhAiUsageSink
    {
        _services.TryAddEnumerable(
            ServiceDescriptor.Scoped<INhAiUsageSink, TSink>());
        return this;
    }

    public NhAiBuilder AddContextSource<TSource>()
        where TSource : class, INhAiContextSource
    {
        _services.TryAddEnumerable(
            ServiceDescriptor.Scoped<INhAiContextSource, TSource>());
        return this;
    }

    public NhAiBuilder UseContextAuthorizationPolicy<TPolicy>()
        where TPolicy : class, INhAiContextAuthorizationPolicy
    {
        _services.Replace(
            ServiceDescriptor.Scoped<INhAiContextAuthorizationPolicy, TPolicy>());
        return this;
    }

    public NhAiBuilder UseContextRanker<TRanker>()
        where TRanker : class, INhAiContextRanker
    {
        _services.Replace(
            ServiceDescriptor.Scoped<INhAiContextRanker, TRanker>());
        return this;
    }

    public NhAiBuilder UseContextConflictResolver<TResolver>()
        where TResolver : class, INhAiContextConflictResolver
    {
        _services.Replace(
            ServiceDescriptor.Scoped<INhAiContextConflictResolver, TResolver>());
        return this;
    }

    public NhAiBuilder AddContextTraceSink<TSink>()
        where TSink : class, INhAiContextTraceSink
    {
        _services.AddScoped<INhAiContextTraceSink, TSink>();
        return this;
    }

    public NhAiBuilder UsePromptAssembler<TAssembler>()
        where TAssembler : class, INhAiPromptAssembler
    {
        _services.Replace(
            ServiceDescriptor.Singleton<INhAiPromptAssembler, TAssembler>());
        return this;
    }

    public NhAiBuilder AddIngestionSource<TSource>()
        where TSource : class, INhAiIngestionSource
    {
        _services.AddScoped<INhAiIngestionSource, TSource>();
        return this;
    }

    public NhAiBuilder UseIngestionAuthorizationPolicy<TPolicy>()
        where TPolicy : class, INhAiIngestionAuthorizationPolicy
    {
        _services.Replace(
            ServiceDescriptor.Scoped<INhAiIngestionAuthorizationPolicy, TPolicy>());
        return this;
    }

    public NhAiBuilder UseIngestionVersionManager<TManager>(
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TManager : class, INhAiIngestionVersionManager
    {
        _services.Replace(
            new ServiceDescriptor(
                typeof(INhAiIngestionVersionManager),
                typeof(TManager),
                lifetime));
        return this;
    }

    public NhAiBuilder UseDocumentChunker<TChunker>()
        where TChunker : class, INhAiDocumentChunker
    {
        _services.Replace(
            ServiceDescriptor.Singleton<INhAiDocumentChunker, TChunker>());
        return this;
    }

    public NhAiBuilder UseBudgetManager<TManager>(
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TManager : class, INhAiBudgetManager
    {
        _services.Replace(
            ServiceDescriptor.Describe(
                typeof(INhAiBudgetManager),
                typeof(TManager),
                lifetime));
        return this;
    }

    public NhAiBuilder UseConcurrencyLimiter<TLimiter>(
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TLimiter : class, INhAiToolConcurrencyLimiter
    {
        _services.Replace(
            ServiceDescriptor.Describe(
                typeof(INhAiToolConcurrencyLimiter),
                typeof(TLimiter),
                lifetime));
        return this;
    }
}

internal sealed class NhAiRegistrationState
{
    private readonly Dictionary<string, NhAiModelProfile> _profiles =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, NhAiModelCapability> _startupRequirements =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<NhAiModelProfile> Profiles => _profiles.Values;
    public IReadOnlyDictionary<string, NhAiModelCapability> StartupRequirements =>
        _startupRequirements;

    public void AddProfile(NhAiModelProfile profile)
    {
        if (_profiles.TryGetValue(profile.Name, out var existing))
        {
            if (string.Equals(existing.Fingerprint, profile.Fingerprint, StringComparison.Ordinal))
            {
                return;
            }
            throw new InvalidOperationException(
                $"AI model profile '{profile.Name}' is already registered with a different contract.");
        }
        _profiles.Add(profile.Name, profile);
    }

    public void AddStartupRequirement(
        string profileName,
        NhAiModelCapability requiredCapabilities)
    {
        _startupRequirements[profileName] =
            _startupRequirements.GetValueOrDefault(profileName) | requiredCapabilities;
    }
}
