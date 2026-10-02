using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Mcp;

/// <summary>
/// Adds host-owned request metadata (<c>_meta</c>) to calls of one reviewed imported MCP tool.
/// The importer calls the binder inside the governed invoker: after authorization, capability,
/// approval, budget and idempotency checks and immediately before the remote call. A failed
/// binding stops the call; the remote server never receives a partial request.
/// </summary>
public interface INhAiMcpInvocationBinder
{
    ValueTask<TaskResult<NhAiMcpRequestMetadata>> BindAsync(
        NhAiMcpInvocationBindingContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The trusted input of a binder: the validated model arguments and the authorized invocation
/// context of the current call. Arguments are model-supplied and must not be treated as identity.
/// </summary>
public sealed record NhAiMcpInvocationBindingContext(
    string ServerId,
    string Namespace,
    string RemoteName,
    NhAiToolDescriptor Descriptor,
    IReadOnlyDictionary<string, object?> Arguments,
    NhAiInvocationContext Invocation)
{
    /// <summary>
    /// Content-free provenance of this call, for the binder to include in its metadata.
    /// </summary>
    public NhAiMcpInvocationIdentity Identity => NhAiMcpInvocationIdentity.From(Descriptor, Invocation);
}

/// <summary>
/// Provenance of one governed call. It separates the acting principal, for example an agent, from
/// the accountable human and carries the run and invocation ids. It is not an authorization grant:
/// the remote server keeps its own credential and scope checks.
/// </summary>
public sealed record NhAiMcpInvocationIdentity(
    Guid InvocationId,
    string ToolId,
    int ToolVersion,
    string ContractHash,
    string ActorId,
    NhAiActorKind ActorKind,
    string? AccountableOwnerId,
    string? RunId,
    string? CorrelationId,
    string? TenantId,
    string? ProposalId,
    string? ApprovalId)
{
    public static NhAiMcpInvocationIdentity From(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        return new NhAiMcpInvocationIdentity(
            context.InvocationId,
            descriptor.Id,
            descriptor.Version,
            descriptor.ContractHash,
            context.ActorId,
            context.ActorKind,
            context.AccountableOwnerId,
            context.RunId,
            context.CorrelationId,
            context.TenantId,
            context.ProposalId,
            context.ApprovalId);
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["invocationId"] = InvocationId.ToString(),
            ["tool"] = new JsonObject
            {
                ["id"] = ToolId,
                ["version"] = ToolVersion,
                ["contractHash"] = ContractHash
            },
            ["actor"] = new JsonObject
            {
                ["id"] = ActorId,
                ["kind"] = ActorKind switch
                {
                    NhAiActorKind.Agent => "agent",
                    NhAiActorKind.Service => "service",
                    _ => "human"
                }
            }
        };
        Add(json, "accountableOwnerId", AccountableOwnerId);
        Add(json, "runId", RunId);
        Add(json, "correlationId", CorrelationId);
        Add(json, "tenantId", TenantId);
        Add(json, "proposalId", ProposalId);
        Add(json, "approvalId", ApprovalId);
        return json;
    }

    private static void Add(JsonObject json, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            json[name] = value;
        }
    }
}

/// <summary>
/// Request metadata produced by a binder. Keys use a reverse-DNS prefix such as
/// <c>com.newheap/assistant-context</c>; prefixes reserved for MCP itself are refused.
/// </summary>
public sealed partial class NhAiMcpRequestMetadata
{
    public const int MaxEntries = 8;
    public const int MaxKeyLength = 128;

    private readonly Dictionary<string, JsonNode> _entries = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, JsonNode> Entries => _entries;

    public static NhAiMcpRequestMetadata Create(string key, JsonNode value)
    {
        return new NhAiMcpRequestMetadata().Add(key, value);
    }

