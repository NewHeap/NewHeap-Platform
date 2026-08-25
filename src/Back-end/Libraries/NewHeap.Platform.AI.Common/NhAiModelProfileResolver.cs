using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

internal sealed class NhAiModelProfileRegistry(
    NhAiRegistrationState state) : INhAiModelProfileRegistry
{
    public IReadOnlyCollection<NhAiModelProfile> Profiles => state.Profiles;

    public bool TryGet(string name, out NhAiModelProfile profile)
    {
        profile = state.Profiles.FirstOrDefault(
            candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))!;
        return profile is not null;
    }
}

internal sealed class NhAiModelProfileResolver(
    IServiceProvider serviceProvider,
    INhAiModelProfileRegistry registry) : INhAiModelProfileResolver
{
    public Task<TaskResult<NhAiResolvedChatProfile>> ResolveChatAsync(
        NhAiModelResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileName);
        NhAiNames.ValidateSegment(request.Purpose, nameof(request.Purpose));
        cancellationToken.ThrowIfCancellationRequested();

        if (!registry.TryGet(request.ProfileName, out var requestedProfile))
        {
            return Task.FromResult(
                TaskResult<NhAiResolvedChatProfile>.Failed(
                    $"AI model profile '{request.ProfileName}' is not registered."));
        }

        var trace = new List<string>(16);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new Queue<NhAiModelProfile>();
        candidates.Enqueue(requestedProfile);

        while (candidates.Count > 0 && trace.Count < 16)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = candidates.Dequeue();
            if (!visited.Add(profile.Name))
            {
                continue;
            }

            if ((profile.Capabilities & request.RequiredCapabilities) != request.RequiredCapabilities)
            {
                trace.Add($"profile:{profile.Name}:capability-mismatch");
                EnqueueFallbacks(profile, candidates, trace);
                continue;
            }
            if (!profile.PermittedDataClassifications.Contains(request.DataClassification))
            {
                trace.Add($"profile:{profile.Name}:classification-denied");
                EnqueueFallbacks(profile, candidates, trace);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(request.ExecutionRegion)
                && !profile.PermittedExecutionRegions.Contains(request.ExecutionRegion))
            {
                trace.Add($"profile:{profile.Name}:region-denied");
                EnqueueFallbacks(profile, candidates, trace);
                continue;
            }

            var client = serviceProvider.GetKeyedService<IChatClient>(profile.KeyedClientKey);
            if (client is null)
            {
                trace.Add($"profile:{profile.Name}:client-unavailable");
                EnqueueFallbacks(profile, candidates, trace);
                continue;
            }

            trace.Add($"profile:{profile.Name}:selected");
            return Task.FromResult(
                TaskResult<NhAiResolvedChatProfile>.Succeeded(
                    new NhAiResolvedChatProfile(profile, client, trace.ToArray())));
        }

        return Task.FromResult(
            TaskResult<NhAiResolvedChatProfile>.Failed(
                "No registered AI model profile satisfies the requested policy and capability constraints."));
    }

    public Task<TaskResult<NhAiResolvedEmbeddingProfile>> ResolveEmbeddingsAsync(
        NhAiModelResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var required = request with
        {
            RequiredCapabilities = request.RequiredCapabilities | NhAiModelCapability.Embeddings
        };
        return ResolveAsync(
            required,
            profile => serviceProvider.GetKeyedService<
                IEmbeddingGenerator<string, Embedding<float>>>(profile.KeyedClientKey),
            (profile, generator, trace) => new NhAiResolvedEmbeddingProfile(
                profile,
                generator,
                trace),
            "embedding generator",
            cancellationToken);
    }

    private Task<TaskResult<TResult>> ResolveAsync<TClient, TResult>(
        NhAiModelResolutionRequest request,
        Func<NhAiModelProfile, TClient?> resolveClient,
        Func<NhAiModelProfile, TClient, IReadOnlyList<string>, TResult> createResult,
        string clientKind,
        CancellationToken cancellationToken)
        where TClient : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileName);
        NhAiNames.ValidateSegment(request.Purpose, nameof(request.Purpose));
        cancellationToken.ThrowIfCancellationRequested();
        if (!registry.TryGet(request.ProfileName, out var requestedProfile))
        {
            return Task.FromResult(TaskResult<TResult>.Failed(
                $"AI model profile '{request.ProfileName}' is not registered."));
        }

        var trace = new List<string>(16);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new Queue<NhAiModelProfile>();
        candidates.Enqueue(requestedProfile);
        while (candidates.Count > 0 && trace.Count < 16)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = candidates.Dequeue();
            if (!visited.Add(profile.Name))
            {
                continue;
            }
            if ((profile.Capabilities & request.RequiredCapabilities) != request.RequiredCapabilities)
            {
                trace.Add($"profile:{profile.Name}:capability-mismatch");
                EnqueueFallbacks(profile, candidates, trace);
                continue;
            }
            if (!profile.PermittedDataClassifications.Contains(request.DataClassification))
            {
                trace.Add($"profile:{profile.Name}:classification-denied");
                EnqueueFallbacks(profile, candidates, trace);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(request.ExecutionRegion)
                && !profile.PermittedExecutionRegions.Contains(request.ExecutionRegion))
            {
                trace.Add($"profile:{profile.Name}:region-denied");
                EnqueueFallbacks(profile, candidates, trace);
                continue;
            }
            var client = resolveClient(profile);
            if (client is null)
            {
                trace.Add($"profile:{profile.Name}:{clientKind.Replace(' ', '-')}-unavailable");
                EnqueueFallbacks(profile, candidates, trace);
                continue;
            }

            trace.Add($"profile:{profile.Name}:selected");
            return Task.FromResult(TaskResult<TResult>.Succeeded(
                createResult(profile, client, trace.ToArray())));
        }

        return Task.FromResult(TaskResult<TResult>.Failed(
            "No registered AI model profile satisfies the requested policy and capability constraints."));
    }

    private void EnqueueFallbacks(
        NhAiModelProfile profile,
        Queue<NhAiModelProfile> candidates,
        ICollection<string> trace)
    {
        foreach (var fallbackName in profile.FallbackProfileNames)
        {
            if (registry.TryGet(fallbackName, out var fallback))
            {
                candidates.Enqueue(fallback);
            }
            else
            {
                trace.Add($"profile:{profile.Name}:fallback-missing");
            }
        }
    }
}

