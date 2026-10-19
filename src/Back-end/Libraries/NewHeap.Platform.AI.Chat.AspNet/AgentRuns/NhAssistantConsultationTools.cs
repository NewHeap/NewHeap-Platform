using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The governed tools that consult agents and request their work, generated from every
/// <c>AllowConsultation</c> registration: <c>assistant.consult.{agentId}.{subjectType}</c> and, when
/// requests are allowed, <c>assistant.request.{agentId}.{subjectType}</c>. Every call runs through the
/// shared invoker; no agent grant can cover them, so they always use a person's own rights.
/// </summary>
internal sealed class NhAssistantConsultationToolCatalog : INhAiAttestedToolCatalog
{
    public const string CatalogId = "assistant-consultations";

    private const int ContractVersion = 1;
    public const int MaxQuestionChars = 2_000;
    public const int MaxRequestChars = 4_000;

    private const int MaxExportNameLength = 64;

    private readonly Lazy<CatalogModel> _model;

    public NhAssistantConsultationToolCatalog(
        NhAssistantRegistrationState registration,
        IOptions<NhAssistantOptions> options)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(options);
        _model = new Lazy<CatalogModel>(
            () => Build(registration, options.Value),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors => _model.Value.Descriptors;

    public NhAiToolCatalogManifest Manifest => _model.Value.Manifest;

    public string AttestationHash => _model.Value.Manifest.SchemaHash;

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var model = _model.Value;
        return model.Descriptors
            .Select(descriptor => CreateFunction(descriptor, model.Tools[descriptor.Id], services))
            .ToArray();
    }

    private static AIFunction CreateFunction(NhAiToolDescriptor descriptor, ConsultationTool tool, IServiceProvider services)
    {
        AIFunction inner = tool.IsRequest
            ? AIFunctionFactory.Create(
                (JsonElement input, CancellationToken cancellationToken) => InvokeAsync(
                    descriptor,
                    services,
                    input,
                    (handler, context, token) => handler.RequestAsync(tool.Options, input, context, token),
                    cancellationToken),
                CreateFunctionOptions<NhAssistantAgentRequestReceipt>(descriptor))
            : AIFunctionFactory.Create(
                (JsonElement input, CancellationToken cancellationToken) => InvokeAsync(
                    descriptor,
                    services,
                    input,
                    (handler, context, token) => handler.ConsultAsync(tool.Options, input, context, token),
                    cancellationToken),
                CreateFunctionOptions<NhAssistantConsultationAnswer>(descriptor));
        return NhAiGovernedAIFunction.Create(
            descriptor,
            new EnvelopeFunction(inner, CreateEnvelopeSchema(descriptor.InputSchemaJson)),
            services);
    }

    /// <summary>
    /// Gives the model the shared tool result: the data on success, otherwise the stable
    /// <c>assistant-consult-*</c> or pipeline code and its message.
    /// </summary>
    private static AIFunctionFactoryOptions CreateFunctionOptions<T>(NhAiToolDescriptor descriptor)
    {
        return new AIFunctionFactoryOptions
        {
            Name = descriptor.ExportName,
            Description = descriptor.Description,
            ExcludeResultSchema = true,
            MarshalResult = NhAiToolResult.CreateMarshaller<T>(ResultOptions)
        };
    }

