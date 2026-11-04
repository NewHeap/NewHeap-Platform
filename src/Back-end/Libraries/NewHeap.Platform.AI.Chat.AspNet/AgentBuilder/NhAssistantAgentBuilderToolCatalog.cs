using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// The governed tools of the agent builder (RB-8), <c>assistant.agent-builder.*</c>, as an attested catalog
/// that every call goes through the shared invoker of: the invocation gate authorizes the tool's policy
/// (<see cref="NhAssistantAgentBuilderIds.Policy"/>, the access and administration policies) as the person who
/// chats, the effect policy asks for the approval of every write, and the audit records each call. Only
/// agents see them (<see cref="NhAiToolExposure.Agent"/>), never MCP clients, and the turn runner offers them
/// to the agent builder alone.
/// </summary>
internal sealed class NhAssistantAgentBuilderToolCatalog : INhAiAttestedToolCatalog
{
    public const string CatalogId = "assistant-agent-builder";

    public const int ContractVersion = 1;

    private readonly IReadOnlyDictionary<string, NhAssistantAgentBuilderTool> _tools;

    public NhAssistantAgentBuilderToolCatalog()
    {
        _tools = NhAssistantAgentBuilderTools.All.ToDictionary(tool => tool.Id, StringComparer.Ordinal);
        Descriptors = NhAssistantAgentBuilderTools.All
            .Select(CreateDescriptor)
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ToArray();
        AttestationHash = Hash(string.Join(
            "\n",
            Descriptors.Select(descriptor => descriptor.Id + "@"
                + descriptor.Version.ToString(CultureInfo.InvariantCulture) + ":" + descriptor.ContractHash)));
        Manifest = new NhAiToolCatalogManifest(
            CatalogId,
            ContractVersion,
            AttestationHash,
            Descriptors
                .Select(descriptor => new NhAiToolManifestEntry(descriptor.Id, descriptor.Version, descriptor.SchemaHash, descriptor.ContractHash)
                {
                    ExportName = descriptor.ExportName
                })
                .ToArray());
    }

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; }

    public NhAiToolCatalogManifest Manifest { get; }

    public string AttestationHash { get; }

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Descriptors
            .Select(descriptor => CreateFunction(descriptor, _tools[descriptor.Id], services))
            .ToArray();
    }

    private static AIFunction CreateFunction(NhAiToolDescriptor descriptor, NhAssistantAgentBuilderTool tool, IServiceProvider services)
    {
        var inner = AIFunctionFactory.Create(
            (JsonElement input, CancellationToken cancellationToken) => InvokeAsync(descriptor, tool, services, input, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = descriptor.ExportName,
                Description = descriptor.Description,
                ExcludeResultSchema = true,
                MarshalResult = NhAiToolResult.CreateMarshaller<JsonNode>(NhAssistantAgentBuilderToolHandler.ResultOptions)
            });
        return NhAiGovernedAIFunction.Create(
            descriptor,
            new EnvelopeFunction(inner, CreateEnvelopeSchema(descriptor.InputSchemaJson)),
            services);
    }

    private static async Task<TaskResult<JsonNode>> InvokeAsync(
        NhAiToolDescriptor descriptor,
        NhAssistantAgentBuilderTool tool,
        IServiceProvider services,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        // Input that does not fit the tool's schema, such as a secret or a number sent as text, is refused
        // before the invoker runs: it never becomes a proposal, an approval card or a stored argument.
        var (accepted, refusal) = NhAssistantAgentBuilderInput.Read(input, tool.Input.Schema);
        if (accepted is null)
        {
            return await RefuseInputAsync(descriptor, refusal!, services, cancellationToken);
        }

        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var handler = services.GetRequiredService<NhAssistantAgentBuilderToolHandler>();
        return await invoker.InvokeAsync(
            descriptor,
            input,
            (context, token) => handler.HandleAsync(tool, input, context, token),
            cancellationToken);
    }

    /// <summary>
    /// Refuses a call whose input does not fit the tool's schema, as a refused argument shape is: the outcome
    /// of the call and a content-free audit record name the code, and the message names the property, never
    /// its value.
    /// </summary>
    private static async Task<TaskResult<JsonNode>> RefuseInputAsync(
        NhAiToolDescriptor descriptor,
        string message,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        NhAiToolOutcomeCapture.Record(TaskResult.Failed(NhAiToolFailureCodes.InputInvalid, message), null);
        var record = new NhAiAuditRecord(
            Guid.NewGuid(),
            descriptor.Id,
            descriptor.Version,
            null,
            null,
            NhAiOutcomeKind.TerminalFailure,
            DateTimeOffset.UtcNow)
        {
            ResultCode = NhAiToolFailureCodes.InputInvalid,
            Origin = NhAiToolCallOrigins.Current
        };
        foreach (var sink in services.GetServices<INhAiAuditSink>())
        {
            await sink.WriteAsync(record, cancellationToken);
        }

        return TaskResult<JsonNode>.Failed(NhAiToolFailureCodes.InputInvalid, message);
    }

    private static NhAiToolDescriptor CreateDescriptor(NhAssistantAgentBuilderTool tool)
    {
        var inputSchema = tool.Input.Schema.ToJsonString();
        const string outputSchema = """{"type":"object"}""";
        var policies = new[] { NhAssistantAgentBuilderIds.Policy };
        return new NhAiToolDescriptor(
            tool.Id,
            ContractVersion,
            tool.Description,
            typeof(JsonElement),
            typeof(JsonElement),
            tool.Effect,
            NhAiToolExposure.Agent,
            true,
            policies)
        {
            ExportName = tool.ExportName,
            CatalogId = CatalogId,
            CatalogVersion = ContractVersion,
            DeclaringAssembly = typeof(NhAssistantAgentBuilderToolCatalog).Assembly.GetName().Name ?? string.Empty,
            InputSchemaJson = inputSchema,
            OutputSchemaJson = outputSchema,
            SchemaHash = Hash(inputSchema + "\n" + outputSchema),
            ContractHash = Hash(string.Join(
                "\n",
                tool.Id,
                tool.Effect.ToString(),
                tool.Approval.ToString(),
                inputSchema,
                outputSchema,
                string.Join(",", policies))),
            Approval = tool.Approval,
            // A retried call of the same invocation runs once: every effect needs an idempotency lease.
            Idempotency = tool.Effect == NhAiToolEffect.ReadOnly ? NhAiIdempotencySupport.None : NhAiIdempotencySupport.Required,
            // A dry run runs a script on up to twenty subjects.
            Timeout = TimeSpan.FromSeconds(tool.Id == NhAssistantAgentBuilderTools.DryRunRuleScript ? 120 : 60),
            MaxConcurrency = 4,
            MaxInputBytes = 65_536,
            // An agent with its instructions, skills and rule scripts reads as one result.
            MaxResultBytes = 262_144,
            DataClassification = NhAiDataClassification.Internal,
            RetentionCategory = NhAiRetentionCategory.Operational,
            // Each change of an agent is its own decision on its own card.
            RequireIndividualDecision = tool.Approval == NhAiApprovalRequirement.Required
        };
    }

    private static JsonElement CreateEnvelopeSchema(string inputSchemaJson)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["input"] = JsonNode.Parse(inputSchemaJson)
            },
            ["required"] = new JsonArray("input")
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    private static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// Publishes the tool's input schema inside the shared <c>input</c> envelope instead of the untyped
    /// schema of the factory function.
    /// </summary>
    private sealed class EnvelopeFunction(AIFunction inner, JsonElement schema) : AIFunction
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => schema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            return inner.InvokeAsync(arguments, cancellationToken);
        }
    }
}
