namespace NewHeap.Platform.AI;

public sealed record NhAiInvocationContextSeed(
    string ActorId,
    string Purpose,
    string? AccountableOwnerId = null,
    IReadOnlyDictionary<string, string>? Scope = null,
    NhAiActorKind ActorKind = NhAiActorKind.Human);

public interface INhAiInvocationContextContributor
{
    int Order { get; }

    ValueTask ContributeAsync(
        NhAiInvocationContextBuilder builder,
        CancellationToken cancellationToken = default);
}

public interface INhAiInvocationContextFactory
{
    ValueTask<NhAiInvocationContext> CreateAsync(
        NhAiInvocationContextSeed seed,
        CancellationToken cancellationToken = default);
}

public sealed class NhAiInvocationContextBuilder
{
    private const int MaxScopeEntries = 64;
    private const int MaxScopeValueLength = 256;
    private readonly Dictionary<string, string> _scope = new(StringComparer.Ordinal);
    private readonly List<NhAiExecutionScopeEntry> _executionScopes = [];
    private readonly HashSet<string> _capabilityGrants = new(StringComparer.Ordinal);

    internal NhAiInvocationContextBuilder(NhAiInvocationContextSeed seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.ActorId);
        NhAiNames.ValidateSegment(seed.Purpose, nameof(seed.Purpose));
        if (seed.ActorId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(seed.ActorId));
        }
        ActorId = seed.ActorId;
        ActorKind = seed.ActorKind;
        Purpose = seed.Purpose;
        AccountableOwnerId = seed.AccountableOwnerId;
        foreach (var item in seed.Scope ?? new Dictionary<string, string>())
        {
            SetScopeValue(item.Key, item.Value);
        }
    }

    public string ActorId { get; }
    public NhAiActorKind ActorKind { get; }
    public string Purpose { get; }
    public string? AccountableOwnerId { get; }
    public string? RunId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ModelProfileName { get; set; }
    public string? PromptVersion { get; set; }
    public string? PromptHash { get; set; }
    public string? AgentVersion { get; set; }
    public string? CatalogVersion { get; set; }
    public string? CatalogHash { get; set; }
    public string? ContextHash { get; set; }
    public string? ContextPolicyId { get; set; }
    public string? ProposalId { get; set; }
    public string? ApprovalId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? FencingToken { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public NhAiModelBudget? RemainingBudget { get; set; }

    public NhAiInvocationContextBuilder SetScopeValue(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Length > 64 || value.Length > MaxScopeValueLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "AI invocation scope entries must be bounded.");
        }
        if (!_scope.ContainsKey(key) && _scope.Count >= MaxScopeEntries)
        {
            throw new InvalidOperationException("The AI invocation scope contains too many entries.");
        }
        _scope[key] = value;
        return this;
    }

    public NhAiInvocationContextBuilder AddExecutionScope(
        string type,
        string id,
        string? displayName = null)
    {
        NhAiNames.ValidateSegment(type, nameof(type));
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (_executionScopes.Count >= MaxScopeEntries)
        {
            throw new InvalidOperationException("The AI invocation context contains too many execution scopes.");
        }
        _executionScopes.Add(new NhAiExecutionScopeEntry(type, id, displayName));
        return this;
    }

    public NhAiInvocationContextBuilder GrantCapability(string capability)
    {
        NhAiNames.ValidateSegment(capability, nameof(capability));
        _capabilityGrants.Add(capability);
        return this;
    }

    internal NhAiInvocationContext Build()
    {
        return new NhAiInvocationContext(ActorId, Purpose, new Dictionary<string, string>(_scope))
        {
            ActorKind = ActorKind,
            RunId = RunId,
            CorrelationId = CorrelationId,
            AccountableOwnerId = AccountableOwnerId,
            ExecutionScopes = _executionScopes.ToArray(),
            CapabilityGrants = _capabilityGrants.ToHashSet(StringComparer.Ordinal),
            ModelProfileName = ModelProfileName,
            PromptVersion = PromptVersion,
            PromptHash = PromptHash,
            AgentVersion = AgentVersion,
            CatalogVersion = CatalogVersion,
            CatalogHash = CatalogHash,
            ContextHash = ContextHash,
            ContextPolicyId = ContextPolicyId,
            ProposalId = ProposalId,
            ApprovalId = ApprovalId,
            IdempotencyKey = IdempotencyKey,
            FencingToken = FencingToken,
            Deadline = Deadline,
            RemainingBudget = RemainingBudget
        };
    }
}

internal sealed class NhAiInvocationContextFactory(
    IEnumerable<INhAiInvocationContextContributor> contributors) : INhAiInvocationContextFactory
{
    public async ValueTask<NhAiInvocationContext> CreateAsync(
        NhAiInvocationContextSeed seed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var builder = new NhAiInvocationContextBuilder(seed);
        foreach (var contributor in contributors.OrderBy(item => item.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await contributor.ContributeAsync(builder, cancellationToken);
        }
        return builder.Build();
    }
}
