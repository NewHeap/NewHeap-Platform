using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Every effect of the agent's tool calls becomes a proposal: the current
    /// <see cref="NhAiAgentInvocationScope"/> requires approval for effects. An agent with
    /// <see cref="NhAiAutonomyLevel.Propose"/> or <see cref="NhAiAutonomyLevel.Simulate"/>
    /// autonomy is then offered tools that change state as well, and each of its calls asks for
    /// approval instead of executing; <see cref="NhAiAutonomyLevel.Observe"/> and
    /// <see cref="NhAiAutonomyLevel.Explain"/> agents still only read. Creating the agent throws
    /// <see cref="InvalidOperationException"/> when no such scope is current.
    /// </summary>
    public bool EffectsAsProposals { get; init; }

    /// <summary>
    /// Creates the agent's <see cref="Microsoft.Agents.AI.ChatHistoryProvider"/>, which loads the
    /// earlier messages of a session before each run and stores the new ones after it, for example an
    /// <see cref="InMemoryChatHistoryProvider"/> with a chat reducer or a provider over the
    /// application's own store. The adapter calls it once per created agent with the service provider
    /// passed to <see cref="INhAiAgentFrameworkAdapter.CreateAsync"/>; a provider keeps per-session
    /// state in the session's <see cref="AgentSession.StateBag"/>, not in its own fields. Without a
    /// factory the agent keeps Agent Framework's default. Every model call still runs through the
    /// governed client, whatever history the provider supplies.
    /// </summary>
    public Func<IServiceProvider, ChatHistoryProvider>? ChatHistoryProviderFactory { get; init; }

    /// <summary>
    /// Whether a run fails with <see cref="InvalidOperationException"/> when the model service keeps the
    /// conversation itself, which it shows by returning a conversation id (as the OpenAI Responses API
    /// does), while the agent has a history provider from <see cref="ChatHistoryProviderFactory"/>. Sets
    /// Agent Framework's <see cref="ChatClientAgentOptions.ThrowOnChatHistoryProviderConflict"/> and keeps
    /// its default, <see langword="true"/>; the run fails after the governed model call, and the switch
    /// also governs a history provider given for one run while the service keeps the conversation.
    /// History the service keeps lives at the model provider, outside the application's retention and
    /// forgetting, so switch the error off only in a deployment whose conversation data may live there.
    /// Without the error, the session follows the service's conversation id: the run that received it
    /// has read the history provider but stores nothing in it, and later runs of that session neither
    /// read nor write it.
    /// </summary>
    public bool ThrowOnChatHistoryProviderConflict { get; init; } = true;

    /// <summary>
    /// Whether the conflict that <see cref="ThrowOnChatHistoryProviderConflict"/> describes logs a
    /// warning, on every run whose answer carries a conversation id. Sets Agent Framework's
    /// <see cref="ChatClientAgentOptions.WarnOnChatHistoryProviderConflict"/> and keeps its default,
    /// <see langword="true"/>. Agent Framework logs through the <c>ILoggerFactory</c> that the profile's
    /// model client offers through <see cref="IChatClient.GetService"/>, and many clients, such as
    /// OpenAI's, offer none. While the error is off, NewHeap therefore also logs one content-free warning
    /// of its own per created agent through the host's <c>ILoggerFactory</c>, naming only the agent and
    /// its version, the first time the service returns a conversation id.
    /// </summary>
    public bool WarnOnChatHistoryProviderConflict { get; init; } = true;

    /// <summary>
    /// Whether the conflict that <see cref="ThrowOnChatHistoryProviderConflict"/> describes removes the
    /// history provider from the agent, so that no later session of this agent instance reads or
    /// writes it. Sets Agent Framework's <see cref="ChatClientAgentOptions.ClearOnChatHistoryProviderConflict"/>
    /// and keeps its default, <see langword="true"/>. It takes effect only while the error is off. When
    /// it is off as well, the agent keeps the provider for sessions whose conversation the service does
    /// not keep.
    /// </summary>
    public bool ClearOnChatHistoryProviderConflict { get; init; } = true;

    /// <summary>
    /// Creates the agent's <see cref="AIContextProvider"/>s, which add instructions and messages before
    /// each run and see its result after it, such as memory or compaction of the conversation. The
    /// adapter calls it once per created agent with the service provider passed to
    /// <see cref="INhAiAgentFrameworkAdapter.CreateAsync"/>. Tools stay NewHeap's: a run whose context
    /// provider adds a tool fails with <see cref="InvalidOperationException"/> before the model is
    /// called, because only tools the adapter published pass NewHeap's discovery, autonomy and invoker.
    /// </summary>
    public Func<IServiceProvider, IEnumerable<AIContextProvider>>? AIContextProvidersFactory { get; init; }
}

