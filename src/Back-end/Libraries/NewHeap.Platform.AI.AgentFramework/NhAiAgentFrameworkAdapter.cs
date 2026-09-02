using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AgentFramework;

public sealed record NhAiAgentCreateRequest(
    NhAiAgentDescriptor Descriptor,
    NhAiInvocationContext Context,
    string Instructions,
    string ExecutionRegion = "local")
{
    public NhAiDataClassification DataClassification { get; init; } =
        NhAiDataClassification.Internal;
}

public sealed record NhAiAgentInstance(
    AIAgent Agent,
    NhAiAgentDescriptor Descriptor,
    IReadOnlyList<NhAiToolDescriptor> Tools,
    IReadOnlyList<string> ModelDecisionTrace);

public interface INhAiAgentFrameworkAdapter
{
    Task<TaskResult<NhAiAgentInstance>> CreateAsync(
        NhAiAgentCreateRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken = default);
}

public static class NhAiAgentFrameworkServiceCollectionExtensions
{
    public static IServiceCollection AddNewHeapPlatformAIAgentFramework(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNewHeapPlatformAI();
        services.AddScoped<INhAiAgentFrameworkAdapter, NhAiAgentFrameworkAdapter>();
        services.AddScoped<
            INhAiAgentFrameworkWorkflowCheckpointAdapter,
            NhAiAgentFrameworkWorkflowCheckpointAdapter>();
        return services;
    }
}

public interface INhAiAgentFrameworkWorkflowCheckpointAdapter
{
    NhAiRunCheckpointReference CreateReference(
        string workflowId,
        int workflowVersion,
        CheckpointInfo checkpoint,
        int checkpointSchemaVersion,
        string stateHash,
        DateTimeOffset createdAt);

    bool IsCompatible(
        NhAiRunCheckpointReference reference,
        string workflowId,
        int workflowVersion,
        CheckpointInfo checkpoint,
        string stateHash);
}

internal sealed class NhAiAgentFrameworkWorkflowCheckpointAdapter :
    INhAiAgentFrameworkWorkflowCheckpointAdapter
{
    private const string AdapterId = "microsoft-agent-framework";

    public NhAiRunCheckpointReference CreateReference(
        string workflowId,
        int workflowVersion,
        CheckpointInfo checkpoint,
        int checkpointSchemaVersion,
        string stateHash,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return NhAiRunCheckpointReferenceFactory.Create(
            AdapterId,
            workflowId,
            workflowVersion,
            checkpoint.CheckpointId,
            checkpointSchemaVersion,
            stateHash,
            createdAt,
            checkpoint.SessionId);
    }

    public bool IsCompatible(
        NhAiRunCheckpointReference reference,
        string workflowId,
        int workflowVersion,
        CheckpointInfo checkpoint,
        string stateHash)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (stateHash is not { Length: 64 } || !stateHash.All(char.IsAsciiHexDigit))
        {
            return false;
        }
        var normalizedStateHash = stateHash.ToLowerInvariant();
        return string.Equals(reference.AdapterId, AdapterId, StringComparison.Ordinal)
            && string.Equals(reference.WorkflowId, workflowId, StringComparison.Ordinal)
            && reference.WorkflowVersion == workflowVersion
            && string.Equals(reference.CheckpointId, checkpoint.CheckpointId, StringComparison.Ordinal)
            && string.Equals(reference.SessionId, checkpoint.SessionId, StringComparison.Ordinal)
            && string.Equals(reference.StateHash, normalizedStateHash, StringComparison.Ordinal);
    }
}