    private static Task<TaskResult<T>> InvokeAsync<T>(
        NhAiToolDescriptor descriptor,
        IServiceProvider services,
        JsonElement input,
        Func<NhAssistantConsultationToolHandler, NhAiInvocationContext, CancellationToken, Task<TaskResult<T>>> handle,
        CancellationToken cancellationToken)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var handler = services.GetRequiredService<NhAssistantConsultationToolHandler>();
        return invoker.InvokeAsync(
            descriptor,
            input,
            (context, token) => handle(handler, context, token),
            cancellationToken);
    }

    // Writes nulls, so the receipt's nullable run and thread ids stay present as the schema requires.
    private static readonly JsonSerializerOptions ResultOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = AIJsonUtilities.DefaultOptions.TypeInfoResolver
    };

    private static CatalogModel Build(NhAssistantRegistrationState registration, NhAssistantOptions options)
    {
        var accessPolicy = NhAssistantEndpointOptions.ResolveAccessPolicy(registration, options);
        var tools = new Dictionary<string, ConsultationTool>(StringComparer.Ordinal);
        var descriptors = new List<NhAiToolDescriptor>();
        foreach (var consultation in registration.AgentRuns.Values.SelectMany(run => run.Consultations))
        {
            var agent = registration.Agents.FirstOrDefault(item => string.Equals(item.Id, consultation.AgentId, StringComparison.Ordinal));
            var policies = consultation.Policies.Count > 0
                ? consultation.Policies
                : [agent?.RequiredPolicy ?? accessPolicy];
            descriptors.Add(CreateConsultDescriptor(consultation, policies));
            tools.Add(consultation.ConsultToolId, new ConsultationTool(consultation, false));
            if (consultation.AllowRequests)
            {
                descriptors.Add(CreateRequestDescriptor(consultation, policies));
                tools.Add(consultation.RequestToolId, new ConsultationTool(consultation, true));
            }
        }

        var ordered = descriptors
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ToArray();
        var attestationHash = Hash(string.Join(
            "\n",
            ordered.Select(descriptor => descriptor.Id + "@"
                + descriptor.Version.ToString(CultureInfo.InvariantCulture) + ":" + descriptor.ContractHash)));
        var manifest = new NhAiToolCatalogManifest(
            CatalogId,
            ContractVersion,
            attestationHash,
            ordered
                .Select(descriptor => new NhAiToolManifestEntry(
                    descriptor.Id,
                    descriptor.Version,
                    descriptor.SchemaHash,
                    descriptor.ContractHash)
                {
                    ExportName = descriptor.ExportName
                })
                .ToArray());
        return new CatalogModel(ordered, tools, manifest);
    }

    private static NhAiToolDescriptor CreateConsultDescriptor(
        NhAssistantConsultationOptions consultation,
        IReadOnlyList<string> policies)
    {
        var inputSchema = ObjectSchema(
            ("subjectId", StringSchema(NhAiSubjectRef.MaxIdLength, $"The id of the {consultation.SubjectType}, exactly as the application shows it.")),
            ("question", StringSchema(MaxQuestionChars, "A self-contained question for the agent.")));
        var outputSchema = """{"type":"object","properties":{"consultationId":{"type":"string","format":"uuid"},"answer":{"type":"string"},"proposalCount":{"type":"integer"}},"required":["consultationId","answer","proposalCount"]}""";
        var description = (consultation.Description is null ? string.Empty : consultation.Description + " ")
            + $"Ask the agent '{consultation.AgentId}' about one {consultation.SubjectType} and get its answer now. "
            + "It changes nothing itself; what it would change becomes a proposal for the "
            + consultation.SubjectType + "'s approvers.";
        return CreateDescriptor(
            consultation.ConsultToolId,
            "consult_" + consultation.AgentId + "_" + consultation.SubjectType,
            description,
            typeof(NhAssistantConsultationAnswer),
            NhAiToolEffect.ReadOnly,
            consultation,
            policies,
            inputSchema,
            outputSchema) with
        {
            Timeout = consultation.Timeout + TimeSpan.FromSeconds(15),
            MaxResultBytes = Math.Max(65_536, (consultation.MaxAnswerChars * 4) + 1_024)
        };
    }

    private static NhAiToolDescriptor CreateRequestDescriptor(
        NhAssistantConsultationOptions consultation,
        IReadOnlyList<string> policies)
    {
        var inputSchema = ObjectSchema(
            ("subjectId", StringSchema(NhAiSubjectRef.MaxIdLength, $"The id of the {consultation.SubjectType}, exactly as the application shows it.")),
            ("message", StringSchema(MaxRequestChars, "What the agent should work on.")));
        var outputSchema = """{"type":"object","properties":{"outcome":{"type":"string"},"runId":{"type":["string","null"],"format":"uuid"},"threadId":{"type":["string","null"],"format":"uuid"}},"required":["outcome","runId","threadId"]}""";
        var description = $"Ask the agent '{consultation.AgentId}' to work on one {consultation.SubjectType} in a run of its own. "
            + "It answers later in its thread about the " + consultation.SubjectType + ", and the person is notified.";
        return CreateDescriptor(
            consultation.RequestToolId,
            "request_" + consultation.AgentId + "_" + consultation.SubjectType,
            description,
            typeof(NhAssistantAgentRequestReceipt),
            NhAiToolEffect.IdempotentMutation,
            consultation,
            policies,
            inputSchema,
            outputSchema) with
        {
            // Starting a run is what a participant may do in the thread anyway; the run's own
            // changes wait for the subject's approvers. A retried call starts no second run.
            Approval = NhAiApprovalRequirement.NotRequired,
            Idempotency = NhAiIdempotencySupport.Required
        };
    }

    private static NhAiToolDescriptor CreateDescriptor(
        string id,
        string exportName,
        string description,
        Type outputType,
        NhAiToolEffect effect,
        NhAssistantConsultationOptions consultation,
        IReadOnlyList<string> policies,
        string inputSchema,
        string outputSchema)
    {
        var effectCode = effect.ToString();
        return new NhAiToolDescriptor(
            id,
            ContractVersion,
            NhAssistantContent.Bound(description, 1_024),
            typeof(JsonElement),
            outputType,
            effect,
            consultation.Exposure,
            true,
            policies.ToArray())
        {
            ExportName = ToBoundedExportName(exportName + "_v" + ContractVersion.ToString(CultureInfo.InvariantCulture)),
            CatalogId = CatalogId,
            CatalogVersion = ContractVersion,
            DeclaringAssembly = typeof(NhAssistantConsultationToolCatalog).Assembly.GetName().Name ?? string.Empty,
            InputSchemaJson = inputSchema,
            OutputSchemaJson = outputSchema,
            SchemaHash = Hash(inputSchema + "\n" + outputSchema),
            ContractHash = Hash(string.Join(
                "\n",
                id,
                effectCode,
                inputSchema,
                outputSchema,
                string.Join(",", policies))),
            Approval = NhAiApprovalRequirement.PolicyControlled,
            Idempotency = NhAiIdempotencySupport.None,
            MaxConcurrency = 8,
            MaxInputBytes = 16_384,
            DataClassification = consultation.DataClassification
        };
    }

    private static string ObjectSchema(params (string Name, JsonObject Schema)[] properties)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(properties.Select(property =>
                new KeyValuePair<string, JsonNode?>(property.Name, property.Schema))),
            ["required"] = new JsonArray(properties.Select(property => (JsonNode?)JsonValue.Create(property.Name)).ToArray()),
            ["additionalProperties"] = false
        };
        return schema.ToJsonString();
    }

    private static JsonObject StringSchema(int maxLength, string description)
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["minLength"] = 1,
            ["maxLength"] = maxLength,
            ["description"] = description
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

    /// <summary>
    /// Keeps an export name within the 64 characters model providers accept, with a stable hash
    /// suffix when it has to be shortened.
    /// </summary>
    private static string ToBoundedExportName(string value)
    {
        var name = value.Replace('.', '_');
        if (name.Length <= MaxExportNameLength)
        {
            return name;
        }

        var suffix = Hash(name)[..12];
        return name[..(MaxExportNameLength - suffix.Length - 1)].TrimEnd('_', '-') + "_" + suffix;
    }

    private static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed record ConsultationTool(NhAssistantConsultationOptions Options, bool IsRequest);

    private sealed record CatalogModel(
        IReadOnlyList<NhAiToolDescriptor> Descriptors,
        IReadOnlyDictionary<string, ConsultationTool> Tools,
        NhAiToolCatalogManifest Manifest);

    /// <summary>
    /// Publishes the tool's input schema inside the shared <c>input</c> envelope instead of the
    /// untyped schema of the factory function.
    /// </summary>
    private sealed class EnvelopeFunction(AIFunction inner, JsonElement schema) : AIFunction
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => schema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            return inner.InvokeAsync(arguments, cancellationToken);
        }
    }
}

