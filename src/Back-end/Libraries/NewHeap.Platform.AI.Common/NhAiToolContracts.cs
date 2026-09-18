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
    NotRequired = 2,

    /// <summary>
    /// Approval is required, but the consuming application validates its own
    /// authoritative approval evidence inside the governed invocation and returns
    /// its typed domain outcome. The invoker records the consumer-attested step in
    /// the audit record instead of demanding a Platform proposal and approval.
    /// </summary>
    ConsumerAuthoritative = 3,

    /// <summary>
    /// The invocation issues an approval artifact, such as a single-use grant, under the
    /// consuming application's own write authorization instead of consuming one. The
    /// invoker still authorizes, resolves capabilities, budgets, bounds and audits it and
    /// records the approval code <c>issuer</c>. Issuers cannot be destructive and cannot
    /// request a Platform idempotency lease.
    /// </summary>
    Issuer = 4
}

/// <summary>
/// The role a tool plays in an approval flow, derived from <see cref="NhAiApprovalRequirement"/>.
/// </summary>
public enum NhAiApprovalRole
{
    None = 0,
    Consumer = 1,
    Issuer = 2
}

/// <summary>
/// An explicit MCP tool annotation hint. <see cref="Inherit"/> publishes the value the
/// governance effect implies; an explicit value may only make the published hint more
/// cautious than the effect, never less.
/// </summary>
public enum NhAiToolHint
{
    Inherit = 0,
    True = 1,
    False = 2
}

public enum NhAiIdempotencySupport
{
    None = 0,
    Supported = 1,
    Required = 2,

    /// <summary>
    /// The consuming application owns idempotency inside the governed invocation:
    /// a replayed key reaches the application engine, which reconciles and returns
    /// its receipt. The invoker records the consumer-attested step instead of
    /// acquiring a Platform idempotency lease.
    /// </summary>
    ConsumerAuthoritative = 3
}

/// <summary>
/// Describes the wire shape of a generated tool when it is exported through MCP.
/// </summary>
public enum NhAiToolExportSchema
{
    /// <summary>
    /// Arguments travel in the <c>input</c> envelope and results in the
    /// <c>TaskResult&lt;T&gt;</c> envelope.
    /// </summary>
    Enveloped = 0,

    /// <summary>
    /// The input type's properties are the top-level arguments and the output
    /// type is the structured result. A failed result with typed data publishes
    /// that data as the structured error payload.
    /// </summary>
    Flat = 1
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
    public NhAiToolExportSchema ExportSchema { get; set; } = NhAiToolExportSchema.Enveloped;

    /// <summary>
    /// Overrides the MCP <c>readOnlyHint</c>. Only <see cref="NhAiToolHint.False"/> can differ
    /// from a read-only effect; claiming read-only for any other effect is rejected.
    /// </summary>
    public NhAiToolHint ReadOnlyHint { get; set; } = NhAiToolHint.Inherit;

    /// <summary>
    /// Overrides the MCP <c>destructiveHint</c>. Any non-destructive effect may publish
    /// <see cref="NhAiToolHint.True"/>; a destructive effect cannot publish false.
    /// </summary>
    public NhAiToolHint DestructiveHint { get; set; } = NhAiToolHint.Inherit;

    /// <summary>
    /// Overrides the MCP <c>idempotentHint</c>. Any effect may publish
    /// <see cref="NhAiToolHint.False"/>; only read-only and idempotent-mutation effects may
    /// publish true.
    /// </summary>
    public NhAiToolHint IdempotentHint { get; set; } = NhAiToolHint.Inherit;

    /// <summary>
    /// Overrides the MCP <c>openWorldHint</c>. Any effect may publish
    /// <see cref="NhAiToolHint.True"/>; an external side effect cannot publish false.
    /// </summary>
    public NhAiToolHint OpenWorldHint { get; set; } = NhAiToolHint.Inherit;

