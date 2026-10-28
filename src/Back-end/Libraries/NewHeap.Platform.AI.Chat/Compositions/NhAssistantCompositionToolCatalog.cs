using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The governed tools of the compositions the application declared with
/// <see cref="NhAssistantBuilder.AddComposition{TInput}"/>. Each descriptor is marked
/// <see cref="NhAiCompositionFeature.Compositions"/>; its effect is the strongest effect of its steps, so a
/// composition that only reads is read-only for agents and over MCP; its data classification is the
/// highest of its steps'. The descriptors are built from the steps' tools the first time the catalog is
/// read, and a startup check reads them, so a step that names no tool, a tool of a catalog that changes
/// at runtime, another composition, a consultation, or a tool not exposed where the composition is,
/// stops the host. A composition runs no code of its own: its function runs every step through the step
/// runtime, which runs it through the shared invoker.
/// </summary>
internal sealed class NhAssistantCompositionToolCatalog : INhAiAttestedToolCatalog
{
    public const string CatalogId = "assistant-compositions";

    private const int ContractVersion = 1;

    private readonly Lazy<Model> _model;

    public NhAssistantCompositionToolCatalog(NhAssistantRegistrationState registration, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(services);
        _model = new Lazy<Model>(
            () => Model.Create(registration.Compositions, services),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors => _model.Value.Descriptors;

    public NhAiToolCatalogManifest Manifest => _model.Value.Manifest;

    public string AttestationHash => _model.Value.Manifest.SchemaHash;

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return _model.Value.Compositions
            .Select(composition => NhAiGovernedAIFunction.Create(
                composition.Descriptor,
                new NhAssistantCompositionFunction(composition, services),
                services))
            .ToArray();
    }

    /// <summary>
    /// The tool ids the steps of <paramref name="compositionId"/> call; empty for another tool.
    /// </summary>
    public IReadOnlySet<string> StepToolIdsOf(string compositionId)
    {
        return _model.Value.StepToolIds.TryGetValue(compositionId, out var ids)
            ? ids
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The tools a turn offers, without the compositions whose steps call a tool the turn does not offer:
    /// such a composition could only fail at that step.
    /// </summary>
    public static List<NhAiToolDescriptor> WithStepsOffered(
        IReadOnlyList<NhAiToolDescriptor> offered,
        IEnumerable<INhAiToolCatalog> catalogs)
    {
        var compositions = catalogs.OfType<NhAssistantCompositionToolCatalog>().FirstOrDefault();
        if (compositions is null || !offered.Any(descriptor => descriptor.CompositionFeature == NhAiCompositionFeature.Compositions))
        {
            return offered.ToList();
        }

        var ids = offered.Select(descriptor => descriptor.Id).ToHashSet(StringComparer.Ordinal);
        return offered
            .Where(descriptor => descriptor.CompositionFeature != NhAiCompositionFeature.Compositions
                || compositions.StepToolIdsOf(descriptor.Id).All(ids.Contains))
            .ToList();
    }

    internal sealed record Composition(
        NhAssistantCompositionDefinition Definition,
        NhAiToolDescriptor Descriptor);

    private sealed record Model(
        IReadOnlyList<Composition> Compositions,
        IReadOnlyList<NhAiToolDescriptor> Descriptors,
        NhAiToolCatalogManifest Manifest,
        IReadOnlyDictionary<string, IReadOnlySet<string>> StepToolIds)
    {
        public static Model Create(IEnumerable<NhAssistantCompositionDefinition> definitions, IServiceProvider services)
        {
            // Steps call tools of catalogs whose descriptors stay as they are while the host runs.
            var tools = services.GetServices<INhAiToolCatalog>()
                .Where(catalog => catalog is not NhAssistantCompositionToolCatalog and not INhAiChangingToolCatalog)
                .SelectMany(catalog => catalog.Descriptors)
                .ToArray();
            var compositions = new List<Composition>();
            var stepToolIds = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            foreach (var definition in definitions.OrderBy(definition => definition.Id, StringComparer.Ordinal))
            {
                var steps = definition.Steps.Select(step => StepTool(definition, step, tools)).ToArray();
                compositions.Add(new Composition(definition, DescriptorOf(definition, steps)));
                stepToolIds[definition.Id] = steps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
            }

            var descriptors = compositions.Select(composition => composition.Descriptor).ToArray();
            var attestation = Hash(string.Join(
                "\n",
                descriptors
                    .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
                    .ThenBy(descriptor => descriptor.Version)
                    .Select(descriptor => descriptor.Id + "@" + descriptor.Version.ToString(CultureInfo.InvariantCulture) + ":" + descriptor.ContractHash)));
            var manifest = new NhAiToolCatalogManifest(
                CatalogId,
                ContractVersion,
                attestation,
                descriptors
                    .Select(descriptor => new NhAiToolManifestEntry(descriptor.Id, descriptor.Version, descriptor.SchemaHash, descriptor.ContractHash)
                    {
                        ExportName = descriptor.ExportName
                    })
                    .ToArray());
            return new Model(compositions, descriptors, manifest, stepToolIds);
        }

        private static NhAiToolDescriptor StepTool(
            NhAssistantCompositionDefinition definition,
            NhAssistantCompositionStepDefinition step,
            IReadOnlyList<NhAiToolDescriptor> tools)
        {
            var matches = tools.Where(tool => string.Equals(tool.Id, step.ToolId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"The step '{step.Name}' of composition '{definition.Id}' calls '{step.ToolId}', which is not exactly one tool of a catalog that does not change at runtime.");
            }

            var tool = matches[0];
            if (tool.CompositionFeature != NhAiCompositionFeature.None || NhAssistantConsultationTools.IsConsultationTool(tool.Id))
            {
                throw new InvalidOperationException(
                    $"The step '{step.Name}' of composition '{definition.Id}' calls '{step.ToolId}', a composition, script or consultation tool, which no step calls.");
            }
            if ((tool.Exposure & definition.Exposure) != definition.Exposure)
            {
                throw new InvalidOperationException(
                    $"The step '{step.Name}' of composition '{definition.Id}' calls '{step.ToolId}', which is not exposed everywhere the composition is ({definition.Exposure}).");
            }

            return tool;
        }

        private static NhAiToolDescriptor DescriptorOf(NhAssistantCompositionDefinition definition, IReadOnlyList<NhAiToolDescriptor> steps)
        {
            var effect = steps.Max(step => step.Effect);
            var classification = steps.Max(step => step.DataClassification);
            var inputSchema = AIJsonUtilities.CreateJsonSchema(definition.InputType, serializerOptions: NhAssistantCompositionFunction.JsonOptions).GetRawText();
            const string outputSchema = "{}";
            var stepLines = definition.Steps
                .Zip(steps, (step, tool) => step.Name + "=" + tool.Id + "@" + tool.Version.ToString(CultureInfo.InvariantCulture) + ":" + tool.ContractHash);
            return new NhAiToolDescriptor(
                definition.Id,
                definition.Version,
                definition.Description,
                definition.InputType,
                typeof(JsonElement),
                effect,
                definition.Exposure,
                true,
                [])
            {
                ExportName = definition.ExportName,
                CatalogId = CatalogId,
                CatalogVersion = ContractVersion,
                DeclaringAssembly = typeof(NhAssistantCompositionToolCatalog).Assembly.GetName().Name ?? string.Empty,
                InputSchemaJson = inputSchema,
                OutputSchemaJson = outputSchema,
                SchemaHash = Hash(inputSchema + "\n" + outputSchema),
                ContractHash = Hash(string.Join(
                    "\n",
                    [
                        definition.Id,
                        definition.Version.ToString(CultureInfo.InvariantCulture),
                        effect.ToString(),
                        ((int)definition.Exposure).ToString(CultureInfo.InvariantCulture),
                        NhAiCompositionFeatures.NameOf(NhAiCompositionFeature.Compositions),
                        inputSchema,
                        outputSchema,
                        .. stepLines
                    ])),
                // A composition grants nothing: every step is authorized with the caller's rights.
                AllowAgentGrant = true,
                MaxConcurrency = 8,
                Timeout = TimeSpan.FromSeconds(Math.Min(600, steps.Sum(step => step.Timeout.TotalSeconds))),
                MaxResultBytes = definition.MaxResultBytes,
                DataClassification = classification,
                CompositionFeature = NhAiCompositionFeature.Compositions
            };
        }
    }

    internal static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

/// <summary>
/// Runs one composition: binds its input, runs its steps one after the other through the step runtime,
/// stops at the first step that failed, was refused or waits for a decision, and returns the declared
/// result, or what succeeded with the stopping step's code.
/// </summary>
internal sealed class NhAssistantCompositionFunction : AIFunction
{
    internal static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private readonly NhAssistantCompositionToolCatalog.Composition _composition;
    private readonly IServiceProvider _services;
    private readonly JsonElement _schema;

    public NhAssistantCompositionFunction(NhAssistantCompositionToolCatalog.Composition composition, IServiceProvider services)
    {
        _composition = composition;
        _services = services;
        _schema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["input"] = JsonNode.Parse(composition.Descriptor.InputSchemaJson)
            },
            ["required"] = new JsonArray("input")
        });
    }

