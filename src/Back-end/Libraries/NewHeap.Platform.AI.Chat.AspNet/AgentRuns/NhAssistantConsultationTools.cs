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
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The governed tools that consult agents and request their work, generated from every
/// <c>AllowConsultation</c> registration: <c>assistant.consult.{agentId}.{subjectType}</c> and, when
/// requests are allowed, <c>assistant.request.{agentId}.{subjectType}</c>. Every call runs through the
/// shared invoker; neither an agent grant nor the accountable account's rights can cover them, so they
/// always use a person's own rights. The catalog holds the consultations in code only and is built
/// once; those defined as data are in <see cref="NhAssistantDataConsultationToolCatalog"/>.
/// </summary>
internal sealed class NhAssistantConsultationToolCatalog : INhAiAttestedToolCatalog
{
    public const string CatalogId = "assistant-consultations";

    public const int MaxQuestionChars = 2_000;
    public const int MaxRequestChars = 4_000;

    private readonly Lazy<NhAssistantConsultationCatalogView> _model;

    public NhAssistantConsultationToolCatalog(
        NhAssistantRegistrationState registration,
        IOptions<NhAssistantOptions> options)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(options);
        _model = new Lazy<NhAssistantConsultationCatalogView>(
            () => NhAssistantConsultationCatalogView.Create(
                CatalogId,
                registration.CodeAgentRuns.Values.SelectMany(run => run.Consultations),
                consultation => NhAssistantConsultationCatalogView.PoliciesOf(registration, options.Value, consultation),
                lateBinding: false),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors => _model.Value.Descriptors;

    public NhAiToolCatalogManifest Manifest => _model.Value.Manifest;

    public string AttestationHash => _model.Value.AttestationHash;

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        return _model.Value.CreateFunctions(services);
    }
}

/// <summary>
/// One immutable view of generated consultation tools: their descriptors, manifest and attestation
/// hash, and the governed functions bound to them. A view of consultations in code binds each function
/// to the options it was built from; a view of consultations defined as data binds late: the function
/// knows its agent, subject type and contract hash and finds the consultation in effect when it is
/// called, failing closed when it is gone or its contract changed (AC12).
/// </summary>
internal sealed class NhAssistantConsultationCatalogView : INhAiAttestedToolCatalog
{
    private readonly bool _lateBinding;

    private NhAssistantConsultationCatalogView(
        IReadOnlyList<NhAiToolDescriptor> descriptors,
        IReadOnlyDictionary<string, ConsultationTool> tools,
        NhAiToolCatalogManifest manifest,
        bool lateBinding)
    {
        Descriptors = descriptors;
        Tools = tools;
        Manifest = manifest;
        _lateBinding = lateBinding;
    }