    public NhAiMcpRequestMetadata Add(string key, JsonNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValidKey(key))
        {
            throw new ArgumentException(
                "MCP request metadata keys need a reverse-DNS prefix that MCP does not reserve.",
                nameof(key));
        }
        if (!_entries.ContainsKey(key) && _entries.Count >= MaxEntries)
        {
            throw new InvalidOperationException("MCP request metadata contains too many entries.");
        }
        _entries[key] = value.DeepClone();
        return this;
    }

    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            return false;
        }
        var match = KeyPattern().Match(key);
        if (!match.Success)
        {
            return false;
        }
        // MCP reserves prefixes that contain a modelcontextprotocol or mcp label.
        return !match.Groups["prefix"].Value
            .Split('.')
            .Any(label => label.Equals("mcp", StringComparison.OrdinalIgnoreCase)
                || label.Equals("modelcontextprotocol", StringComparison.OrdinalIgnoreCase));
    }

    internal JsonObject ToJsonObject()
    {
        var json = new JsonObject();
        foreach (var entry in _entries)
        {
            json[entry.Key] = entry.Value.DeepClone();
        }
        return json;
    }

    [GeneratedRegex(
        "^(?<prefix>[A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\\.[A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+)/[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>
/// A reviewed server and tool pair that may receive metadata from the binder with
/// <paramref name="BinderId"/>. Import policies opt in with
/// <see cref="NhAiMcpImportedToolPolicy.InvocationBinderId"/>; a policy that names a binder without a
/// matching registration fails the import. The binder id and version are part of the tool contract,
/// so approvals and audit records identify the binder that shaped a call.
/// </summary>
public sealed record NhAiMcpInvocationBinding(
    string ServerId,
    string RemoteName,
    string BinderId)
{
    public int Version { get; init; } = 1;
}

public static class NhAiMcpInvocationBindingCodes
{
    public const string BindingFailed = "ai-mcp-binding-failed";
}

public static class NhAiMcpInvocationBinderServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TBinder"/> for one reviewed server and tool pair. Register each
    /// pair explicitly; nothing applies a binder to other servers or tools.
    /// </summary>
    public static IServiceCollection AddNewHeapPlatformAIMcpInvocationBinder<TBinder>(
        this IServiceCollection services,
        NhAiMcpInvocationBinding binding)
        where TBinder : class, INhAiMcpInvocationBinder
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(binding);
        NhAiMcpInvocationBinderRegistry.Validate(binding);
        services.AddNewHeapPlatformAIMcp();
        services.TryAddScoped<TBinder>();
        services.AddSingleton(new NhAiMcpInvocationBinderRegistration(binding, typeof(TBinder)));
        return services;
    }
}

internal sealed record NhAiMcpInvocationBinderRegistration(
    NhAiMcpInvocationBinding Binding,
    Type BinderType);

internal sealed class NhAiMcpInvocationBinderRegistry
{
    private readonly Dictionary<(string ServerId, string RemoteName), NhAiMcpInvocationBinderRegistration> _registrations = [];

    public NhAiMcpInvocationBinderRegistry()
        : this([])
    {
    }

    public NhAiMcpInvocationBinderRegistry(IEnumerable<NhAiMcpInvocationBinderRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            var key = (registration.Binding.ServerId, registration.Binding.RemoteName);
            if (_registrations.TryGetValue(key, out var existing))
            {
                if (existing.Binding == registration.Binding && existing.BinderType == registration.BinderType)
                {
                    continue;
                }
                throw new InvalidOperationException(
                    $"MCP tool '{key.RemoteName}' of server '{key.ServerId}' has more than one invocation binder.");
            }
            _registrations[key] = registration;
        }
    }

    public NhAiMcpInvocationBinderRegistration Resolve(string serverId, string remoteName, string binderId)
    {
        if (!_registrations.TryGetValue((serverId, remoteName), out var registration)
            || !string.Equals(registration.Binding.BinderId, binderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"MCP tool '{remoteName}' of server '{serverId}' names invocation binder '{binderId}', which is not registered for this server and tool.");
        }
        return registration;
    }

    public static void Validate(NhAiMcpInvocationBinding binding)
    {
        if (!NhAiNames.IsSegment(binding.ServerId)
            || !NhAiNames.IsSegment(binding.BinderId)
            || string.IsNullOrWhiteSpace(binding.RemoteName)
            || binding.RemoteName.Length > 128
            || binding.Version < 1)
        {
            throw new ArgumentException(
                "An MCP invocation binding needs dash-case server and binder ids, a remote tool name and a positive version.",
                nameof(binding));
        }
    }
}

/// <summary>
/// Binds one call: runs the binder, validates its metadata and keeps the outbound request within
/// the tool's input limit.
/// </summary>
internal static class NhAiMcpInvocationBinderRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<TaskResult<JsonObject>> BindAsync(
        INhAiMcpInvocationBinder binder,
        NhAiMcpInvocationBindingContext context,
        CancellationToken cancellationToken)
    {
        TaskResult<NhAiMcpRequestMetadata> bound;
        try
        {
            bound = await binder.BindAsync(context, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return TaskResult<JsonObject>.Failed(
                NhAiMcpInvocationBindingCodes.BindingFailed,
                "The MCP invocation context could not be bound.");
        }
        if (!bound.Success || bound.Data is null)
        {
            var code = bound.GetResultItems()
                .Select(item => item.Name)
                .FirstOrDefault(NhAiNames.IsSegment);
            return TaskResult<JsonObject>.Failed(
                code ?? NhAiMcpInvocationBindingCodes.BindingFailed,
                "The MCP invocation context could not be bound.");
        }

        var meta = bound.Data.ToJsonObject();
        var outbound = Encoding.UTF8.GetByteCount(meta.ToJsonString())
            + JsonSerializer.SerializeToUtf8Bytes(context.Arguments, SerializerOptions).Length;
        if (outbound > context.Descriptor.MaxInputBytes)
        {
            return TaskResult<JsonObject>.Failed(
                NhAiToolFailureCodes.InputTooLarge,
                "AI tool input and request metadata exceeded the configured size limit.");
        }
        return TaskResult<JsonObject>.Succeeded(meta);
    }
}
