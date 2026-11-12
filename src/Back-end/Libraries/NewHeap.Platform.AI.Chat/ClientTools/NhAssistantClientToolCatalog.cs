using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The governed tools of the client tools the application declared with
/// <see cref="NhAssistantBuilder.AddClientTool{TInput}"/>, as an attested catalog. Each descriptor is a
/// read-only tool that only agents see (<see cref="NhAiToolExposure.Agent"/>), never MCP clients, so the
/// invocation gate authorizes the person's policies and the agent's selectors, the tool budget counts the
/// call and the audit records it as for any read. Its function binds the input and runs nothing in the
/// application: it returns the bound arguments, and the turn's interceptor hands them to the person's
/// browser and waits for its report.
/// </summary>
internal sealed class NhAssistantClientToolCatalog : INhAiAttestedToolCatalog
{
    public const string CatalogId = "assistant-client-tools";

    /// <summary>
    /// The property of the function's data that holds the bound arguments the browser receives.
    /// </summary>
    internal const string ArgumentsPropertyName = "arguments";

    private const int ContractVersion = 1;

    /// <summary>
    /// What the model reads after the application's description of every client tool.
    /// </summary>
    internal const string RunsInBrowserNote =
        " This tool runs in the person's own browser, in the application's interface, only while the person takes part in this chat; its result says what happened there.";

    /// <summary>
    /// What the model reads after the application's description of a client tool that hands a change to
    /// the person (<see cref="NhAssistantClientToolBuilder{TInput}.HandsOffChange"/>).
    /// </summary>
    internal const string HandsOffChangeNote =
        " It opens the application's own form with these values; the person reviews and saves it with their own rights, so nothing changes until they do. When the person asks in this chat for a change this tool covers, prefer it over proposing the same change; the result says whether they saved, cancelled or closed the form.";

    internal static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private readonly IReadOnlyDictionary<string, Tool> _tools;

    public NhAssistantClientToolCatalog(NhAssistantRegistrationState registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _tools = registration.ClientTools
            .OrderBy(definition => definition.Id, StringComparer.Ordinal)
            .Select(definition => new Tool(definition, DescriptorOf(definition)))
            .ToDictionary(tool => tool.Definition.Id, StringComparer.Ordinal);
        Descriptors = _tools.Values.Select(tool => tool.Descriptor).ToArray();
        AttestationHash = Hash(string.Join(
            "\n",
            Descriptors
                .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
                .ThenBy(descriptor => descriptor.Version)
                .Select(descriptor => descriptor.Id + "@" + descriptor.Version.ToString(CultureInfo.InvariantCulture) + ":" + descriptor.ContractHash)));
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

    /// <summary>
    /// Whether <paramref name="descriptor"/> is a client tool of this catalog.
    /// </summary>
    public static bool IsClientTool(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return string.Equals(descriptor.CatalogId, CatalogId, StringComparison.Ordinal);
    }

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return _tools.Values
            .Select(tool => CreateFunction(tool, services))
            .ToArray();
    }

    private static AIFunction CreateFunction(Tool tool, IServiceProvider services)
    {
        var descriptor = tool.Descriptor;
        var inner = AIFunctionFactory.Create(
            (JsonElement input, CancellationToken cancellationToken) => InvokeAsync(tool, services, input, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = descriptor.ExportName,
                Description = descriptor.Description,
                ExcludeResultSchema = true,
                MarshalResult = NhAiToolResult.CreateMarshaller<JsonNode>(JsonOptions)
            });
        return NhAiGovernedAIFunction.Create(
            descriptor,
            new EnvelopeFunction(inner, CreateEnvelopeSchema(descriptor.InputSchemaJson)),
            services);
    }

    /// <summary>
    /// Binds the model's input to the tool's input type and runs the call through the shared invoker, which
    /// authorizes, budgets and audits it. The call itself changes nothing: its data are the bound arguments
    /// the browser receives. Input that does not bind is refused before the invoker runs.
    /// </summary>
    private static async Task<TaskResult<JsonNode>> InvokeAsync(
        Tool tool,
        IServiceProvider services,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        if (Bind(tool.Definition.InputType, input) is not { } bound)
        {
            return await RefuseInputAsync(tool.Descriptor, services, cancellationToken);
        }

        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        return await invoker.InvokeAsync(
            tool.Descriptor,
            bound,
            (_, _) => Task.FromResult(TaskResult<JsonNode>.Succeeded(new JsonObject
            {
                [ArgumentsPropertyName] = JsonNode.Parse(bound.GetRawText())
            })),
            cancellationToken);
    }

    /// <summary>
    /// The input bound to <paramref name="type"/> and written again, so the browser receives only the
    /// declared properties; <see langword="null"/> when it does not bind.
    /// </summary>
    internal static JsonElement? Bind(Type type, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var value = input.Deserialize(type, JsonOptions);
            return value is null ? null : JsonSerializer.SerializeToElement(value, type, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Refuses a call whose input does not bind, as a refused argument shape is: the outcome of the call
    /// and a content-free audit record name the code, never a value.
    /// </summary>
    private static async Task<TaskResult<JsonNode>> RefuseInputAsync(
        NhAiToolDescriptor descriptor,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        const string message = "The input does not match the client tool's input schema.";
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

    private static NhAiToolDescriptor DescriptorOf(NhAssistantClientToolDefinition definition)
    {
        var inputSchema = AIJsonUtilities.CreateJsonSchema(definition.InputType, serializerOptions: JsonOptions).GetRawText();
        var outputSchema = definition.ResultType is null
            ? "{}"
            : AIJsonUtilities.CreateJsonSchema(definition.ResultType, serializerOptions: JsonOptions).GetRawText();
        var description = definition.Description.TrimEnd() + RunsInBrowserNote + (definition.HandsOffChange ? HandsOffChangeNote : string.Empty);
        return new NhAiToolDescriptor(
            definition.Id,
            definition.Version,
            description,
            definition.InputType,
            typeof(JsonElement),
            NhAiToolEffect.ReadOnly,
            NhAiToolExposure.Agent,
            true,
            definition.Policies)
        {
            ExportName = definition.ExportName,
            CatalogId = CatalogId,
            CatalogVersion = ContractVersion,
            DeclaringAssembly = typeof(NhAssistantClientToolCatalog).Assembly.GetName().Name ?? string.Empty,
            InputSchemaJson = inputSchema,
            OutputSchemaJson = outputSchema,
            SchemaHash = Hash(inputSchema + "\n" + outputSchema),
            ContractHash = Hash(string.Join(
                "\n",
                definition.Id,
                definition.Version.ToString(CultureInfo.InvariantCulture),
                description,
                definition.HandsOffChange ? "hands-off-change" : "shows",
                inputSchema,
                outputSchema,
                string.Join(",", definition.Policies))),
            MaxConcurrency = 8,
            Timeout = TimeSpan.FromSeconds(10),
            MaxInputBytes = 16_384,
            DataClassification = NhAiDataClassification.Internal,
            RetentionCategory = NhAiRetentionCategory.Operational
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

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = AIJsonUtilities.DefaultOptions.TypeInfoResolver,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed record Tool(NhAssistantClientToolDefinition Definition, NhAiToolDescriptor Descriptor);

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
