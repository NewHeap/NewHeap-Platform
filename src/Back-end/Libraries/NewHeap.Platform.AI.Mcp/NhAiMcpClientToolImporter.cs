using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Mcp;

public sealed record NhAiMcpImportedToolPolicy(
    string RemoteName,
    string Id,
    string Description,
    NhAiToolEffect Effect,
    NhAiToolExposure Exposure,
    IReadOnlyList<string> AuthorizationPolicies)
{
    public int Version { get; init; } = 1;
    public NhAiApprovalRequirement Approval { get; init; } = NhAiApprovalRequirement.Required;
    public NhAiIdempotencySupport Idempotency { get; init; } = NhAiIdempotencySupport.None;
    public string? VerifierId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxConcurrency { get; init; } = 1;
    public int MaxInputBytes { get; init; } = 65_536;
    public int MaxResultBytes { get; init; } = 65_536;
    public NhAiDataClassification DataClassification { get; init; } = NhAiDataClassification.Internal;
    public NhAiRetentionCategory RetentionCategory { get; init; } = NhAiRetentionCategory.Operational;
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
}

public sealed record NhAiMcpImportOptions(
    string ServerId,
    string Namespace,
    IReadOnlyList<NhAiMcpImportedToolPolicy> Tools)
{
    public int MaxInputSchemaBytes { get; init; } = 65_536;
    public int MaxOutputSchemaBytes { get; init; } = 65_536;
}

public interface INhAiMcpClientToolImporter
{
    INhAiToolCatalog Import(
        IEnumerable<McpClientTool> remoteTools,
        NhAiMcpImportOptions options);
}