internal sealed class NhAiStartupValidator(
    IServiceScopeFactory serviceScopeFactory,
    INhAiModelProfileRegistry registry,
    NhAiRegistrationState registrationState,
    IEnumerable<INhAiToolCatalog> catalogs) : IHostedService
{
    private readonly IReadOnlyList<INhAiToolCatalog> _catalogs = catalogs.ToArray();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var requirement in registrationState.StartupRequirements)
        {
            if (!registry.TryGet(requirement.Key, out var requiredProfile))
            {
                throw new InvalidOperationException(
                    $"Required AI model profile '{requirement.Key}' is not registered.");
            }
            if ((requiredProfile.Capabilities & requirement.Value) != requirement.Value)
            {
                throw new InvalidOperationException(
                    $"Required AI model profile '{requirement.Key}' does not declare all required capabilities '{requirement.Value}'.");
            }
        }

        using var scope = serviceScopeFactory.CreateScope();
        foreach (var profile in registry.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chatCapabilities = NhAiModelCapability.Chat
                | NhAiModelCapability.FunctionCalling
                | NhAiModelCapability.StructuredOutput
                | NhAiModelCapability.Streaming
                | NhAiModelCapability.Vision;
            if ((profile.Capabilities & chatCapabilities) != 0
                && scope.ServiceProvider.GetKeyedService<IChatClient>(profile.KeyedClientKey) is null)
            {
                throw new InvalidOperationException(
                    $"AI model profile '{profile.Name}' references an unregistered keyed IChatClient.");
            }
            if ((profile.Capabilities & NhAiModelCapability.Embeddings) != 0
                && scope.ServiceProvider.GetKeyedService<
                    IEmbeddingGenerator<string, Embedding<float>>>(profile.KeyedClientKey) is null)
            {
                throw new InvalidOperationException(
                    $"AI model profile '{profile.Name}' references an unregistered keyed embedding generator.");
            }
            foreach (var fallback in profile.FallbackProfileNames)
            {
                if (!registry.TryGet(fallback, out _))
                {
                    throw new InvalidOperationException(
                        $"AI model profile '{profile.Name}' references missing fallback profile '{fallback}'.");
                }
            }
        }

        foreach (var profile in registry.Profiles)
        {
            ValidateFallbackCycle(profile, [], []);
        }

        ValidateToolRuntime(scope.ServiceProvider);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void ValidateFallbackCycle(
        NhAiModelProfile profile,
        HashSet<string> visiting,
        HashSet<string> validated)
    {
        if (validated.Contains(profile.Name))
        {
            return;
        }
        if (!visiting.Add(profile.Name))
        {
            throw new InvalidOperationException(
                $"AI model profile fallback cycle detected at '{profile.Name}'.");
        }

        foreach (var fallbackName in profile.FallbackProfileNames)
        {
            if (registry.TryGet(fallbackName, out var fallback))
            {
                ValidateFallbackCycle(fallback, visiting, validated);
            }
        }
        visiting.Remove(profile.Name);
        validated.Add(profile.Name);
    }

    private void ValidateToolRuntime(IServiceProvider serviceProvider)
    {
        var verifiers = new Dictionary<string, INhAiToolVerifier>(StringComparer.Ordinal);
        foreach (var verifier in serviceProvider.GetServices<INhAiToolVerifier>())
        {
            NhAiNames.ValidateSegment(verifier.Id, nameof(INhAiToolVerifier.Id));
            if (!verifiers.TryAdd(verifier.Id, verifier))
            {
                throw new InvalidOperationException(
                    $"AI tool verifier '{verifier.Id}' is registered more than once.");
            }
        }

        var descriptors = new HashSet<string>(StringComparer.Ordinal);
        var idempotencyManager = serviceProvider.GetRequiredService<INhAiIdempotencyManager>();
        var budgetManager = serviceProvider.GetRequiredService<INhAiBudgetManager>();
        if ((registry.Profiles.Count > 0 || _catalogs.Count > 0)
            && budgetManager is NhAiDenyBudgetManager)
        {
            throw new InvalidOperationException(
                "AI model profiles and tool catalogs require a configured budget manager.");
        }

        foreach (var catalog in _catalogs)
        {
            if (catalog.Governance != NhAiToolCatalogGovernance.SharedInvoker)
            {
                throw new InvalidOperationException(
                    $"AI tool catalog '{catalog.Manifest.CatalogId}' is not governed by INhAiToolInvoker.");
            }

            foreach (var descriptor in catalog.Descriptors)
            {
                var identity = $"{descriptor.Id}@{descriptor.Version}";
                if (!descriptors.Add(identity))
                {
                    throw new InvalidOperationException(
                        $"AI tool descriptor '{identity}' is registered more than once.");
                }
                if (descriptor.Timeout <= TimeSpan.Zero
                    || descriptor.MaxConcurrency < 1
                    || descriptor.MaxInputBytes < 1
                    || descriptor.MaxResultBytes < 1)
                {
                    throw new InvalidOperationException(
                        $"AI tool descriptor '{identity}' declares invalid execution bounds.");
                }
                if (descriptor.Effect != NhAiToolEffect.ReadOnly
                    && descriptor.Idempotency != NhAiIdempotencySupport.Required)
                {
                    throw new InvalidOperationException(
                        $"AI tool descriptor '{identity}' must require idempotency for side effects.");
                }
                var requiresApproval = descriptor.Effect is NhAiToolEffect.Mutation
                    or NhAiToolEffect.ExternalSideEffect
                    or NhAiToolEffect.Destructive;
                if (requiresApproval
                    && descriptor.Approval != NhAiApprovalRequirement.Required)
                {
                    throw new InvalidOperationException(
                        $"AI tool descriptor '{identity}' must require approval for its side effect.");
                }
                if (descriptor.Effect == NhAiToolEffect.Destructive
                    && string.IsNullOrWhiteSpace(descriptor.VerifierId))
                {
                    throw new InvalidOperationException(
                        $"AI tool descriptor '{identity}' must declare a verifier for destructive effects.");
                }
                if (descriptor.Idempotency == NhAiIdempotencySupport.Required
                    && idempotencyManager is NhAiDenyIdempotencyManager)
                {
                    throw new InvalidOperationException(
                        $"AI tool descriptor '{identity}' requires a configured idempotency manager.");
                }
                if (!string.IsNullOrWhiteSpace(descriptor.VerifierId)
                    && !verifiers.ContainsKey(descriptor.VerifierId))
                {
                    throw new InvalidOperationException(
                        $"AI tool descriptor '{identity}' references unregistered verifier '{descriptor.VerifierId}'.");
                }
            }
        }
    }
}