/// <summary>
/// The failed result of a governed model call of an Agent Framework agent. The agent's chat client
/// can only answer or throw, so the adapter throws this exception for a failed
/// <see cref="INhAiChatExecutor"/> result, a stream that could not start or a stream whose completion
/// failed. <see cref="Code"/> keeps the result's failure code, such as
/// <see cref="NhAiModelFailureCodes.Timeout"/> when a governed deadline ended the call or
/// <see cref="NhAiModelFailureCodes.DependencyUnavailable"/>. The caller's own cancellation still
/// throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class NhAiAgentModelCallException : InvalidOperationException
{
    /// <summary>
    /// The code of a failed result that names none.
    /// </summary>
    public const string UnclassifiedCode = "ai-model-call-failed";

    public NhAiAgentModelCallException(string message, string code)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>
    /// The failure code of the governed call's result.
    /// </summary>
    public string Code { get; }

    internal static NhAiAgentModelCallException Of(TaskResult result, string message)
    {
        var code = result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        return new NhAiAgentModelCallException(message, code ?? UnclassifiedCode);
    }
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
    IEnumerable<INhAiToolCatalog> catalogs,
    ILoggerFactory? loggerFactory = null) : INhAiAgentFrameworkAdapter
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
                request.Descriptor.MaximumAutonomy,
                request.EffectsAsProposals))
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Version)
            .ToArray();
        var selectedIdentities = selectedDescriptors
            .Select(descriptor => $"{descriptor.Id}@{descriptor.Version}")
            .ToHashSet(StringComparer.Ordinal);
        var functions = new List<AITool>();
        foreach (var listed in catalogs)
        {
            // A catalog that changes at runtime is read through one view, so its descriptors and
            // functions belong to the same moment.
            var catalog = NhAiToolCatalogViews.Of(listed);
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
            },
            // A service that keeps the conversation and a history provider of the application's:
            // the request's switches map one to one onto Agent Framework's.
            ThrowOnChatHistoryProviderConflict = request.ThrowOnChatHistoryProviderConflict,
            WarnOnChatHistoryProviderConflict = request.WarnOnChatHistoryProviderConflict,
            ClearOnChatHistoryProviderConflict = request.ClearOnChatHistoryProviderConflict
        };
        if (request.ChatHistoryProviderFactory is { } historyFactory)
        {
            options.ChatHistoryProvider = historyFactory(services)
                ?? throw new InvalidOperationException(
                    "The chat history provider factory of an AI agent returned no provider.");
        }
        if (request.AIContextProvidersFactory is { } contextFactory)
        {
            var created = contextFactory(services)
                ?? throw new InvalidOperationException(
                    "The AI context provider factory of an AI agent returned no providers.");
            var providers = new List<AIContextProvider>();
            foreach (var provider in created)
            {
                if (provider is null)
                {
                    throw new InvalidOperationException(
                        "The AI context provider factory of an AI agent returned a null provider.");
                }

                // Each provider runs behind a guard that keeps tool publication NewHeap's.
                providers.Add(new NhAiAgentFrameworkContextProvider(provider));
            }
            if (providers.Count > 0)
            {
                options.AIContextProviders = providers;
            }
        }
        var effectiveContext = request.Context with
        {
            RemainingBudget = IntersectBudgets(
                request.Context.RemainingBudget,
                request.Descriptor.Budget)
        };
        // Agent Framework warns only through a logger factory the model client offers, so NewHeap warns
        // through the host's as well when the service keeps the conversation next to the provider.
        var conflictLogger = options.ChatHistoryProvider is not null
                             && !request.ThrowOnChatHistoryProviderConflict
                             && request.WarnOnChatHistoryProviderConflict
            ? loggerFactory?.CreateLogger<NhAiAgentFrameworkAdapter>()
            : null;
        var governedClient = new NhAiAgentFrameworkChatClient(
            chatExecutor,
            resolution.Data.Client,
            request.Descriptor,
            effectiveContext,
            request.DataClassification,
            request.ExecutionRegion,
            conflictLogger);
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
        if (request.EffectsAsProposals
            && (NhAiAgentInvocationScopes.Current is not { RequireApprovalForEffects: true } scope
                || !string.Equals(scope.AgentActorId, request.Context.ActorId, StringComparison.Ordinal)))
        {
            // Only the invoker's approval requirement turns an effect into a proposal; without it a
            // propose-level agent would execute.
            throw new InvalidOperationException(
                "Effects as proposals require a current agent invocation scope of this agent that requires approval for effects.");
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

    /// <summary>
    /// Reads need observe autonomy. Effects need execute autonomy, or propose autonomy when every
    /// effect becomes a proposal.
    /// </summary>
    private static bool IsWithinAutonomy(
        NhAiToolDescriptor descriptor,
        NhAiAutonomyLevel autonomy,
        bool effectsAsProposals)
    {
        if (descriptor.Effect == NhAiToolEffect.ReadOnly)
        {
            return autonomy >= NhAiAutonomyLevel.Observe;
        }

        return effectsAsProposals
            ? autonomy >= NhAiAutonomyLevel.Propose
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
    string executionRegion,
    ILogger? conflictLogger = null) : IChatClient
{
    private int _conflictWarned;

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
            throw NhAiAgentModelCallException.Of(
                result,
                "The governed AI agent model call did not complete successfully.");
        }
        WarnIfTheServiceKeepsTheConversation(result.Data.Response.ConversationId);
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
            throw NhAiAgentModelCallException.Of(
                start,
                "The governed AI agent model stream could not be started.");
        }

        await foreach (var update in start.Data.Updates.WithCancellation(cancellationToken))
        {
            WarnIfTheServiceKeepsTheConversation(update.ConversationId);
            yield return update;
        }
        var completion = await start.Data.Completion;
        if (!completion.Success)
        {
            throw NhAiAgentModelCallException.Of(
                completion,
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

    /// <summary>
    /// Logs once per agent that the model service keeps the conversation, so the agent's history provider
    /// is not used for that session. The warning names the agent and its version, never the conversation
    /// id or a message.
    /// </summary>
    private void WarnIfTheServiceKeepsTheConversation(string? conversationId)
    {
        if (conflictLogger is null
            || conversationId is null
            || Interlocked.Exchange(ref _conflictWarned, 1) == 1)
        {
            return;
        }

        conflictLogger.LogWarning(
            "AI agent {AgentId} version {AgentVersion}: the model service keeps the conversation, so the agent's chat history provider is not used for a session the service keeps.",
            descriptor.Id,
            descriptor.Version);
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

/// <summary>
/// Runs an application's <see cref="AIContextProvider"/> for a NewHeap agent. Its instructions and
/// messages reach the model, but a tool it adds would run outside NewHeap's discovery, autonomy and
/// invoker, so the run fails before the model is called.
/// </summary>
internal sealed class NhAiAgentFrameworkContextProvider(AIContextProvider inner) : AIContextProvider
{
    public override IReadOnlyList<string> StateKeys => inner.StateKeys;

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return base.GetService(serviceType, serviceKey) ?? inner.GetService(serviceType, serviceKey);
    }

    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        var offered = context.AIContext.Tools?.ToArray() ?? [];
        var provided = await inner.InvokingAsync(context, cancellationToken);
        foreach (var tool in provided.Tools ?? [])
        {
            if (!offered.Contains(tool))
            {
                throw new InvalidOperationException(
                    "An AI context provider of a governed AI agent may not add tools; publish tools through a governed NewHeap tool catalog.");
            }
        }

        return provided;
    }

    protected override ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        return inner.InvokedAsync(context, cancellationToken);
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