/// <summary>
/// Runs the generated consultation tools for the person whose rights they use: the person chatting,
/// the initiator who delegated an agent turn, or the MCP caller.
/// </summary>
internal sealed class NhAssistantConsultationToolHandler(
    NhAssistantConsultationService consultations,
    NhAssistantSubjectAccessService subjects,
    NhAssistantAgentTriggers triggers,
    NhAssistantAgentEvents events,
    NhAssistantRegistrationState registration,
    IHttpContextAccessor httpContextAccessor,
    IServiceProvider services)
{
    private static readonly JsonSerializerOptions InputOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public async Task<TaskResult<NhAssistantConsultationAnswer>> ConsultAsync(
        NhAssistantConsultationOptions options,
        JsonElement input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        var request = Read<ConsultInput>(input);
        if (request is not { SubjectId.Length: > 0, Question.Length: > 0 and <= NhAssistantConsultationToolCatalog.MaxQuestionChars })
        {
            return TaskResult<NhAssistantConsultationAnswer>.Failed(
                NhAiToolFailureCodes.InputInvalid,
                "Give the subject id and a question.");
        }

        var asker = ResolveAsker(context);
        if (asker is null)
        {
            return TaskResult<NhAssistantConsultationAnswer>.Failed(
                NhAssistantErrorCodes.ConsultSubjectUnavailable,
                "A consultation needs a person whose rights it uses.");
        }

        var requestPrincipal = RequestPrincipalOf(asker);
        return await consultations.ConsultAsync(
            new NhAssistantConsultationRequest(
                options,
                request.SubjectId,
                request.Question,
                asker,
                requestPrincipal is not null,
                CultureInfo.CurrentUICulture.Name,
                new NhAssistantConsultationViewer(services, registration, options, request.SubjectId.Trim(), asker, requestPrincipal)),
            cancellationToken);
    }

    public async Task<TaskResult<NhAssistantAgentRequestReceipt>> RequestAsync(
        NhAssistantConsultationOptions options,
        JsonElement input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        var request = Read<RequestInput>(input);
        if (request is not { SubjectId.Length: > 0, Message.Length: > 0 and <= NhAssistantConsultationToolCatalog.MaxRequestChars })
        {
            return TaskResult<NhAssistantAgentRequestReceipt>.Failed(
                NhAiToolFailureCodes.InputInvalid,
                "Give the subject id and what the agent should work on.");
        }

        var asker = ResolveAsker(context);
        var askingAgentId = NhAssistantExecutionScope.CurrentTurn?.Agent.Id;
        var subjectId = request.SubjectId.Trim();
        if (asker is null || subjectId.Length > NhAiSubjectRef.MaxIdLength)
        {
            return TaskResult<NhAssistantAgentRequestReceipt>.Failed(
                NhAssistantErrorCodes.ConsultSubjectUnavailable,
                "The subject does not exist or you may not ask the agent about it.");
        }

        var subject = new NhAiSubjectRef(options.SubjectType, subjectId);
        var access = await subjects.ResolveAsync(subject, asker, cancellationToken);
        if (!access.Has(NhAiSubjectRole.Participant)
            || await subjects.DescribeAsync(subject, cancellationToken) is null)
        {
            await events.RequestAsync(options.AgentId, subject, asker, askingAgentId, NhAssistantErrorCodes.ConsultSubjectUnavailable, null, null, CancellationToken.None);
            return TaskResult<NhAssistantAgentRequestReceipt>.Failed(
                NhAssistantErrorCodes.ConsultSubjectUnavailable,
                "The subject does not exist or you may not ask the agent about it.");
        }
        // An agent in shadow takes no requests: its work stays with administrators.
        if (await services.GetRequiredService<NhAssistantShadowModes>().IsAgentShadowAsync(options.AgentId, cancellationToken))
        {
            await events.RequestAsync(options.AgentId, subject, asker, askingAgentId, NhAssistantErrorCodes.ConsultAgentUnavailable, null, null, CancellationToken.None);
            return TaskResult<NhAssistantAgentRequestReceipt>.Failed(
                NhAssistantErrorCodes.ConsultAgentUnavailable,
                "The agent cannot take requests.");
        }

        // A retried call of the same tool invocation starts no second run.
        var receipt = await triggers.FireAsync(
            new NhAssistantTriggerFire(
                NhAssistantTriggerIds.AgentRequest,
                options.AgentId,
                subject,
                asker,
                NhAssistantContent.Bound(request.Message.Trim(), registration.Limits.MaxMessageChars),
                context.IdempotencyKey ?? context.InvocationId.ToString("N"),
                NhAssistantTriggerConflict.Queue,
                TimeSpan.Zero),
            cancellationToken);
        var rejected = receipt.Outcome == NhAssistantTriggerOutcomes.Rejected;
        await events.RequestAsync(
            options.AgentId,
            subject,
            asker,
            askingAgentId,
            rejected ? receipt.ReasonCode ?? receipt.Outcome : receipt.Outcome,
            receipt.ConversationId,
            receipt.RunId,
            CancellationToken.None);
        if (rejected)
        {
            var code = receipt.ReasonCode ?? NhAssistantErrorCodes.ConsultAgentUnavailable;
            return TaskResult<NhAssistantAgentRequestReceipt>.Failed(code, "The agent did not accept the request.");
        }

        return TaskResult<NhAssistantAgentRequestReceipt>.Succeeded(
            new NhAssistantAgentRequestReceipt(receipt.Outcome, receipt.RunId, receipt.ConversationId));
    }

    /// <summary>
    /// The person whose rights the call uses. In an agent turn that is the initiator who delegated
    /// it; the gate never lets an agent's own grant cover these tools.
    /// </summary>
    private static string? ResolveAsker(NhAiInvocationContext context)
    {
        if (NhAiAgentInvocationScopes.Current is { } scope)
        {
            return string.IsNullOrWhiteSpace(scope.InitiatorActorId) ? null : scope.InitiatorActorId;
        }

        if (NhAssistantExecutionScope.CurrentTurn is { } turn)
        {
            return turn.OwnerActorId;
        }

        return context.ActorKind == NhAiActorKind.Human ? context.ActorId : null;
    }

    /// <summary>
    /// The request principal when it is the asker's own, so authorization uses the live request
    /// instead of a rebuilt principal.
    /// </summary>
    private ClaimsPrincipal? RequestPrincipalOf(string asker)
    {
        var user = httpContextAccessor.HttpContext?.User;
        return user?.Identity?.IsAuthenticated == true
               && string.Equals(user.FindFirstValue(ClaimTypes.NameIdentifier), asker, StringComparison.Ordinal)
            ? user
            : null;
    }

    private static T? Read<T>(JsonElement input)
        where T : class
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return input.Deserialize<T>(InputOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ConsultInput(string? SubjectId, string? Question);

    private sealed record RequestInput(string? SubjectId, string? Message);
}

/// <summary>
/// Shields what the consulted agent obtained with its own grant from an asker who could not have run
/// the tool, with the same rule as the thread view. The asker's principal is the request's own, or
/// one rebuilt by <see cref="INhAiPrincipalFactory"/> in a background turn.
/// </summary>
internal sealed class NhAssistantConsultationViewer(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    NhAssistantConsultationOptions options,
    string subjectId,
    string askerActorId,
    ClaimsPrincipal? requestPrincipal) : INhAssistantConsultationViewer
{
    private readonly Dictionary<string, bool> _allowed = new(StringComparer.Ordinal);
    private NhAiSubjectAuthorizationResource? _resource;
    private ClaimsPrincipal? _principal;
    private bool _resolved;

    public async ValueTask<bool> MayViewAsync(
        NhAiGrantSource grantSource,
        string toolId,
        string? readForActorId,
        CancellationToken cancellationToken)
    {
        if (grantSource == NhAiGrantSource.DelegatedRead)
        {
            return NhAssistantRestrictedResults.IsReadFor(askerActorId, readForActorId);
        }
        var shield = !registration.AgentRuns.TryGetValue(options.AgentId, out var runOptions)
                     || runOptions.ElevatedReads == NhAiElevatedReads.Restricted;
        if (grantSource is not (NhAiGrantSource.Agent or NhAiGrantSource.Accountable) || !shield)
        {
            return true;
        }
        if (_allowed.TryGetValue(toolId, out var cached))
        {
            return cached;
        }

        await ResolveAsync(cancellationToken);
        var may = _principal is not null
                  && _resource is not null
                  && await NhAssistantRestrictedResults.SatisfiesToolPoliciesAsync(services, _principal, _resource, toolId);
        _allowed[toolId] = may;
        return may;
    }

    private async Task ResolveAsync(CancellationToken cancellationToken)
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        var subject = await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef(options.SubjectType, subjectId), cancellationToken);
        if (subject is null)
        {
            return;
        }

        _resource = new NhAiSubjectAuthorizationResource(
            subject.Subject,
            subject.ScopeKey,
            subject.DivisionId,
            NhAssistantRestrictedResults.ViewPurpose);
        _principal = requestPrincipal
                     ?? await services.GetRequiredService<INhAiPrincipalFactory>().CreateAsync(askerActorId, subject, cancellationToken);
    }
}