    public override string Name => _composition.Descriptor.ExportName;

    public override string Description => _composition.Descriptor.Description;

    public override JsonElement JsonSchema => _schema;

    public override JsonSerializerOptions JsonSerializerOptions => JsonOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var descriptor = _composition.Descriptor;
        var definition = _composition.Definition;
        var begun = await _services.GetRequiredService<INhAssistantToolSteps>().BeginAsync(
            new NhAssistantToolStepRequest(descriptor, NhAiToolCallOriginKind.Composition)
            {
                MaxSteps = definition.Steps.Count,
                MaxMutationSteps = definition.Steps.Count
            },
            cancellationToken);
        if (!begun.Success)
        {
            return NhAiToolResult.Failed(NhAssistantResults.CodeOf(begun, NhAiToolFailureCodes.AuthorizationDenied), null);
        }

        var session = begun.Data;
        var input = Bind(arguments);
        if (input is null)
        {
            return await CompleteAsync(
                session,
                NhAiToolResult.Failed(NhAiToolFailureCodes.InputInvalid, "The input does not match the composition's input schema."),
                cancellationToken);
        }

        var byName = new Dictionary<string, NhAssistantToolStep>(StringComparer.Ordinal);
        var context = definition.CreateContext(input, name => byName.TryGetValue(name, out var step) ? step : null);
        foreach (var step in definition.Steps)
        {
            JsonElement stepArguments;
            try
            {
                stepArguments = JsonSerializer.SerializeToElement(step.Arguments(context), JsonOptions);
            }
            catch (NhAssistantCompositionStop stop)
            {
                return await CompleteAsync(session, Stopped(definition, byName, step.Name, NhAssistantCompositionCodes.StepResultUnavailable, $"The step needs the result of step '{stop.Step}', which has none."), cancellationToken);
            }

            var ran = await session.RunAsync(step.ToolId, stepArguments, cancellationToken);
            byName[step.Name] = ran;
            if (ran.Status is NhAssistantToolStepStatus.Failed or NhAssistantToolStepStatus.Refused or NhAssistantToolStepStatus.AwaitingApproval
                || session.Stopped)
            {
                return await CompleteAsync(session, Stopped(definition, byName, step.Name, ran.Code ?? NhAiToolFailureCodes.Failed, MessageOf(ran)), cancellationToken);
            }
        }