internal sealed partial class NhAiMcpClientToolImporter : INhAiMcpClientToolImporter
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    public INhAiToolCatalog Import(
        IEnumerable<McpClientTool> remoteTools,
        NhAiMcpImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(remoteTools);
        ArgumentNullException.ThrowIfNull(options);
        ValidateSegment(options.ServerId, nameof(options.ServerId));
        ValidateSegment(options.Namespace, nameof(options.Namespace));
        if (options.MaxInputSchemaBytes is < 2 or > 1_048_576
            || options.MaxOutputSchemaBytes is < 2 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MCP import schema limits must be between 2 bytes and 1 MiB.");
        }

        var toolsByRemoteName = remoteTools
            .GroupBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var imported = new List<NhAiImportedMcpTool>(options.Tools.Count);
        var localIds = new HashSet<string>(StringComparer.Ordinal);
        var remoteNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in options.Tools)
        {
            ValidatePolicy(policy);
            if (!localIds.Add(policy.Id))
            {
                throw new InvalidOperationException(
                    $"MCP import policy contains duplicate local tool id '{policy.Id}'.");
            }
            if (!remoteNames.Add(policy.RemoteName))
            {
                throw new InvalidOperationException(
                    $"MCP import policy contains duplicate remote tool name '{policy.RemoteName}'.");
            }
            if (!toolsByRemoteName.TryGetValue(policy.RemoteName, out var matches)
                || matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Allowlisted MCP tool '{policy.RemoteName}' was not discovered exactly once.");
            }

            var remote = matches[0];
            var inputSchema = ValidateSchema(
                remote.JsonSchema,
                options.MaxInputSchemaBytes,
                policy.RemoteName,
                "input");
            var outputSchema = remote.ReturnJsonSchema is { } returnSchema
                ? ValidateSchema(
                    returnSchema,
                    options.MaxOutputSchemaBytes,
                    policy.RemoteName,
                    "output")
                : "{}";
            imported.Add(new NhAiImportedMcpTool(
                options.ServerId,
                options.Namespace,
                policy,
                remote,
                inputSchema,
                outputSchema));
        }

        return new NhAiImportedMcpCatalog(options.ServerId, options.Namespace, imported);
    }

    private static void ValidatePolicy(NhAiMcpImportedToolPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateSegment(policy.Id, nameof(policy.Id));
        if (string.IsNullOrWhiteSpace(policy.RemoteName)
            || policy.RemoteName.Length > 128
            || string.IsNullOrWhiteSpace(policy.Description)
            || policy.Description.Length > 512
            || policy.Version < 1
            || policy.Timeout <= TimeSpan.Zero
            || policy.Timeout > TimeSpan.FromMinutes(15)
            || policy.MaxConcurrency is < 1 or > 1_000
            || policy.MaxInputBytes is < 1 or > 16_777_216
            || policy.MaxResultBytes is < 1 or > 16_777_216
            || policy.Exposure == NhAiToolExposure.None
            || (policy.Exposure & NhAiToolExposure.Mcp) != 0)
        {
            throw new InvalidOperationException(
                $"MCP import policy for '{policy.RemoteName}' contains invalid local governance metadata.");
        }
        if (policy.Effect != NhAiToolEffect.ReadOnly
            && policy.Approval == NhAiApprovalRequirement.NotRequired)
        {
            throw new InvalidOperationException(
                $"Imported MCP mutation '{policy.RemoteName}' cannot opt out of approval.");
        }
        if (policy.Effect != NhAiToolEffect.ReadOnly
            && policy.Idempotency != NhAiIdempotencySupport.Required)
        {
            throw new InvalidOperationException(
                $"Imported MCP side effect '{policy.RemoteName}' must require idempotency.");
        }
        if (policy.Effect is NhAiToolEffect.Mutation
                or NhAiToolEffect.ExternalSideEffect
                or NhAiToolEffect.Destructive
            && policy.Approval != NhAiApprovalRequirement.Required)
        {
            throw new InvalidOperationException(
                $"Imported MCP side effect '{policy.RemoteName}' must require approval.");
        }
        if (policy.Effect == NhAiToolEffect.Destructive
            && string.IsNullOrWhiteSpace(policy.VerifierId))
        {
            throw new InvalidOperationException(
                $"Imported destructive MCP tool '{policy.RemoteName}' must declare a verifier.");
        }
        if (policy.AuthorizationPolicies.Count == 0
            || policy.AuthorizationPolicies.Any(string.IsNullOrWhiteSpace)
            || policy.RequiredCapabilities.Any(capability =>
                string.IsNullOrWhiteSpace(capability)
                || capability.Length > 64
                || !SegmentPattern().IsMatch(capability)))
        {
            throw new InvalidOperationException(
                $"Imported MCP tool '{policy.RemoteName}' requires explicit local authorization policies and valid capabilities.");
        }
    }

    private static string ValidateSchema(
        JsonElement schema,
        int maxBytes,
        string toolName,
        string direction)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"MCP tool '{toolName}' returned a non-object {direction} schema.");
        }
        var json = schema.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > maxBytes)
        {
            throw new InvalidOperationException(
                $"MCP tool '{toolName}' exceeded the configured {direction} schema limit.");
        }
        return json;
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || !SegmentPattern().IsMatch(value))
        {
            throw new ArgumentException(
                "The value must be a dash-case identifier of at most 64 characters.",
                parameterName);
        }
    }
}

internal sealed class NhAiImportedMcpCatalog : INhAiToolCatalog
{
    private readonly IReadOnlyList<NhAiImportedMcpTool> _tools;

    public NhAiImportedMcpCatalog(
        string serverId,
        string toolNamespace,
        IReadOnlyList<NhAiImportedMcpTool> tools)
    {
        _tools = tools;
        Descriptors = tools.Select(tool => tool.Descriptor).ToArray();
        var entries = Descriptors
            .Select(descriptor => new NhAiToolManifestEntry(
                descriptor.Id,
                descriptor.Version,
                descriptor.SchemaHash,
                descriptor.ContractHash))
            .ToArray();
        Manifest = new NhAiToolCatalogManifest(
            $"mcp-{toolNamespace}",
            1,
            NhAiCanonicalJson.ComputeHash(new
            {
                serverId,
                toolNamespace,
                Tools = entries
            }),
            entries);
    }

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; }

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public NhAiToolCatalogManifest Manifest { get; }

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var invoker = services.GetService(typeof(INhAiToolInvoker)) as INhAiToolInvoker
            ?? throw new InvalidOperationException(
                "INhAiToolInvoker is not registered. Imported MCP execution remains disabled.");
        return _tools
            .Select(tool => (AIFunction)new NhAiGovernedMcpClientFunction(tool, invoker))
            .ToArray();
    }
}

