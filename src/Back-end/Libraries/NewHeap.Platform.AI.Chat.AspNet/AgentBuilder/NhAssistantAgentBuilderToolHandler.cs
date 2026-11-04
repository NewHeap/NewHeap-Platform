using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// One call of an agent builder tool: the person who chats with the builder, whose rights it uses, and the
/// call's checked input.
/// </summary>
internal sealed record NhAssistantAgentBuilderCall(string Person, NhAssistantAgentBuilderInput Input)
{
    /// <summary>
    /// The author of what the call saves in a rule script: the person, with the agent builder acting for them.
    /// </summary>
    public NhAssistantRuleScriptAuthor Author => new(Person) { AgentId = NhAssistantAgentBuilderIds.AgentId };
}

/// <summary>
/// Runs the agent builder's tools (RB-8, FB94, FB95) through the services the administration's endpoints
/// use, so their validation, versions, ceilings, package locks, four eyes and audit apply unchanged, with
/// the person who chats as the actor. A call works only in a person's own chat with the agent builder,
/// never in an agent's run, a consultation or outside a turn, and never on the agent builder itself. The
/// invoker already authorized the tool's policy for that person and, for a write, checked their approval.
/// No call takes or returns a secret: MCP servers are read without their connections and credentials.
/// </summary>
internal sealed partial class NhAssistantAgentBuilderToolHandler(
    IServiceProvider services,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantAgentBuilderToolHandler> logger)
{
    /// <summary>The most tools the catalog lists in one result.</summary>
    public const int MaxCatalogTools = 300;

    /// <summary>The longest description of a tool the catalog lists.</summary>
    public const int MaxToolDescriptionLength = 300;

    /// <summary>The longest query of an account search, as the administration's.</summary>
    public const int MaxAccountQueryLength = 100;

    /// <summary>The largest result of a gateway read the builder passes on, in characters of JSON.</summary>
    public const int MaxGatewayResultLength = 48_000;

    /// <summary>The most recent failed runs of an agent whose codes its blockers explain.</summary>
    private const int MaxRecentFailedRuns = 10;

    /// <summary>The most result fields a described resource lists when it does not fit as a whole.</summary>
    private const int MaxGatewayResultFields = 100;

    /// <summary>
    /// How results reach the model: camelCase, enums by name, without empty values.
    /// </summary>
    internal static readonly JsonSerializerOptions ResultOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    private INhAssistantAgentConfiguration Configuration => services.GetRequiredService<INhAssistantAgentConfiguration>();

    private INhAssistantAgentDefinitions Definitions => services.GetRequiredService<INhAssistantAgentDefinitions>();

    private INhAssistantAgentDirectory Directory => services.GetRequiredService<INhAssistantAgentDirectory>();

    private INhAssistantAgentControl Control => services.GetRequiredService<INhAssistantAgentControl>();

    private INhAssistantRuleScripts RuleScripts => services.GetRequiredService<INhAssistantRuleScripts>();

    private INhAssistantMcpServers McpServers => services.GetRequiredService<INhAssistantMcpServers>();

    private INhAssistantDataHandOffs HandOffs => services.GetRequiredService<INhAssistantDataHandOffs>();

    private INhAssistantWithoutApprovalSwitches WithoutApproval => services.GetRequiredService<INhAssistantWithoutApprovalSwitches>();

    private INhAssistantAlertModelSwitches AlertModel => services.GetRequiredService<INhAssistantAlertModelSwitches>();

    private INhAssistantAgentGroups Groups => services.GetRequiredService<INhAssistantAgentGroups>();

    /// <summary>
    /// Runs one call: in a person's chat with the agent builder only, with input that the tool takes.
    /// </summary>
    public async Task<TaskResult<JsonNode>> HandleAsync(
        NhAssistantAgentBuilderTool tool,
        JsonElement value,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);
        if (PersonOf() is not { } person)
        {
            logger.LogWarning(
                "The agent builder's tool {ToolId} was refused outside a person's chat with the agent builder.",
                tool.Id);
            return Refused(NhAssistantAdminErrorCodes.Forbidden, "The agent builder's tools work only in a person's chat with the agent builder.");
        }

        var (input, unknown) = NhAssistantAgentBuilderInput.Read(value, tool.Input.Properties);
        if (input is null)
        {
            return Refused(NhAiToolFailureCodes.InputInvalid, UnknownInputMessage(unknown!));
        }

        try
        {
            return await tool.Handle(this, new NhAssistantAgentBuilderCall(person, input), cancellationToken);
        }
        catch (NhAssistantAgentBuilderInputException wrong)
        {
            return Refused(NhAiToolFailureCodes.InputInvalid, $"'{wrong.Property}' has the wrong type. Send it as its schema says.");
        }
    }

    /// <summary>
    /// What the model reads when a call names a property its tool does not take.
    /// </summary>
    internal static string UnknownInputMessage(string property)
    {
        return $"The tool does not take '{property}'. Use only the properties its schema names; the agent builder never takes or handles secrets.";
    }
    /// <summary>
    /// The person of the running turn when it is their own chat with the agent builder: not an agent's run
    /// or thread, a consultation or a shadow run; <see langword="null"/> otherwise.
    /// </summary>
    private static string? PersonOf()
    {
        var turn = NhAssistantExecutionScope.CurrentTurn;
        if (turn is null
            || !NhAssistantAgentBuilderIds.IsAgentBuilder(turn.Agent.Id)
            || turn.Consultation is not null
            || turn.RunId is not null
            || turn.ShadowRun
            || NhAiAgentInvocationScopes.Current is not null)
        {
            return null;
        }

        return turn.OwnerActorId;
    }

    // ----- Reads -----

    public async Task<TaskResult<JsonNode>> ReadCatalogAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var query = call.Input.Text("query");
        var tools = new List<CatalogTool>();
        foreach (var listed in services.GetServices<INhAiToolCatalog>())
        {
            // A catalog that changes at runtime reads its source first, as the administration's list does.
            if (listed is INhAiChangingToolCatalog changing)
            {
                await changing.RefreshAsync(cancellationToken);
            }

            tools.AddRange(NhAiToolCatalogViews.Of(listed).Descriptors
                .Where(descriptor => (descriptor.Exposure & NhAiToolExposure.Agent) != 0)
                .Where(descriptor => !NhAssistantAgentBuilderIds.IsAgentBuilderTool(descriptor.Id))
                .Select(descriptor => new CatalogTool(
                    descriptor.Id,
                    EffectName(descriptor.Effect),
                    NhAssistantContent.Bound(descriptor.Description, MaxToolDescriptionLength),
                    descriptor.Subject?.Type)
                {
                    AllowWithoutApproval = NhAiWithoutApproval.IsAllowedBy(descriptor) ? true : null
                }));
        }

        var matching = tools
            .DistinctBy(tool => tool.Id, StringComparer.Ordinal)
            .Where(tool => query is null
                || tool.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tool.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tool => tool.Id, StringComparer.Ordinal)
            .ToArray();
        var run = Configuration.GetCatalog();
        var alertKinds = registration.AlertKinds.Values
            .OrderBy(kind => kind.Id, StringComparer.Ordinal)
            .Select(kind => new CatalogAlertKind(
                kind.Id,
                kind.DisplayNameKey,
                kind.Urgency.ToString().ToLowerInvariant(),
                kind.ModelTemplate?.Description))
            .ToArray();
        var gateway = services.GetService<INhAiGatewayResources>();
        return Ok(new
        {
            tools = matching.Take(MaxCatalogTools).ToArray(),
            toolsLeftOut = Math.Max(0, matching.Length - MaxCatalogTools),
            run.TriggerEvents,
            run.SubjectSets,
            run.SubjectTypes,
            run.RuleHandlers,
            approvalPolicies = run.ApprovalPolicySummaries.Count > 0 ? (object)run.ApprovalPolicySummaries : run.ApprovalPolicies,
            run.Profiles,
            run.AgentPolicies,
            run.Divisions,
            run.CompositionCeiling,
            alertKinds,
            agentGroups = Groups.ListGroups().Select(group => new { group.Id, group.Name }).ToArray(),
            ruleScripts = RuleScriptCeilingOf(),
            gateway = gateway is null
                ? null
                : new { tools = gateway.ToolIds.Order(StringComparer.Ordinal).ToArray() },
            mcpServers = await McpServersAsync(cancellationToken)
        });
    }

    public async Task<TaskResult<JsonNode>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        var agents = await Directory.ListAsync(null, cancellationToken);
        return Ok(new
        {
            agents = agents
                .Where(agent => !NhAssistantAgentBuilderIds.IsAgentBuilder(agent.AgentId))
                .Select(agent => new
                {
                    agent.AgentId,
                    agent.Source,
                    agent.Version,
                    agent.DisplayName,
                    agent.Description,
                    agent.Autonomy,
                    agent.RunState,
                    agent.ChatOnly,
                    agent.InShadow,
                    agent.ShadowOf,
                    agent.OfferedInChat
                })
                .ToArray()
        });
    }

    public async Task<TaskResult<JsonNode>> ReadAgentAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (await Directory.GetAsync(agentId, cancellationToken) is not { } agent)
        {
            return Refused(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        var configuration = await Configuration.GetAsync(agentId, cancellationToken);
        var ruleScript = await RuleScripts.GetAsync(agentId, null, cancellationToken);
        var worksOn = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(agentId, includeDisabled: true, cancellationToken))?.WorksOn;
        var metadata = await Configuration.GetMetadataAsync(agentId, cancellationToken);
        var handOffs = await HandOffs.GetAsync(agentId, cancellationToken);
        var shadowVariants = await services.GetRequiredService<NhAssistantShadowVariants>().ListAsync(agentId, cancellationToken);
        var withoutApproval = await WithoutApproval.GetAsync(agentId, cancellationToken);
        var alertModel = await AlertModel.GetAsync(agentId, cancellationToken);
        var groups = await Groups.GetMembershipAsync(agentId, cancellationToken);
        return Ok(new
        {
            agent,
            worksOn,
            groups = groups.Success ? groups.Data : null,
            configuration,
            metadata = metadata.Success ? metadata.Data : null,
            handOffs,
            shadowVariants,
            withoutApproval,
            alertModel,
            ruleScript = ruleScript is null
                ? null
                : new
                {
                    ruleScript.Allowed,
                    ruleScript.Revision,
                    ruleScript.LiveVersion,
                    ruleScript.CandidateVersion,
                    ruleScript.CandidateShadow,
                    ruleScript.CodeRule
                },
            blockers = await BlockersAsync(agent, configuration, ruleScript, cancellationToken)
        });
    }

    public async Task<TaskResult<JsonNode>> ReadRuleScriptAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }

        var skillId = call.Input.Text("skillId");
        var ruleScript = await RuleScripts.GetAsync(agentId, skillId, cancellationToken);
        if (ruleScript is null)
        {
            return Refused(NhAssistantErrorCodes.AgentNotFound, "The agent or skill was not found.");
        }
        if (!ruleScript.Allowed)
        {
            return Refused(NhAssistantRuleScriptCodes.NotAllowed, "The code does not let this agent have rule scripts.");
        }

        var catalog = await RuleScripts.GetCatalogAsync(agentId, skillId, call.Input.Texts("writes"), cancellationToken);
        if (!catalog.Success)
        {
            return From(catalog, NhAssistantRuleScriptCodes.NotAllowed);
        }

        var candidateDryRun = ruleScript.CandidateVersion is { } candidate
            ? await RuleScripts.GetDryRunAsync(agentId, skillId, candidate, cancellationToken)
            : null;
        return Ok(new
        {
            ruleScript.AgentId,
            ruleScript.SkillId,
            ruleScript.Revision,
            ruleScript.LiveVersion,
            ruleScript.CandidateVersion,
            ruleScript.CandidateShadow,
            ruleScript.CodeRule,
            ruleScript.Ceiling,
            // The live version and the candidate in full; the history by number and status.
            versions = ruleScript.Versions
                .Where(version => version.Version == ruleScript.LiveVersion || version.Version == ruleScript.CandidateVersion)
                .ToArray(),
            history = ruleScript.Versions
                .Where(version => version.Version != ruleScript.LiveVersion && version.Version != ruleScript.CandidateVersion)
                .Select(version => new { version.Version, version.Status, version.ScriptHash, version.CreatedAt })
                .ToArray(),
            candidateDryRun,
            catalog = catalog.Data
        });
    }

    public async Task<TaskResult<JsonNode>> SearchAccountsAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        var accounts = services.GetRequiredService<NhAssistantAccounts>();
        var ids = call.Input.Texts("ids");
        var found = ids is { Count: > 0 }
            ? await accounts.FindAsync(string.Join(',', ids), cancellationToken)
            : await accounts.SearchAsync(call.Input.Text("query"), call.Input.Int("limit"), cancellationToken);
        return found.Success ? Ok(new { accounts = found.Data }) : From(found, NhAssistantAdminErrorCodes.ValidationFailed);
    }

    public async Task<TaskResult<JsonNode>> ReadGatewayAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        if (services.GetService<INhAiGatewayResources>() is not { } gateway || gateway.ToolIds.Count == 0)
        {
            return Ok(new
            {
                gateway = false,
                message = "The application has no API gateway, so agents cannot read its data through one; they use the application's own tools from the catalog."
            });
        }

        if (call.Input.Text("resource") is { } resource)
        {
            var described = await gateway.DescribeAsync(resource, cancellationToken);
            return described.Success
                ? Ok(new { gateway = true, resource = BoundDescription(described.Data) })
                : From(described, NhAiToolFailureCodes.InputInvalid);
        }

        if (call.Input.Text("query") is not { } query)
        {
            return Refused(NhAiToolFailureCodes.InputInvalid, "Give query to search the resources, or resource to describe one.");
        }

        var limit = Math.Clamp(call.Input.Int("limit") ?? 10, 1, 20);
        var found = await gateway.SearchAsync(query, limit, cancellationToken);
        return found.Success
            ? Ok(new { gateway = true, resources = found.Data })
            : From(found, NhAiToolFailureCodes.InputInvalid);
    }

    // ----- Dry runs -----

    public async Task<TaskResult<JsonNode>> DryRunRuleScriptAsync(NhAssistantAgentBuilderCall call, CancellationToken cancellationToken)
    {
        if (TargetOf(call) is not { } agentId)
        {
            return TargetRefused(call);
        }
        if (call.Input.Int("version") is not { } version)
        {
            return Missing("version");
        }

        var subjects = (call.Input.Objects("subjects") ?? [])
            .Select(subject => new NhAssistantRuleScriptSubjectDto(
                subject.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : string.Empty,
                subject.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : string.Empty))
            .ToArray();
        var dryRun = await RuleScripts.DryRunAsync(
            agentId,
            call.Input.Text("skillId"),
            version,
            new NhAssistantRuleScriptDryRunRequest(subjects),
            call.Author,
            cancellationToken);
        return dryRun.Success ? Ok(dryRun.Data!) : From(dryRun, NhAssistantRuleScriptCodes.SubjectsInvalid);
    }

    // ----- Helpers -----

    /// <summary>
    /// The MCP servers an agent may be assigned, without their address, connection, references or
    /// credentials: the builder assigns existing servers and never handles secrets.
    /// </summary>
    private async Task<object[]> McpServersAsync(CancellationToken cancellationToken)
    {
        return (await McpServers.ListAsync(cancellationToken))
            .Select(server => (object)new
            {
                server.Id,
                server.DisplayName,
                server.IsEnabled,
                server.AuthMode,
                server.HeadlessUse,
                server.RequiredPolicy,
                server.AssignedAgentIds
            })
            .ToArray();
    }

    /// <summary>
    /// The blockers of the agent, with the gateway's tools and the alert kinds its rules may raise.
    /// </summary>
    private async Task<IReadOnlyList<NhAssistantAgentBuilderBlocker>> BlockersAsync(
        NhAssistantAgentInEffectDto agent,
        NhAssistantAgentConfigurationDto? configuration,
        NhAssistantRuleScriptDto? ruleScript,
        CancellationToken cancellationToken)
    {
        if (services.GetService<NhAssistantRunConfigurations>() is { } configurations)
        {
            await configurations.EnsureCurrentAsync(cancellationToken);
        }

        var scripts = new List<string>();
        foreach (var skillId in new string?[] { null }.Concat(configuration?.DataSkills.Select(skill => skill.SkillId) ?? []))
        {
            var script = skillId is null ? ruleScript : await RuleScripts.GetAsync(agent.AgentId, skillId, cancellationToken);
            scripts.AddRange((script?.Versions ?? [])
                .Where(version => version.Version == script!.LiveVersion || version.Version == script.CandidateVersion)
                .Select(version => version.Script));
        }

        var facts = new NhAssistantAgentBuilderBlockerFacts(
            services.GetService<INhAiGatewayResources>()?.ToolIds ?? [],
            services.GetService<INhAiDelegatedReadCredentialIssuer>() is not null && services.GetServices<INhAiDelegatedReadSurface>().Any(),
            // The kinds in code, and those the run settings of an agent created in the administration list.
            registration.RunOptionsOf(agent.AgentId).AlertKinds
                .Concat(configuration?.RunSettings?.AlertKinds ?? [])
                .Concat(configuration?.CodeRunSettings?.AlertKinds ?? [])
                .ToHashSet(StringComparer.Ordinal),
            scripts,
            await RecentFailureCodesAsync(agent.AgentId, cancellationToken));
        return NhAssistantAgentBuilderBlockers.Of(agent, configuration, facts);
    }

    /// <summary>
    /// The codes the agent's most recent failed runs ended with, newest first, as its runs view lists them.
    /// </summary>
    private async Task<IReadOnlyList<string>> RecentFailureCodesAsync(string agentId, CancellationToken cancellationToken)
    {
        var query = NhAssistantRunHistory.ReadQuery(null, null, null, NhAssistantRunStatuses.Failed, null, null, 1, MaxRecentFailedRuns);
        var failed = await services.GetRequiredService<NhAssistantRunHistory>().ListForAgentAsync(agentId, query.Data!, cancellationToken);
        return failed.Success
            ? failed.Data!.Items.Select(run => run.FailureCode).OfType<string>().ToArray()
            : [];
    }

    /// <summary>
    /// A described resource as the model reads it: whole when it fits, otherwise its query shape and the
    /// keys of its first result fields.
    /// </summary>
    private static JsonNode BoundDescription(JsonElement description)
    {
        var whole = JsonNode.Parse(description.GetRawText())!;
        if (description.GetRawText().Length <= MaxGatewayResultLength || whole is not JsonObject full)
        {
            return whole;
        }

        var bounded = new JsonObject
        {
            ["resource"] = full["resource"]?.DeepClone(),
            ["title"] = full["title"]?.DeepClone(),
            ["query"] = full["query"]?.DeepClone(),
            ["get"] = full["get"]?.DeepClone(),
            ["truncated"] = true
        };
        if (full["resultFields"] is JsonArray fields)
        {
            bounded["resultFieldKeys"] = new JsonArray(fields
                .Take(MaxGatewayResultFields)
                .Select(field => (JsonNode?)JsonValue.Create(field?["key"]?.GetValue<string>()))
                .ToArray());
        }

        return bounded;
    }

    /// <summary>
    /// What the code's ceiling of rule scripts allows, for the catalog; <see langword="null"/> while no agent
    /// may have rule scripts.
    /// </summary>
    private object? RuleScriptCeilingOf()
    {
        if (registration.RuleScriptsAsData is not { } ceiling)
        {
            return null;
        }

        return new
        {
            agents = ceiling.Agents
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new
                {
                    agentId = item.Key,
                    writes = item.Value.Writes.Order(StringComparer.Ordinal).ToArray(),
                    item.Value.MaxWritesPerRun
                })
                .ToArray(),
            agentsCreatedInAdministration = ceiling.AgentsCreatedInAdministration is { } admin
                ? new { writes = admin.Writes.Order(StringComparer.Ordinal).ToArray(), admin.MaxWritesPerRun }
                : null,
            ceiling.RequireFourEyes
        };
    }

    /// <summary>
    /// The agent a call names: <see langword="null"/> when it names none or names the agent builder, which
    /// never works on itself.
    /// </summary>
    private static string? TargetOf(NhAssistantAgentBuilderCall call)
    {
        var agentId = call.Input.Text("agentId");
        return agentId is null || NhAssistantAgentBuilderIds.IsAgentBuilder(agentId) ? null : agentId;
    }

    private static TaskResult<JsonNode> TargetRefused(NhAssistantAgentBuilderCall call)
    {
        return call.Input.Text("agentId") is null
            ? Missing("agentId")
            : Itself();
    }

    private static TaskResult<JsonNode> Itself()
    {
        return Refused(NhAssistantAdminErrorCodes.Forbidden, "The agent builder never changes or reads itself; name another agent.");
    }

    private static (NhAiAutonomyLevel? Value, TaskResult<JsonNode>? Refused) AutonomyOf(NhAssistantAgentBuilderInput input)
    {
        return input.Text("autonomy") switch
        {
            null => (null, null),
            "observe" => (NhAiAutonomyLevel.Observe, null),
            "explain" => (NhAiAutonomyLevel.Explain, null),
            "propose" => (NhAiAutonomyLevel.Propose, null),
            "simulate" => (NhAiAutonomyLevel.Simulate, null),
            "execute" => (NhAiAutonomyLevel.Execute, null),
            _ => (null, Invalid("autonomy"))
        };
    }

    private static TaskResult<JsonNode>? Required(NhAssistantAgentBuilderInput input, params string[] names)
    {
        var missing = names.Where(name => !input.Has(name)).ToArray();
        return missing.Length == 0
            ? null
            : Refused(NhAiToolFailureCodes.InputInvalid, "Give " + string.Join(", ", missing) + ".");
    }

    private static int? PickInt(NhAssistantAgentBuilderInput input, string name, int? current)
    {
        return input.Given(name) ? input.Int(name) : current;
    }

    private static long? PickLong(NhAssistantAgentBuilderInput input, string name, long? current)
    {
        return input.Given(name) ? input.Long(name) : current;
    }

    private static string? Pick(NhAssistantAgentBuilderInput input, string name, string? current)
    {
        return input.Given(name) ? input.Text(name) : current;
    }

    private static bool PickBool(NhAssistantAgentBuilderInput input, string name, bool current)
    {
        return input.Bool(name) ?? current;
    }

    private static TaskResult<JsonNode> Missing(string name)
    {
        return Refused(NhAiToolFailureCodes.InputInvalid, $"Give {name}.");
    }

    private static TaskResult<JsonNode> Invalid(string name)
    {
        return Refused(NhAiToolFailureCodes.InputInvalid, $"'{name}' is not one of the values its schema names.");
    }

    private static TaskResult<JsonNode> Refused(string code, string message)
    {
        return TaskResult<JsonNode>.Failed(code, message);
    }

    /// <summary>
    /// A failure of a service as the model reads it: its code, its message and its field errors.
    /// </summary>
    private static TaskResult<JsonNode> From(TaskResult result, string fallback)
    {
        var code = NhAssistantResults.CodeOf(result, fallback);
        var message = result.GetResultItems()
            .Where(item => item.Name == code)
            .SelectMany(item => item.ErrorMessages)
            .Select(item => item.ToString())
            .FirstOrDefault() ?? "The administration refused the change.";
        var fields = NhAssistantResults.FieldErrorsOf(result);
        if (fields.Count > 0)
        {
            message += " Fields: " + string.Join("; ", fields.Select(field => field.Key + ": " + string.Join(", ", field.Value))) + ".";
        }

        return Refused(code, message);
    }

    /// <summary>
    /// The outcome of a change as the model reads it: what the service returned, or its refusal.
    /// </summary>
    private static TaskResult<JsonNode> Outcome<T>(TaskResult<T> result, Func<T, object> success, string fallback = NhAssistantAdminErrorCodes.ValidationFailed)
    {
        return result.Success ? Ok(success(result.Data!)) : From(result, fallback);
    }

    private static TaskResult<JsonNode> Ok(object value)
    {
        return TaskResult<JsonNode>.Succeeded(JsonSerializer.SerializeToNode(value, value.GetType(), ResultOptions)!);
    }

    private static string EffectName(NhAiToolEffect effect)
    {
        return effect switch
        {
            NhAiToolEffect.ReadOnly => "read-only",
            NhAiToolEffect.IdempotentMutation => "idempotent-mutation",
            NhAiToolEffect.Mutation => "mutation",
            NhAiToolEffect.ExternalSideEffect => "external-side-effect",
            _ => "destructive"
        };
    }

    private sealed record CatalogTool(string Id, string Effect, string Description, string? SubjectType)
    {
        /// <summary>Whether its code lets an agent's own runs call it without approval.</summary>
        public bool? AllowWithoutApproval { get; init; }
    }

    private sealed record CatalogAlertKind(string Id, string NameKey, string Urgency, string? ModelTemplate);
}