        JsonElement data;
        try
        {
            data = JsonSerializer.SerializeToElement(definition.Returns(context), JsonOptions);
        }
        catch (NhAssistantCompositionStop stop)
        {
            return await CompleteAsync(session, Stopped(definition, byName, null, NhAssistantCompositionCodes.StepResultUnavailable, $"The result needs the result of step '{stop.Step}', which has none."), cancellationToken);
        }

        var result = JsonSerializer.SerializeToElement(new JsonObject
        {
            [NhAiToolResult.SuccessPropertyName] = true,
            [NhAiToolResult.DataPropertyName] = JsonNode.Parse(data.GetRawText())
        });
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > definition.MaxResultBytes)
        {
            result = NhAiToolResult.Failed(NhAiToolFailureCodes.ResultTooLarge, "The composition's result exceeded its size limit.");
        }

        return await CompleteAsync(session, result, cancellationToken);
    }

    private static async Task<JsonElement> CompleteAsync(NhAssistantToolStepSession session, JsonElement result, CancellationToken cancellationToken)
    {
        await session.CompleteAsync(result, cancellationToken);
        return result;
    }

    private object? Bind(AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue("input", out var value) || value is null)
        {
            return null;
        }

        try
        {
            var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, JsonOptions);
            return json.ValueKind == JsonValueKind.Object
                ? json.Deserialize(_composition.Definition.InputType, JsonOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The result of a chain that stopped: what every step that ran did, in order, with the data of those
    /// that succeeded, and the step it stopped at with its code. The data of the steps is left out when
    /// it does not fit the composition's size limit.
    /// </summary>
    private static JsonElement Stopped(
        NhAssistantCompositionDefinition definition,
        IReadOnlyDictionary<string, NhAssistantToolStep> byName,
        string? stoppedAt,
        string code,
        string? detail)
    {
        var waits = stoppedAt is not null
                    && byName.TryGetValue(stoppedAt, out var stopping)
                    && stopping.Status == NhAssistantToolStepStatus.AwaitingApproval;
        var message = waits
            ? $"The composition stopped at step '{stoppedAt}', which waits for a person's decision. The steps before it completed; nothing after it ran. Do not call it again for the same work; the conversation continues after the decision."
            : stoppedAt is null
                ? $"Every step of the composition ran, but its result could not be built: {detail}"
                : $"The composition stopped at step '{stoppedAt}' with code '{code}'. The steps before it completed; nothing after it ran. {detail}";
        foreach (var withData in new[] { true, false })
        {
            var steps = new JsonArray();
            foreach (var step in definition.Steps)
            {
                if (!byName.TryGetValue(step.Name, out var ran))
                {
                    continue;
                }

                var item = new JsonObject
                {
                    ["step"] = step.Name,
                    ["tool"] = ran.ToolId,
                    ["status"] = StatusName(ran.Status)
                };
                if (ran.Code is { } stepCode)
                {
                    item["code"] = stepCode;
                }
                if (ran.ProposalId is { } proposalId)
                {
                    item["proposalId"] = proposalId.ToString("D");
                }
                if (withData && ran.TryGetData(out var data))
                {
                    item["data"] = JsonNode.Parse(data.GetRawText());
                }

                steps.Add(item);
            }

            var result = JsonSerializer.SerializeToElement(new JsonObject
            {
                [NhAiToolResult.SuccessPropertyName] = false,
                [NhAiToolResult.CodePropertyName] = code,
                [NhAiToolResult.MessagePropertyName] = message,
                [NhAiToolResult.DataPropertyName] = new JsonObject
                {
                    ["stoppedAt"] = stoppedAt,
                    ["steps"] = steps
                }
            });
            if (!withData || Encoding.UTF8.GetByteCount(result.GetRawText()) <= definition.MaxResultBytes)
            {
                return result;
            }
        }

        throw new InvalidOperationException("Unreachable.");
    }

    private static string? MessageOf(NhAssistantToolStep step)
    {
        return step.Result.ValueKind == JsonValueKind.Object
               && step.Result.TryGetProperty(NhAiToolResult.MessagePropertyName, out var message)
               && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;
    }

    internal static string StatusName(NhAssistantToolStepStatus status)
    {
        return status switch
        {
            NhAssistantToolStepStatus.Succeeded => "succeeded",
            NhAssistantToolStepStatus.Proposed => "proposed",
            NhAssistantToolStepStatus.NotNeeded => "not-needed",
            NhAssistantToolStepStatus.AwaitingApproval => "awaiting-approval",
            NhAssistantToolStepStatus.Failed => "failed",
            _ => "refused"
        };
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = AIJsonUtilities.DefaultOptions.TypeInfoResolver
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}