    public static NhAssistantConsultationCatalogView Empty(string catalogId)
    {
        return Create(catalogId, [], _ => [], lateBinding: true);
    }

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; }

    public NhAiToolCatalogManifest Manifest { get; }

    public string AttestationHash => Manifest.SchemaHash;

    /// <summary>
    /// The consultation of every generated tool, by tool id.
    /// </summary>
    public IReadOnlyDictionary<string, ConsultationTool> Tools { get; }

    /// <summary>
    /// What a listing shows of every tool: its id, contract, exposure and description. A change of it
    /// changes the tool list MCP clients and agents see.
    /// </summary>
    public string ListingHash => Hash(string.Join(
        "\n",
        Descriptors.Select(descriptor => string.Join(
            "|",
            descriptor.Id,
            descriptor.ContractHash,
            ((int)descriptor.Exposure).ToString(CultureInfo.InvariantCulture),
            descriptor.Description))));

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Descriptors
            .Select(descriptor => CreateFunction(descriptor, Tools[descriptor.Id], services))
            .ToArray();
    }

    /// <summary>
    /// The policies the asker must pass: the consultation's own, else the agent's required policy, else
    /// the assistant's access policy.
    /// </summary>
    public static IReadOnlyList<string> PoliciesOf(
        NhAssistantRegistrationState registration,
        NhAssistantOptions options,
        NhAssistantConsultationOptions consultation)
    {
        if (consultation.Policies.Count > 0)
        {
            return consultation.Policies;
        }

        var agent = registration.Agents.FirstOrDefault(item => string.Equals(item.Id, consultation.AgentId, StringComparison.Ordinal));
        return [agent?.RequiredPolicy ?? NhAssistantEndpointOptions.ResolveAccessPolicy(registration, options)];
    }

    public static NhAssistantConsultationCatalogView Create(
        string catalogId,
        IEnumerable<NhAssistantConsultationOptions> consultations,
        Func<NhAssistantConsultationOptions, IReadOnlyList<string>> policiesOf,
        bool lateBinding)
    {
        var tools = new Dictionary<string, ConsultationTool>(StringComparer.Ordinal);
        var descriptors = new List<NhAiToolDescriptor>();
        foreach (var consultation in consultations)
        {
            var policies = policiesOf(consultation);
            var consult = CreateConsultDescriptor(catalogId, consultation, policies);
            descriptors.Add(consult);
            tools.Add(consult.Id, new ConsultationTool(consultation, false, consult.ContractHash));
            if (consultation.AllowRequests)
            {
                var request = CreateRequestDescriptor(catalogId, consultation, policies);
                descriptors.Add(request);
                tools.Add(request.Id, new ConsultationTool(consultation, true, request.ContractHash));
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
            catalogId,
            NhAssistantConsultationTools.ContractVersion,
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
        return new NhAssistantConsultationCatalogView(ordered, tools, manifest, lateBinding);
    }

    private AIFunction CreateFunction(NhAiToolDescriptor descriptor, ConsultationTool tool, IServiceProvider services)
    {
        var options = tool.Options;
        var agentId = options.AgentId;
        var subjectType = options.SubjectType;
        var contractHash = descriptor.ContractHash;
        var lateBinding = _lateBinding;
        AIFunction inner = tool.IsRequest
            ? AIFunctionFactory.Create(
                (JsonElement input, CancellationToken cancellationToken) => InvokeAsync(
                    descriptor,
                    services,
                    input,
                    (handler, context, token) => lateBinding
                        ? handler.RequestDataAsync(agentId, subjectType, contractHash, input, context, token)
                        : handler.RequestAsync(options, input, context, token),
                    cancellationToken),
                CreateFunctionOptions<NhAssistantAgentRequestReceipt>(descriptor))
            : AIFunctionFactory.Create(
                (JsonElement input, CancellationToken cancellationToken) => InvokeAsync(
                    descriptor,
                    services,
                    input,
                    (handler, context, token) => lateBinding
                        ? handler.ConsultDataAsync(agentId, subjectType, contractHash, input, context, token)
                        : handler.ConsultAsync(options, input, context, token),
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

    private static NhAiToolDescriptor CreateConsultDescriptor(
        string catalogId,
        NhAssistantConsultationOptions consultation,
        IReadOnlyList<string> policies)
    {
        var inputSchema = ObjectSchema(
            ("subjectId", StringSchema(NhAiSubjectRef.MaxIdLength, $"The id of the {consultation.SubjectType}, exactly as the application shows it.")),
            ("question", StringSchema(NhAssistantConsultationToolCatalog.MaxQuestionChars, "A self-contained question for the agent.")));
        var outputSchema = """{"type":"object","properties":{"consultationId":{"type":"string","format":"uuid"},"answer":{"type":"string"},"proposalCount":{"type":"integer"},"decision":{"type":["string","null"],"description":"asker when the person decides the proposal now in this conversation, subject when it waits for the subject approvers"},"approvalIds":{"type":"array","items":{"type":"string","format":"uuid"}}},"required":["consultationId","answer","proposalCount"]}""";
        var description = (consultation.Description is null ? string.Empty : consultation.Description + " ")
            + $"Ask the agent '{consultation.AgentId}' about one {consultation.SubjectType} and get its answer now. "
            + ProposalsText(consultation);
        return CreateDescriptor(
            catalogId,
            consultation.ConsultToolId,
            NhAssistantConsultationTools.ExportNameOf(false, consultation.AgentId, consultation.SubjectType),
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

    /// <summary>
    /// What the asking model reads about the consulted agent's proposals: who decides them.
    /// </summary>
    private static string ProposalsText(NhAssistantConsultationOptions consultation)
    {
        var approvers = consultation.SubjectType + "'s approvers";
        if (!consultation.AllowProposals)
        {
            return "It changes nothing; it describes changes in its answer.";
        }
        if (consultation.AskerDecides)
        {
            return consultation.AtSubjectRights is null
                ? "It changes nothing itself; what it would change becomes a proposal the person decides right away in this conversation, when they may."
                : "It changes nothing itself; what it would change becomes a proposal the person decides right away in this conversation, when they may, or otherwise one for the " + approvers + ".";
        }

        return "It changes nothing itself; what it would change becomes a proposal for the " + approvers + ".";
    }

    private static NhAiToolDescriptor CreateRequestDescriptor(
        string catalogId,
        NhAssistantConsultationOptions consultation,
        IReadOnlyList<string> policies)
    {
        var inputSchema = ObjectSchema(
            ("subjectId", StringSchema(NhAiSubjectRef.MaxIdLength, $"The id of the {consultation.SubjectType}, exactly as the application shows it.")),
            ("message", StringSchema(NhAssistantConsultationToolCatalog.MaxRequestChars, "What the agent should work on.")));
        var outputSchema = """{"type":"object","properties":{"outcome":{"type":"string"},"runId":{"type":["string","null"],"format":"uuid"},"threadId":{"type":["string","null"],"format":"uuid"}},"required":["outcome","runId","threadId"]}""";
        var description = $"Ask the agent '{consultation.AgentId}' to work on one {consultation.SubjectType} in a run of its own. "
            + "It answers later in its thread about the " + consultation.SubjectType + ", and the person is notified.";
        return CreateDescriptor(
            catalogId,
            consultation.RequestToolId,
            NhAssistantConsultationTools.ExportNameOf(true, consultation.AgentId, consultation.SubjectType),
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
        string catalogId,
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
            NhAssistantConsultationTools.ContractVersion,
            NhAssistantContent.Bound(description, 1_024),
            typeof(JsonElement),
            outputType,
            effect,
            consultation.Exposure,
            true,
            policies.ToArray())
        {
            ExportName = exportName,
            CatalogId = catalogId,
            CatalogVersion = NhAssistantConsultationTools.ContractVersion,
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

    private static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <param name="ContractHash">The contract hash of the tool's descriptor in this view.</param>
    internal sealed record ConsultationTool(NhAssistantConsultationOptions Options, bool IsRequest, string ContractHash);

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
/// The consult and request tools act for a person: in an agent turn the gate allows them only through
/// the rights of the initiator the turn delegates from, never through the accountable account's
/// rights or an agent grant.
/// </summary>
internal sealed class NhAssistantConsultationPersonRightsTools : INhAiPersonRightsTools
{
    public bool UsesPersonRightsOnly(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return NhAssistantConsultationTools.IsConsultationTool(descriptor.Id);
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

    /// <summary>
    /// Consults the agent under the consultation defined as data in effect now, when it still has the
    /// contract the calling function was built with; a removed or changed consultation fails closed.
    /// </summary>
    public async Task<TaskResult<NhAssistantConsultationAnswer>> ConsultDataAsync(
        string agentId,
        string subjectType,
        string contractHash,
        JsonElement input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (ResolveData(agentId, subjectType, request: false, contractHash) is not { } options)
        {
            return TaskResult<NhAssistantConsultationAnswer>.Failed(
                NhAssistantErrorCodes.ConsultAgentUnavailable,
                "The agent cannot be consulted this way any more.");
        }

        return await ConsultAsync(options, input, context, cancellationToken);
    }

    /// <summary>
    /// Requests the agent's work under the consultation defined as data in effect now; see
    /// <see cref="ConsultDataAsync"/>.
    /// </summary>
    public async Task<TaskResult<NhAssistantAgentRequestReceipt>> RequestDataAsync(
        string agentId,
        string subjectType,
        string contractHash,
        JsonElement input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (ResolveData(agentId, subjectType, request: true, contractHash) is not { } options)
        {
            return TaskResult<NhAssistantAgentRequestReceipt>.Failed(
                NhAssistantErrorCodes.ConsultAgentUnavailable,
                "The agent cannot be asked this way any more.");
        }

        return await RequestAsync(options, input, context, cancellationToken);
    }

    private NhAssistantConsultationOptions? ResolveData(string agentId, string subjectType, bool request, string contractHash)
    {
        return services.GetService<NhAssistantDataConsultationToolCatalog>()?.Resolve(agentId, subjectType, request, contractHash);
    }

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

        var requestPrincipal = await RequestPrincipalOfAsync(asker, cancellationToken);
        return await consultations.ConsultAsync(
            new NhAssistantConsultationRequest(
                options,
                request.SubjectId,
                request.Question,
                asker,
                requestPrincipal is not null,
                CultureInfo.CurrentUICulture.Name,
                new NhAssistantConsultationViewer(services, registration, options, request.SubjectId.Trim(), asker, requestPrincipal),
                AskerTenantOf(context)),
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

        // A subject of another tenant than the asker's, and the whole application for an asker bound
        // to a tenant that takes no part in it, is unknown, as for a consultation. The asker's role is
        // the one for the agent's thread: the whole application's only for its view policy's people.
        var subject = new NhAiSubjectRef(options.SubjectType, subjectId);
        var tenant = AskerTenantOf(context);
        var access = await subjects.ResolveForAgentAsync(subject, options.AgentId, asker, cancellationToken);
        var info = access.Role == NhAiSubjectRole.None ? null : await subjects.DescribeAsync(subject, cancellationToken);
        if (info is null || (tenant is not null && !tenant.Reaches(registration, info)))
        {
            return await RefuseRequestAsync(options, subject, asker, askingAgentId, NhAssistantErrorCodes.ConsultSubjectUnavailable);
        }
        // An agent in shadow takes no requests: its work stays with administrators. A run in shadow asks
        // for none either: shadow work never starts live work, as for a hand-off.
        if (NhAssistantExecutionScope.CurrentTurn is { ShadowRun: true }
            || await services.GetRequiredService<NhAssistantShadowModes>().IsAgentShadowAsync(options.AgentId, cancellationToken))
        {
            return await RefuseRequestAsync(options, subject, asker, askingAgentId, NhAssistantErrorCodes.ConsultAgentUnavailable);
        }

        // A request starts the agent with the asker as initiator: the rule of a start by hand decides
        // (K9), with the agent as it is in effect now and the asker's own principal.
        var principal = await PrincipalOfAsync(asker, info, cancellationToken);
        if (principal is null)
        {
            return await RefuseRequestAsync(options, subject, asker, askingAgentId, NhAssistantErrorCodes.ConsultSubjectUnavailable);
        }

        var agent = await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(options.AgentId, includeDisabled: false, cancellationToken);
        var clearance = await services.GetRequiredService<NhAssistantManualStarts>()
            .ClearAsync(agent, subject, access, asker, principal, cancellationToken);
        if (!clearance.Success)
        {
            // Too little access to the subject reads as an unknown subject, so a request cannot probe.
            var code = NhAssistantResults.CodeOf(clearance, NhAssistantErrorCodes.AgentForbidden);
            return await RefuseRequestAsync(
                options,
                subject,
                asker,
                askingAgentId,
                code == NhAssistantErrorCodes.SubjectAccessDenied ? NhAssistantErrorCodes.ConsultSubjectUnavailable : code);
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
                TimeSpan.Zero)
            {
                Tenant = tenant,
                StartClearance = clearance.Data
            },
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
    /// Audits a request refused before any thread, input or run was made, and answers the asking
    /// model with the code.
    /// </summary>
    private async Task<TaskResult<NhAssistantAgentRequestReceipt>> RefuseRequestAsync(
        NhAssistantConsultationOptions options,
        NhAiSubjectRef subject,
        string asker,
        string? askingAgentId,
        string code)
    {
        await events.RequestAsync(options.AgentId, subject, asker, askingAgentId, code, null, null, CancellationToken.None);
        var message = code == NhAssistantErrorCodes.ConsultSubjectUnavailable
            ? "The subject does not exist or you may not ask the agent about it."
            : "The agent cannot take this request.";
        return TaskResult<NhAssistantAgentRequestReceipt>.Failed(code, message);
    }

    /// <summary>
    /// The asker's principal for the rules a call checks: the request's own when the request is the
    /// asker's, otherwise the one the application's principal factory rebuilds for the subject.
    /// <see langword="null"/> when neither is available, which refuses the call.
    /// </summary>
    private async Task<ClaimsPrincipal?> PrincipalOfAsync(string asker, NhAiSubjectInfo subject, CancellationToken cancellationToken)
    {
        return await RequestPrincipalOfAsync(asker, cancellationToken)
               ?? await services.GetRequiredService<INhAiPrincipalFactory>().CreateAsync(asker, subject, cancellationToken);
    }

    /// <summary>
    /// The tenant the asker's consultation or request is bound to. A person's own turn has the tenant
    /// of the person's request, and a call outside a turn, such as an MCP call, the tenant of its
    /// context; a consultation within a consultation keeps the first asker's. An agent run works in
    /// its subject's tenant, as the proposals it places do: a run bound to a tenant reaches only that
    /// tenant. A run of the whole application, stored without a tenant, is bound to none, as it places
    /// at objects of any tenant.
    /// </summary>
    private static NhAssistantTenantBinding? AskerTenantOf(NhAiInvocationContext context)
    {
        // A consulted agent that consults in turn asks for the same person, in the same tenant.
        if (NhAssistantExecutionScope.CurrentTurn?.Consultation is { } consultation)
        {
            return consultation.AskerTenant;
        }

        if (NhAiAgentInvocationScopes.Current is { } scope)
        {
            return scope.Subject.TenantId is null && NhAiSystemSubjects.IsSystem(scope.Subject.Subject)
                ? null
                : new NhAssistantTenantBinding(scope.Subject.TenantId);
        }

        if (NhAssistantExecutionScope.CurrentTurn is { } turn)
        {
            return new NhAssistantTenantBinding(turn.TenantId);
        }

        return new NhAssistantTenantBinding(context.TenantId);
    }

    /// <summary>
    /// The person whose rights the call uses. In an agent turn that is only the initiator whose
    /// rights the gate delegated for this call; a call that the accountable account's rights or an
    /// agent grant allowed has no asker, whoever started the turn.
    /// </summary>
    private static string? ResolveAsker(NhAiInvocationContext context)
    {
        if (NhAiAgentInvocationScopes.Current is { } scope)
        {
            return context.GrantSource == NhAiGrantSource.Delegated
                   && scope.DelegateFromInitiator
                   && !string.IsNullOrWhiteSpace(scope.InitiatorActorId)
                ? scope.InitiatorActorId
                : null;
        }

        if (NhAssistantExecutionScope.CurrentTurn is { } turn)
        {
            return turn.OwnerActorId;
        }

        return context.ActorKind == NhAiActorKind.Human ? context.ActorId : null;
    }

    /// <summary>
    /// The request principal when it is the asker's own, so authorization uses the live request
    /// instead of a rebuilt principal. The asker is recognized by the platform's actor id of the
    /// request's person, the id the assistant records as an asker, not by a claim.
    /// </summary>
    private async Task<ClaimsPrincipal?> RequestPrincipalOfAsync(string asker, CancellationToken cancellationToken)
    {
        // A person's turn that does not depend on the request uses the principal captured when it started.
        if (NhAiPersonInvocationScopes.Current is { } person)
        {
            return string.Equals(person.Context.ActorId, asker, StringComparison.Ordinal) ? person.User : null;
        }

        var httpContext = httpContextAccessor.HttpContext;
        return httpContext is not null && await NhAiRequestActor.IsAsync(httpContext, asker, services, cancellationToken)
            ? httpContext.User
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

    /// <summary>
    /// Whether the asker may decide a proposal of <paramref name="toolId"/> under <paramref name="policy"/>
    /// in their own conversation (CP4): the same live check as any approver of the thread, with the
    /// principal of the asker's own request. Without that request, or without a policy that names the
    /// asker, nobody decides here.
    /// </summary>
    public async ValueTask<bool> MayDecideAsync(
        NhAiApprovalPolicy policy,
        AssistantThread thread,
        string toolId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(thread);
        if (requestPrincipal is null || policy.FourEyes)
        {
            return false;
        }

        var check = await services.GetRequiredService<NhAssistantApproverCheck>().CheckAsync(
            new AssistantApproval
            {
                ConversationId = thread.ConversationId,
                ToolId = toolId,
                PolicyJson = NhAssistantApprovalPolicySnapshot.Serialize(policy),
                InitiatorActorId = askerActorId
            },
            thread,
            askerActorId,
            requestPrincipal,
            cancellationToken);
        return check.Success;
    }

    public async ValueTask<bool> MayUseAgentAsync(NhAssistantAgentDefinition agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.RequiredPolicy is null)
        {
            return true;
        }

        await ResolveAsync(cancellationToken);
        return _principal is not null
               && await services.GetRequiredService<NhAssistantAgentAccess>().CanUseAsync(_principal, agent);
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