    public string? VerifierId { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxConcurrency { get; set; } = 1;
    public int MaxInputBytes { get; set; } = 65_536;
    public int MaxResultBytes { get; set; } = 65_536;
    public NhAiDataClassification DataClassification { get; set; } = NhAiDataClassification.Internal;
    public NhAiRetentionCategory RetentionCategory { get; set; } = NhAiRetentionCategory.Operational;
    public string[] RequiredCapabilities { get; set; } = [];
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NhAiToolExportNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
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
    public string ExportName { get; init; } = string.Empty;
    public string CatalogId { get; init; } = string.Empty;
    public int CatalogVersion { get; init; } = 1;
    public string DeclaringAssembly { get; init; } = string.Empty;
    public string InputSchemaJson { get; init; } = "{}";
    public string OutputSchemaJson { get; init; } = "{}";
    public string SchemaHash { get; init; } = string.Empty;
    public string ContractHash { get; init; } = string.Empty;
    public NhAiApprovalRequirement Approval { get; init; } = NhAiApprovalRequirement.PolicyControlled;
    public NhAiIdempotencySupport Idempotency { get; init; } = NhAiIdempotencySupport.None;
    public NhAiToolExportSchema ExportSchema { get; init; } = NhAiToolExportSchema.Enveloped;
    public NhAiToolHint ReadOnlyHint { get; init; } = NhAiToolHint.Inherit;
    public NhAiToolHint DestructiveHint { get; init; } = NhAiToolHint.Inherit;
    public NhAiToolHint IdempotentHint { get; init; } = NhAiToolHint.Inherit;
    public NhAiToolHint OpenWorldHint { get; init; } = NhAiToolHint.Inherit;
    public string? VerifierId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxConcurrency { get; init; } = 1;
    public int MaxInputBytes { get; init; } = 65_536;
    public int MaxResultBytes { get; init; } = 65_536;
    public NhAiDataClassification DataClassification { get; init; } = NhAiDataClassification.Internal;
    public NhAiRetentionCategory RetentionCategory { get; init; } = NhAiRetentionCategory.Operational;
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>
    /// The approval role this tool declares: consuming approval, issuing approval, or neither.
    /// </summary>
    public NhAiApprovalRole ApprovalRole => Approval switch
    {
        NhAiApprovalRequirement.Required or NhAiApprovalRequirement.ConsumerAuthoritative => NhAiApprovalRole.Consumer,
        NhAiApprovalRequirement.Issuer => NhAiApprovalRole.Issuer,
        _ => NhAiApprovalRole.None
    };
}

public sealed record NhAiToolManifestEntry(
    string Id,
    int Version,
    string SchemaHash,
    string ContractHash)
{
    public string ExportName { get; init; } = string.Empty;
}

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
    public string? Issuer { get; init; }
    public string? Subject { get; init; }
    public string? TenantId { get; init; }
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

public interface INhAiGeneratedToolCatalog : INhAiToolCatalog
{
}

/// <summary>
/// A catalog whose descriptors and governed functions are created at startup (not by the
/// source generator) and that attests <see cref="NhAiToolCatalogGovernance.SharedInvoker"/>
/// governance. Attested catalogs may enter the MCP export path after
/// <see cref="NhAiToolCatalogAttestation.Validate"/> succeeded at startup.
/// </summary>
public interface INhAiAttestedToolCatalog : INhAiToolCatalog
{
    /// <summary>
    /// SHA-256 over the ordered descriptor contract hashes; must equal
    /// <see cref="NhAiToolCatalogManifest.SchemaHash"/>. The hashed material is one
    /// <c>id@version:contractHash</c> line per descriptor, ordered by id and version and
    /// joined with a line feed, written as lowercase hexadecimal: the same material the
    /// source generator uses for a generated catalog hash.
    /// </summary>
    string AttestationHash { get; }
}

/// <summary>
/// Validates that a runtime catalog is governed by the shared invoker before it is exported.
/// </summary>
public static class NhAiToolCatalogAttestation
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the catalog is not
    /// SharedInvoker-governed, when a created function is not an
    /// <see cref="INhAiGovernedAIFunction"/> bound to one of its descriptors, or when the
    /// attestation hash does not match the manifest.
    /// </summary>
    public static void Validate(INhAiToolCatalog catalog, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(services);

        var catalogId = catalog.Manifest?.CatalogId ?? catalog.GetType().Name;
        if (catalog.Governance != NhAiToolCatalogGovernance.SharedInvoker)
        {
            throw new InvalidOperationException(
                $"AI catalog '{catalogId}' is not governed by INhAiToolInvoker.");
        }

        var descriptors = catalog.Descriptors;
        var byIdentity = new Dictionary<string, NhAiToolDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (!byIdentity.TryAdd(Identity(descriptor.Id, descriptor.Version), descriptor))
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalogId}' declares descriptor '{Identity(descriptor.Id, descriptor.Version)}' more than once.");
            }
        }

        var functions = catalog.CreateFunctions(services);
        if (functions.Count != descriptors.Count)
        {
            throw new InvalidOperationException(
                $"AI catalog '{catalogId}' returned a descriptor/function count mismatch.");
        }

        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            if (function is not INhAiGovernedAIFunction governed)
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalogId}' returned ungoverned function '{function.Name}'.");
            }

            var identity = Identity(governed.Descriptor.Id, governed.Descriptor.Version);
            if (!byIdentity.TryGetValue(identity, out var descriptor)
                || !string.Equals(
                    governed.Descriptor.ContractHash,
                    descriptor.ContractHash,
                    StringComparison.Ordinal)
                || !string.Equals(function.Name, descriptor.ExportName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalogId}' returned function '{function.Name}' that is not bound to one of its descriptors.");
            }
            if (!bound.Add(identity))
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalogId}' returned more than one function for '{identity}'.");
            }
        }

        if (catalog is not INhAiAttestedToolCatalog attested)
        {
            return;
        }

        var expectedHash = ComputeAttestationHash(descriptors);
        if (!string.Equals(attested.AttestationHash, expectedHash, StringComparison.Ordinal)
            || !string.Equals(catalog.Manifest?.SchemaHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AI catalog '{catalogId}' attestation hash does not match its descriptors and manifest.");
        }

        var manifestTools = catalog.Manifest!.Tools;
        foreach (var entry in manifestTools)
        {
            if (!byIdentity.TryGetValue(Identity(entry.Id, entry.Version), out var descriptor)
                || !string.Equals(entry.ContractHash, descriptor.ContractHash, StringComparison.Ordinal)
                || !string.Equals(entry.ExportName, descriptor.ExportName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AI catalog '{catalogId}' manifest entry '{Identity(entry.Id, entry.Version)}' does not match its descriptors.");
            }
        }
        if (manifestTools.Count != descriptors.Count)
        {
            throw new InvalidOperationException(
                $"AI catalog '{catalogId}' manifest entries do not match its descriptors.");
        }
    }

    private static string ComputeAttestationHash(IEnumerable<NhAiToolDescriptor> descriptors)
    {
        var material = string.Join(
            "\n",
            descriptors
                .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
                .ThenBy(descriptor => descriptor.Version)
                .Select(descriptor => Identity(descriptor.Id, descriptor.Version) + ":" + descriptor.ContractHash));
        var bytes = System.Text.Encoding.UTF8.GetBytes(material);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static string Identity(string id, int version)
    {
        return id + "@" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

public interface INhAiGovernedAIFunction
{
    NhAiToolDescriptor Descriptor { get; }
}

/// <summary>
/// Wraps the function of one governed descriptor. Arguments are normalized to the shared
/// <c>input</c> envelope before binding: when the function's schema has <c>input</c> as its only
/// property and the caller sent the input properties at the top level instead, the whole argument
/// object becomes <c>input</c>. Nothing is merged or dropped: <c>input</c> next to other
/// top-level properties, or arguments that cannot be bound, return the structured failure
/// <see cref="NhAiToolFailureCodes.InputInvalid"/> without executing the tool. Normalized
/// arguments still pass through the function's own binding and the shared invoker.
/// </summary>
public sealed class NhAiGovernedAIFunction : AIFunction, INhAiGovernedAIFunction
{
    private readonly AIFunction _inner;
    private readonly IServiceProvider? _services;
    private readonly bool _enveloped;

    private NhAiGovernedAIFunction(
        NhAiToolDescriptor descriptor,
        AIFunction inner,
        IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(inner);
        Descriptor = descriptor;
        _inner = inner;
        _services = services;
        _enveloped = NhAiToolArguments.HasInputEnvelope(inner.JsonSchema);
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
        return new NhAiGovernedAIFunction(descriptor, governedFunction, null);
    }

    /// <summary>
    /// Creates the governed function with the services of the catalog scope. They are used to
    /// audit a rejected argument shape through the registered <see cref="INhAiAuditSink"/>
    /// instances; without them the function falls back to <see cref="AIFunctionArguments.Services"/>.
    /// </summary>
    public static AIFunction Create(
        NhAiToolDescriptor descriptor,
        AIFunction governedFunction,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new NhAiGovernedAIFunction(descriptor, governedFunction, services);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var shape = NhAiToolArguments.Normalize(arguments, _enveloped);
        if (shape.Arguments is null)
        {
            return await NhAiToolArguments.RejectAsync(
                Descriptor,
                shape.UnexpectedNames,
                _services ?? arguments.Services,
                cancellationToken);
        }

        var binding = NhAiToolArguments.BeginBinding();
        try
        {
            return await _inner.InvokeAsync(shape.Arguments, cancellationToken);
        }
        catch (Exception exception) when (!binding.InvokerEntered
            && exception is ArgumentException or JsonException)
        {
            // The arguments could not be bound to the tool input before the shared invoker ran.
            return await NhAiToolArguments.RejectAsync(
                Descriptor,
                [],
                _services ?? arguments.Services,
                cancellationToken);
        }
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