internal sealed record NhAiImportedMcpTool(
    string ServerId,
    string Namespace,
    NhAiMcpImportedToolPolicy Policy,
    McpClientTool Remote,
    string InputSchema,
    string OutputSchema)
{
    public NhAiToolDescriptor Descriptor { get; } = CreateDescriptor(
        ServerId,
        Namespace,
        Policy,
        InputSchema,
        OutputSchema);

    private static NhAiToolDescriptor CreateDescriptor(
        string serverId,
        string toolNamespace,
        NhAiMcpImportedToolPolicy policy,
        string inputSchema,
        string outputSchema)
    {
        var schemaHash = NhAiCanonicalJson.ComputeHash(new
        {
            Input = JsonSerializer.Deserialize<JsonElement>(inputSchema),
            Output = JsonSerializer.Deserialize<JsonElement>(outputSchema)
        });
        var id = $"mcp.{toolNamespace}.{policy.Id}";
        var contractHash = NhAiCanonicalJson.ComputeHash(new
        {
            id,
            policy.Version,
            serverId,
            policy.RemoteName,
            policy.Description,
            policy.Effect,
            policy.Exposure,
            policy.Approval,
            policy.Idempotency,
            policy.VerifierId,
            policy.Timeout,
            policy.MaxConcurrency,
            policy.MaxInputBytes,
            policy.MaxResultBytes,
            policy.DataClassification,
            policy.RetentionCategory,
            AuthorizationPolicies = policy.AuthorizationPolicies.Order(StringComparer.Ordinal),
            RequiredCapabilities = policy.RequiredCapabilities.Order(StringComparer.Ordinal),
            schemaHash
        });
        return new NhAiToolDescriptor(
            id,
            policy.Version,
            policy.Description,
            typeof(IReadOnlyDictionary<string, object?>),
            typeof(CallToolResult),
            policy.Effect,
            policy.Exposure,
            true,
            policy.AuthorizationPolicies.ToArray())
        {
            CatalogId = $"mcp-{toolNamespace}",
            CatalogVersion = 1,
            DeclaringAssembly = typeof(NhAiMcpClientToolImporter).Assembly.GetName().Name ?? string.Empty,
            InputSchemaJson = inputSchema,
            OutputSchemaJson = outputSchema,
            SchemaHash = schemaHash,
            ContractHash = contractHash,
            Approval = policy.Approval,
            Idempotency = policy.Idempotency,
            VerifierId = policy.VerifierId,
            Timeout = policy.Timeout,
            MaxConcurrency = policy.MaxConcurrency,
            MaxInputBytes = policy.MaxInputBytes,
            MaxResultBytes = policy.MaxResultBytes,
            DataClassification = policy.DataClassification,
            RetentionCategory = policy.RetentionCategory,
            RequiredCapabilities = policy.RequiredCapabilities.ToArray()
        };
    }
}

internal sealed class NhAiGovernedMcpClientFunction(
    NhAiImportedMcpTool imported,
    INhAiToolInvoker invoker) : AIFunction, INhAiGovernedAIFunction
{
    private readonly JsonElement _inputSchema = JsonDocument.Parse(imported.InputSchema).RootElement.Clone();
    private readonly JsonElement? _outputSchema = imported.OutputSchema == "{}"
        ? null
        : JsonDocument.Parse(imported.OutputSchema).RootElement.Clone();

    public override string Name => $"mcp_{imported.Namespace}_{imported.Policy.Id}".Replace('-', '_');

    public NhAiToolDescriptor Descriptor => imported.Descriptor;

    public override string Description => imported.Policy.Description;

    public override JsonElement JsonSchema => _inputSchema;

    public override JsonElement? ReturnJsonSchema => _outputSchema;

    public override JsonSerializerOptions JsonSerializerOptions => imported.Remote.JsonSerializerOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var remoteArguments = arguments.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        return await invoker.InvokeAsync(
            imported.Descriptor,
            remoteArguments,
            async (_, invocationCancellationToken) =>
            {
                var result = await imported.Remote.CallAsync(
                    remoteArguments,
                    cancellationToken: invocationCancellationToken);
                return result.IsError is true
                    ? TaskResult<CallToolResult>
                        .Failed("The imported MCP tool reported an execution error.")
                        .WithData(result)
                    : TaskResult<CallToolResult>.Succeeded(result);
            },
            cancellationToken);
    }
}