internal sealed class NhAiAgentFrameworkAdapter(
    INhAiModelProfileResolver profileResolver,
    INhAiChatExecutor chatExecutor,
    INhAiToolDiscoveryService discoveryService,
    IEnumerable<INhAiToolCatalog> catalogs) : INhAiAgentFrameworkAdapter
{
    public async Task<TaskResult<NhAiAgentInstance>> CreateAsync(
        NhAiAgentCreateRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Descriptor);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(services);
        Validate(request);

        var resolution = await profileResolver.ResolveChatAsync(
            new NhAiModelResolutionRequest(
                request.Descriptor.ModelProfileName,
                request.Descriptor.RequiredModelCapabilities,
                request.DataClassification,
                request.Context.Purpose,
                request.ExecutionRegion),
            cancellationToken);
        if (!resolution.Success)
        {
            return TaskResult<NhAiAgentInstance>.Failed(resolution);
        }

        var discovered = await discoveryService.DiscoverAsync(
            new NhAiToolDiscoveryRequest(
                request.Context,
                NhAiToolExposure.Agent),
            cancellationToken);
        var selectedDescriptors = discovered
            .Where(descriptor => IsAllowed(
                descriptor,
                request.Descriptor.AllowedToolSelectors))
            .Where(descriptor => IsWithinAutonomy(
                descriptor,
                request.Descriptor.MaximumAutonomy))
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Version)
            .ToArray();
        var selectedIdentities = selectedDescriptors
            .Select(descriptor => $"{descriptor.Id}@{descriptor.Version}")
            .ToHashSet(StringComparer.Ordinal);
        var functions = new List<AITool>();
        foreach (var catalog in catalogs)
        {
            if (catalog.Governance != NhAiToolCatalogGovernance.SharedInvoker)
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalog.Manifest.CatalogId}' is not governed by INhAiToolInvoker.");
            }
            var descriptors = catalog.Descriptors;
            var catalogFunctions = catalog.CreateFunctions(services);
            if (descriptors.Count != catalogFunctions.Count)
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalog.Manifest.CatalogId}' returned a mismatched function set.");
            }
            for (var index = 0; index < descriptors.Count; index++)
            {
                var descriptor = descriptors[index];
                if (catalogFunctions[index] is not INhAiGovernedAIFunction governed
                    || !string.Equals(governed.Descriptor.Id, descriptor.Id, StringComparison.Ordinal)
                    || governed.Descriptor.Version != descriptor.Version
                    || !string.Equals(
                        governed.Descriptor.ContractHash,
                        descriptor.ContractHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AI catalog '{catalog.Manifest.CatalogId}' returned an ungoverned function for '{descriptor.Id}'.");
                }
                if (selectedIdentities.Contains($"{descriptor.Id}@{descriptor.Version}"))
                {
                    functions.Add(catalogFunctions[index]);
                }
            }
        }

        var options = new ChatClientAgentOptions
        {
            Id = $"{request.Descriptor.Id}-v{request.Descriptor.Version}",
            Name = request.Descriptor.Name,
            Description = request.Descriptor.Description,
            ChatOptions = new ChatOptions
            {
                Instructions = request.Instructions,
                Tools = functions
            }
        };
        var effectiveContext = request.Context with
        {
            RemainingBudget = IntersectBudgets(
                request.Context.RemainingBudget,
                request.Descriptor.Budget)
        };
        var governedClient = new NhAiAgentFrameworkChatClient(
            chatExecutor,
            resolution.Data.Client,
            request.Descriptor,
            effectiveContext,
            request.DataClassification,
            request.ExecutionRegion);
        var agent = new ChatClientAgent(
            governedClient,
            options,
            services: services);
        return TaskResult<NhAiAgentInstance>.Succeeded(
            new NhAiAgentInstance(
                agent,
                request.Descriptor,
                selectedDescriptors,
                resolution.Data.DecisionTrace));
    }

    private static void Validate(NhAiAgentCreateRequest request)
    {
        NhAiAgentFrameworkNames.ValidateSegment(request.Descriptor.Id, nameof(request.Descriptor.Id));
        NhAiAgentFrameworkNames.ValidateSegment(
            request.Descriptor.ModelProfileName,
            nameof(request.Descriptor.ModelProfileName));
        NhAiAgentFrameworkNames.ValidateSegment(
            request.Descriptor.EvaluationBaselineId,
            nameof(request.Descriptor.EvaluationBaselineId));
        if (request.Descriptor.Version < 1
            || request.Descriptor.Budget.MaxCalls < 1
            || request.Descriptor.Budget.MaxInputTokens < 1
            || request.Descriptor.Budget.MaxOutputTokens < 1
            || request.Descriptor.Budget.MaxEstimatedCost < 0
            || string.IsNullOrWhiteSpace(request.Descriptor.Name)
            || request.Descriptor.Name.Length > 128
            || string.IsNullOrWhiteSpace(request.Descriptor.Description)
            || request.Descriptor.Description.Length > 512
            || string.IsNullOrWhiteSpace(request.Instructions)
            || request.Instructions.Length > 32_768)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        if (request.Context.ActorKind != NhAiActorKind.Agent
            || string.IsNullOrWhiteSpace(request.Context.AccountableOwnerId)
            || !string.Equals(
                request.Context.AgentVersion,
                request.Descriptor.Version.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || !string.Equals(
                request.Context.ModelProfileName,
                request.Descriptor.ModelProfileName,
                StringComparison.Ordinal)
            || (request.Descriptor.PromptVersion is not null
                && !string.Equals(
                    request.Context.PromptVersion,
                    request.Descriptor.PromptVersion,
                    StringComparison.Ordinal))
            || (request.Descriptor.PromptHash is not null
                && !string.Equals(
                    request.Context.PromptHash,
                    request.Descriptor.PromptHash,
                    StringComparison.Ordinal))
            || (request.Descriptor.ContextPolicyId is not null
                && !string.Equals(
                    request.Context.ContextPolicyId,
                    request.Descriptor.ContextPolicyId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "AI agent context must identify the non-human actor, accountable owner, agent version, and model profile.");
        }
        if (request.Descriptor.AllowedToolSelectors.Count == 0
            || request.Descriptor.AllowedToolSelectors.Any(selector =>
                string.IsNullOrWhiteSpace(selector)
                || selector.Length > 128))
        {
            throw new ArgumentException(
                "AI agents require an explicit bounded tool allow-list.",
                nameof(request));
        }
    }

    private static bool IsAllowed(
        NhAiToolDescriptor descriptor,
        IEnumerable<string> selectors)
    {
        return selectors.Any(selector =>
            string.Equals(selector, descriptor.Id, StringComparison.Ordinal)
            || (selector.EndsWith(".*", StringComparison.Ordinal)
                && descriptor.Id.StartsWith(selector[..^1], StringComparison.Ordinal)));
    }

    private static bool IsWithinAutonomy(
        NhAiToolDescriptor descriptor,
        NhAiAutonomyLevel autonomy)
    {
        return descriptor.Effect == NhAiToolEffect.ReadOnly
            ? autonomy >= NhAiAutonomyLevel.Observe
            : autonomy >= NhAiAutonomyLevel.Execute;
    }

    private static NhAiModelBudget IntersectBudgets(
        NhAiModelBudget? contextBudget,
        NhAiModelBudget descriptorBudget)
    {
        if (contextBudget is null)
        {
            return descriptorBudget;
        }

        decimal? maximumCost = (contextBudget.MaxEstimatedCost, descriptorBudget.MaxEstimatedCost) switch
        {
            ({ } contextCost, { } descriptorCost) => Math.Min(contextCost, descriptorCost),
            ({ } contextCost, null) => contextCost,
            (null, { } descriptorCost) => descriptorCost,
            _ => null
        };
        return new NhAiModelBudget(
            Math.Min(contextBudget.MaxInputTokens, descriptorBudget.MaxInputTokens),
            Math.Min(contextBudget.MaxOutputTokens, descriptorBudget.MaxOutputTokens),
            Math.Min(contextBudget.MaxCalls, descriptorBudget.MaxCalls),
            maximumCost);
    }
}

internal sealed class NhAiAgentFrameworkChatClient(
    INhAiChatExecutor chatExecutor,
    IChatClient serviceClient,
    NhAiAgentDescriptor descriptor,
    NhAiInvocationContext context,
    NhAiDataClassification dataClassification,
    string executionRegion) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await chatExecutor.GetResponseAsync(
            CreateRequest(messages, options),
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "The governed AI agent model call did not complete successfully.");
        }
        return result.Data.Response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var start = await chatExecutor.StartStreamingAsync(
            CreateRequest(messages, options),
            cancellationToken);
        if (!start.Success)
        {
            throw new InvalidOperationException(
                "The governed AI agent model stream could not be started.");
        }

        await foreach (var update in start.Data.Updates.WithCancellation(cancellationToken))
        {
            yield return update;
        }
        var completion = await start.Data.Completion;
        if (!completion.Success)
        {
            throw new InvalidOperationException(
                "The governed AI agent model stream did not complete successfully.");
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }
        return serviceClient.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
    }

    private NhAiChatRequest CreateRequest(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options)
    {
        var boundedMessages = messages.Take(257).ToArray();
        if (boundedMessages.Length > 256)
        {
            throw new InvalidOperationException(
                "The governed AI agent model call exceeded the message-count limit.");
        }
        var inputCharacters = boundedMessages.Sum(message => (long)message.Text.Length)
            + (options?.Instructions?.Length ?? 0);
        var estimatedInputTokens = (int)Math.Min(
            int.MaxValue,
            Math.Max(1, (inputCharacters + 3) / 4));
        return new NhAiChatRequest(
            context,
            descriptor.ModelProfileName,
            descriptor.RequiredModelCapabilities,
            dataClassification,
            boundedMessages)
        {
            Options = options,
            EstimatedInputTokens = estimatedInputTokens,
            RequestedOutputTokens = descriptor.Budget.MaxOutputTokens,
            EstimatedCost = context.RemainingBudget?.MaxEstimatedCost,
            ExecutionRegion = executionRegion,
            AgentId = descriptor.Id
        };
    }
}

internal static class NhAiAgentFrameworkNames
{
    internal static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128
            || value[0] == '-'
            || value[^1] == '-'
            || value.Contains("--", StringComparison.Ordinal)
            || value.Any(character => character != '-'
                && (character < 'a' || character > 'z')
                && (character < '0' || character > '9')))
        {
            throw new ArgumentException(
                "Agent Framework identifiers must use bounded lowercase dash-case.",
                parameterName);
        }
    }
}
