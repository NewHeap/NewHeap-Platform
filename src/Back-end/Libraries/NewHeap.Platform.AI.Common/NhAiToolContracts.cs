using Microsoft.Extensions.AI;
using NewHeap.Platform.Common.Models;
using System.Text.Json;

namespace NewHeap.Platform.AI;

public enum NhAiToolEffect
{
    ReadOnly = 0,
    IdempotentMutation = 1,
    Mutation = 2,
    ExternalSideEffect = 3,
    Destructive = 4
}

public enum NhAiApprovalRequirement
{
    PolicyControlled = 0,
    Required = 1,
    NotRequired = 2
}

public enum NhAiIdempotencySupport
{
    None = 0,
    Supported = 1,
    Required = 2
}

public enum NhAiToolCatalogGovernance
{
    None = 0,
    SharedInvoker = 1
}

[Flags]
public enum NhAiToolExposure
{
    None = 0,
    Local = 1,
    Mcp = 2,
    Agent = 4
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NhAiToolSetAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public Type? JsonSerializerContextType { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NhAiToolAttribute(
    string id,
    int version,
    NhAiToolEffect effect,
    NhAiToolExposure exposure) : Attribute
{
    public string Id { get; } = id;
    public int Version { get; } = version;
    public NhAiToolEffect Effect { get; } = effect;
    public NhAiToolExposure Exposure { get; } = exposure;
    public NhAiApprovalRequirement Approval { get; set; } = NhAiApprovalRequirement.PolicyControlled;
    public NhAiIdempotencySupport Idempotency { get; set; } = NhAiIdempotencySupport.None;
    public string? VerifierId { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxConcurrency { get; set; } = 1;
    public int MaxInputBytes { get; set; } = 65_536;
    public int MaxResultBytes { get; set; } = 65_536;
    public NhAiDataClassification DataClassification { get; set; } = NhAiDataClassification.Internal;
    public NhAiRetentionCategory RetentionCategory { get; set; } = NhAiRetentionCategory.Operational;
    public string[] RequiredCapabilities { get; set; } = [];
}

public sealed record NhAiToolDescriptor(
    string Id,
    int Version,
    string Description,
    Type InputType,
    Type OutputType,
    NhAiToolEffect Effect,
    NhAiToolExposure Exposure,
    bool RequiresAuthorization,
    IReadOnlyList<string> AuthorizationPolicies)
{
    public string CatalogId { get; init; } = string.Empty;
    public int CatalogVersion { get; init; } = 1;
    public string DeclaringAssembly { get; init; } = string.Empty;
    public string InputSchemaJson { get; init; } = "{}";
    public string OutputSchemaJson { get; init; } = "{}";
    public string SchemaHash { get; init; } = string.Empty;
    public string ContractHash { get; init; } = string.Empty;
    public NhAiApprovalRequirement Approval { get; init; } = NhAiApprovalRequirement.PolicyControlled;
    public NhAiIdempotencySupport Idempotency { get; init; } = NhAiIdempotencySupport.None;
    public string? VerifierId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxConcurrency { get; init; } = 1;
    public int MaxInputBytes { get; init; } = 65_536;
    public int MaxResultBytes { get; init; } = 65_536;
    public NhAiDataClassification DataClassification { get; init; } = NhAiDataClassification.Internal;
    public NhAiRetentionCategory RetentionCategory { get; init; } = NhAiRetentionCategory.Operational;
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
}

public sealed record NhAiToolManifestEntry(
    string Id,
    int Version,
    string SchemaHash,
    string ContractHash);

public sealed record NhAiToolCatalogManifest(
    string CatalogId,
    int Version,
    string SchemaHash,
    IReadOnlyList<NhAiToolManifestEntry> Tools);

public sealed record NhAiInvocationContext(
    string ActorId,
    string Purpose,
    IReadOnlyDictionary<string, string> Scope)
{
    public Guid InvocationId { get; init; } = Guid.NewGuid();
    public NhAiActorKind ActorKind { get; init; } = NhAiActorKind.Human;
    public string? RunId { get; init; }
    public int? RunAttemptNumber { get; init; }
    public string? CorrelationId { get; init; }
    public string? AccountableOwnerId { get; init; }
    public IReadOnlyList<NhAiExecutionScopeEntry> ExecutionScopes { get; init; } = [];
    public IReadOnlySet<string> CapabilityGrants { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public string? ModelProfileName { get; init; }
    public string? PromptVersion { get; init; }
    public string? PromptHash { get; init; }
    public string? AgentVersion { get; init; }
    public string? CatalogVersion { get; init; }
    public string? CatalogHash { get; init; }
    public string? ContextHash { get; init; }
    public string? ContextPolicyId { get; init; }
    public string? ProposalId { get; init; }
    public string? ApprovalId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? FencingToken { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public NhAiModelBudget? RemainingBudget { get; init; }

    public bool TryGetScopeValue(string key, out string value)
    {
        return Scope.TryGetValue(key, out value!);
    }
}

public sealed record NhAiExecutionScopeEntry(
    string Type,
    string Id,
    string? DisplayName = null);

public interface INhAiToolCatalog
{
    NhAiToolCatalogGovernance Governance { get; }

    IReadOnlyList<NhAiToolDescriptor> Descriptors { get; }

    NhAiToolCatalogManifest Manifest { get; }

    IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services);
}

public interface INhAiGovernedAIFunction
{
    NhAiToolDescriptor Descriptor { get; }
}

public sealed class NhAiGovernedAIFunction : AIFunction, INhAiGovernedAIFunction
{
    private readonly AIFunction _inner;

    private NhAiGovernedAIFunction(
        NhAiToolDescriptor descriptor,
        AIFunction inner)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(inner);
        Descriptor = descriptor;
        _inner = inner;
    }

    public NhAiToolDescriptor Descriptor { get; }

    public override string Name => _inner.Name;

    public override string Description => _inner.Description;

    public override JsonElement JsonSchema => _inner.JsonSchema;

    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;

    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

    public static AIFunction Create(
        NhAiToolDescriptor descriptor,
        AIFunction governedFunction)
    {
        return new NhAiGovernedAIFunction(descriptor, governedFunction);
    }

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        return _inner.InvokeAsync(arguments, cancellationToken);
    }
}

public interface INhAiToolInvocationGate
{
    ValueTask<TaskResult<NhAiInvocationContext>> AuthorizeAsync(
        NhAiToolDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

public interface INhAiToolInvoker
{
    Task<TaskResult<T>> InvokeAsync<T>(
        NhAiToolDescriptor descriptor,
        Func<NhAiInvocationContext, CancellationToken, Task<TaskResult<T>>> invocation,
        CancellationToken cancellationToken = default);

    Task<TaskResult<T>> InvokeAsync<T>(
        NhAiToolDescriptor descriptor,
        object arguments,
        Func<NhAiInvocationContext, CancellationToken, Task<TaskResult<T>>> invocation,
        CancellationToken cancellationToken = default);
}
