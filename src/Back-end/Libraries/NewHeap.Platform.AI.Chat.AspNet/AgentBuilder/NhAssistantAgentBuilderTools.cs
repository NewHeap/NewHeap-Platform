using System.Text.Json.Nodes;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// The tools of the agent builder (RB-8). Reads need no approval. A dry run changes nothing and needs none
/// either. Every save and every step live is a governed write that waits for the administrator's approval
/// in the chat, and the approval card says what changes. There is no tool for grants, technical accounts,
/// MCP servers or credentials, the code's ceilings or a deletion.
/// </summary>
internal static class NhAssistantAgentBuilderTools
{
    public const string ReadCatalog = NhAssistantAgentBuilderIds.ToolIdPrefix + "read-catalog";
    public const string ListAgents = NhAssistantAgentBuilderIds.ToolIdPrefix + "list-agents";
    public const string ReadAgent = NhAssistantAgentBuilderIds.ToolIdPrefix + "read-agent";
    public const string ReadRuleScript = NhAssistantAgentBuilderIds.ToolIdPrefix + "read-rule-script";
    public const string DryRunRuleScript = NhAssistantAgentBuilderIds.ToolIdPrefix + "dry-run-rule-script";
    public const string CreateAgent = NhAssistantAgentBuilderIds.ToolIdPrefix + "create-agent";
    public const string UpdateAgent = NhAssistantAgentBuilderIds.ToolIdPrefix + "update-agent";
    public const string SaveRunSettings = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-run-settings";
    public const string SaveSkill = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-skill";
    public const string SaveTrigger = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-trigger";
    public const string SaveRuleScript = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-rule-script";
    public const string SetLive = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-live";
    public const string SetShadow = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-shadow";

    /// <summary>What <see cref="SetLive"/> sets live.</summary>
    public static class LiveParts
    {
        public const string RuleScript = "rule-script";
        public const string SkillCandidate = "skill-candidate";
        public const string TriggerCandidate = "trigger-candidate";
    }

    /// <summary>What <see cref="SetShadow"/> puts in shadow or takes out of it.</summary>
    public static class ShadowParts
    {
        public const string RuleScript = "rule-script";
        public const string Skill = "skill";
        public const string Trigger = "trigger";
        public const string Agent = "agent";
    }

    private static readonly string[] Autonomies = ["observe", "explain", "propose", "simulate", "execute"];

    /// <summary>
    /// Every tool, in the order the model reads them.
    /// </summary>
    public static IReadOnlyList<NhAssistantAgentBuilderTool> All { get; } =
    [
        Read(
            ReadCatalog,
            "builder_read_catalog",
            "Lists the building blocks the application registered in code that an agent may use: tools with their effect and description, trigger events with their filters, object types, subject sets, rule handlers, approval policies, model profiles, agent policies, alert kinds, and which agents may have rule scripts with the writes the code allows. Changes nothing.",
            Schema.Object([("query", Schema.Text("Optional words that the tools must contain in their id or description, to narrow a long list.", 200), false)]),
            (handler, call, token) => handler.ReadCatalogAsync(call, token)),
        Read(
            ListAgents,
            "builder_list_agents",
            "Lists the agents of the application with their id, name, description, where they come from (code or the administration), autonomy, status and whether they only chat. Changes nothing.",
            Schema.Object([]),
            (handler, _, token) => handler.ListAgentsAsync(token)),
        Read(
            ReadAgent,
            "builder_read_agent",
            "Reads one agent in full: its definition with instructions, tools and version; its run settings, skills and triggers in code and as data with their versions and candidates; and its rule scripts. Changes nothing.",
            Schema.Object([("agentId", Schema.Id("The agent's id."), true)]),
            (handler, call, token) => handler.ReadAgentAsync(call, token)),
        Read(
            ReadRuleScript,
            "builder_read_rule_script",
            "Reads the rule script of an agent's own rules or of one of its skills: its revision, live version and candidate with their scripts and writes, the latest dry run of the candidate, the code's ceiling (writes, writes per run, four eyes), and what a script may call: every tool with its function name, TypeScript-style signature and whether it writes, the alert kinds and the platform functions. Changes nothing.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("skillId", Schema.Id("The skill, for a skill's rule script; leave it out for the agent's own rules."), false),
                ("writes", Schema.Texts("The writing tools, by tool id, a version would opt in to; by default every write of the ceiling."), false)
            ]),
            (handler, call, token) => handler.ReadRuleScriptAsync(call, token)),
        new(
            DryRunRuleScript,
            "builder_dry_run_rule_script",
            "Runs a saved version of a rule script on one to twenty real subjects without effects and returns what it would do on each: its calls, writes, proposals, alerts raised and resolved, its end and its log. Nothing is written, proposed, raised or delivered. The result is kept as the version's latest dry run.",
            NhAiToolEffect.IdempotentMutation,
            NhAiApprovalRequirement.NotRequired,
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("skillId", Schema.Id("The skill, for a skill's rule script."), false),
                ("version", Schema.Integer("The version to run."), true),
                ("subjects", Schema.Subjects(), true)
            ]),
            (handler, call, token) => handler.DryRunRuleScriptAsync(call, token)),
        Write(
            CreateAgent,
            "builder_create_agent",
            "Creates a new agent in the administration, after the administrator approves it in the chat. It starts without run settings, skills and triggers, which later steps add. Tool selectors are tool ids or a prefix ending in .*, chosen from the catalog.",
            Schema.Object([
                ("agentId", Schema.Id("A new, lowercase dash-case id."), true),
                ("displayName", Schema.Text("The name people see.", 256), true),
                ("description", Schema.Text("What the agent does, in one or two sentences.", 512), true),
                ("instructions", Schema.Text("The agent's instructions for its model.", 20_000), true),
                ("toolSelectors", Schema.Texts("The tools the agent may use."), true),
                ("autonomy", Schema.Choice("How far the agent may act on its own; propose by default.", Autonomies), false),
                ("worksOnSubjectTypes", Schema.Texts("The object types the agent works on, from the catalog."), false),
                ("worksOnAllSubjects", Schema.Flag("The agent works on every object type."), false),
                ("worksOnApplication", Schema.Flag("The agent works on the whole application."), false),
                ("offeredInChat", Schema.Flag("People can choose the agent in the chat; true by default."), false),
                ("startsInShadow", Schema.Flag("Its runs work in shadow, without effects, until it is put live."), false),
                ("profile", Schema.Text("A model profile from the catalog; the application's chat profile by default.", 128), false),
                ("requiredPolicy", Schema.Text("The policy a person needs to see and use the agent; none by default.", 256), false),
                ("dataClassification", Schema.Choice("The classification of the data its model calls carry; internal by default.", ["public", "internal", "confidential", "restricted"]), false)
            ]),
            (handler, call, token) => handler.CreateAgentAsync(call, token)),
        Write(
            UpdateAgent,
            "builder_update_agent",
            "Changes an agent's definition, after the administrator approves it in the chat: its name, description, instructions, tool selectors, autonomy or whether it is offered in the chat. Fields left out keep their value. expectedVersion is the version builder_read_agent showed. An agent in code gets an override.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("expectedVersion", Schema.Integer("The agent's current version."), true),
                ("displayName", Schema.Text("The new name.", 256), false),
                ("description", Schema.Text("The new description.", 512), false),
                ("instructions", Schema.Text("The new instructions, in full.", 20_000), false),
                ("toolSelectors", Schema.Texts("The new tool selectors, in full."), false),
                ("autonomy", Schema.Choice("The new autonomy.", Autonomies), false),
                ("offeredInChat", Schema.Flag("For an agent created in the administration: whether people can choose it in the chat."), false)
            ]),
            (handler, call, token) => handler.UpdateAgentAsync(call, token)),
        Write(
            SaveRunSettings,
            "builder_save_run_settings",
            "Adds or changes the run settings of an agent created in the administration, after the administrator approves it in the chat: approval policy, retries, daily budgets, concurrency, runs per subject per day, the token ceiling of a run, triage and the rule handler. Settings left out keep their value. Whose rights the agent uses (accountability, technical account, delegation, gateway and elevated reads) stays as it is; new settings answer to the agent itself.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("expectedVersion", Schema.Integer("The settings' current version; leave it out for new settings."), false),
                ("approvalPolicy", Schema.Text("An approval policy from the catalog.", 128), false),
                ("retryCount", Schema.Integer("Automatic retries after a transient failure, 0 to 10."), false),
                ("dailyToolCalls", Schema.Integer("With dailyTokens, the daily budget of tool calls per scope."), false),
                ("dailyTokens", Schema.Integer("With dailyToolCalls, the daily budget of tokens per scope."), false),
                ("maxConcurrentRuns", Schema.Integer("1 to 100."), false),
                ("maxRunsPerSubjectPerDay", Schema.Integer("1 to 1,000."), false),
                ("maxTokensPerRun", Schema.Integer("The token ceiling of one run."), false),
                ("triageProfile", Schema.Text("A model profile that chooses the skill of a run no trigger routed.", 128), false),
                ("ruleHandler", Schema.Text("A rule handler from the catalog.", 128), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SaveRunSettingsAsync(call, token)),
        Write(
            SaveSkill,
            "builder_save_skill",
            "Adds a data skill or saves its next version, after the administrator approves it in the chat. Send the whole skill. A new skill starts in shadow; a change of a skill applies live or, with apply shadow, becomes its candidate next to the live version. expectedVersion is the live version the change is made on, left out for a new skill.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("skillId", Schema.Id("The skill's id, lowercase dash-case."), true),
                ("expectedVersion", Schema.Integer("The skill's live version; leave it out for a new skill."), false),
                ("description", Schema.Text("What the skill does.", 512), true),
                ("instructions", Schema.Text("The skill's instructions for its model.", 20_000), false),
                ("tools", Schema.Texts("The tool selectors of the skill, within the agent's own."), false),
                ("ruleHandler", Schema.Text("A rule handler from the catalog the skill works by.", 128), false),
                ("profile", Schema.Text("A model profile from the catalog.", 128), false),
                ("approvalPolicy", Schema.Text("An approval policy from the catalog.", 128), false),
                ("maxTokensPerRun", Schema.Integer("The token ceiling of one run in the skill."), false),
                ("dailyToolCalls", Schema.Integer("With dailyTokens, the skill's daily budget of tool calls."), false),
                ("dailyTokens", Schema.Integer("With dailyToolCalls, the skill's daily budget of tokens."), false),
                ("apply", Schema.Choice("For a change: live (default) or shadow, as a candidate.", [NhAssistantSkillApplyModes.Live, NhAssistantSkillApplyModes.Shadow]), false),
                ("dataClassification", Schema.Choice("The classification of the data its model calls carry.", ["public", "internal", "confidential", "restricted"]), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SaveSkillAsync(call, token)),
        Write(
            SaveTrigger,
            "builder_save_trigger",
            "Adds a data trigger or saves its next version, after the administrator approves it in the chat. Send the whole trigger: an event trigger names a trigger event and may select its filters; a schedule names a cron expression and a subject set from the catalog (without one it runs for the whole application). A new trigger starts in shadow; a change applies live or, with apply shadow, becomes its candidate.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("triggerId", Schema.Id("The trigger's id, lowercase dash-case."), true),
                ("expectedVersion", Schema.Integer("The trigger's live version; leave it out for a new trigger."), false),
                ("source", Schema.Choice("event or schedule.", ["event", "schedule"]), true),
                ("skill", Schema.Id("The skill the runs work in; leave it out to let the agent's rules or triage choose."), false),
                ("event", Schema.Text("For an event trigger: a trigger event from the catalog.", 128), false),
                ("filters", Schema.Texts("For an event trigger: names of the event's filters that must all accept an event."), false),
                ("cron", Schema.Text("For a schedule: a five- or six-field cron expression.", 128), false),
                ("timeZone", Schema.Text("For a schedule: an IANA time zone id; UTC by default.", 128), false),
                ("subjectSet", Schema.Text("For a schedule: a subject set from the catalog.", 128), false),
                ("message", Schema.Text("For a schedule: what every run receives.", 2_000), false),
                ("conflict", Schema.Choice("What happens while a run on the subject is active.", ["coalesce", "queue", "supersede", "ignore"]), false),
                ("debounceSeconds", Schema.Integer("For an event trigger: the window in which events coalesce, at most 3,600."), false),
                ("apply", Schema.Choice("For a change: live (default) or shadow, as a candidate.", [NhAssistantSkillApplyModes.Live, NhAssistantSkillApplyModes.Shadow]), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SaveTriggerAsync(call, token)),
        Write(
            SaveRuleScript,
            "builder_save_rule_script",
            "Saves the next version of the rule script of an agent's own rules or of one of its skills, after the administrator approves it in the chat. It is a candidate unless live is true; a candidate may run in shadow at once. The script is the body of an async JavaScript function that calls only the tools builder_read_rule_script lists; writes are the writing tools it may call, within the code's ceiling. expectedRevision is the rule script's revision, 0 before its first version. Under four eyes nothing is saved straight live.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("skillId", Schema.Id("The skill, for a skill's rule script."), false),
                ("script", Schema.Text("The script.", NhAssistantRuleScriptLimits.MaxScriptLength), true),
                ("writes", Schema.Texts("The writing tools the version may call, by tool id; none by default."), false),
                ("expectedRevision", Schema.Integer("The rule script's current revision."), true),
                ("live", Schema.Flag("Save it straight live instead of as a candidate."), false),
                ("shadow", Schema.Flag("Let the candidate run in shadow at once."), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SaveRuleScriptAsync(call, token)),
        Write(
            SetLive,
            "builder_set_live",
            "Sets a version live, after the administrator approves it in the chat: a rule script's candidate (with expectedRevision), a data skill's candidate or a data trigger's candidate (with skillId or triggerId). Under four eyes the person who saved a rule script version cannot set it live.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("part", Schema.Choice("What goes live.", [LiveParts.RuleScript, LiveParts.SkillCandidate, LiveParts.TriggerCandidate]), true),
                ("version", Schema.Integer("The candidate's version."), true),
                ("skillId", Schema.Id("The skill: of a skill candidate, or of a skill's rule script."), false),
                ("triggerId", Schema.Id("The trigger of a trigger candidate."), false),
                ("expectedRevision", Schema.Integer("For a rule script: its current revision."), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SetLiveAsync(call, token)),
        Write(
            SetShadow,
            "builder_set_shadow",
            "Puts something in shadow or takes it out, after the administrator approves it in the chat: a rule script's candidate runs in shadow next to the live rules (with version and expectedRevision), or a skill, a data trigger or the agent works in shadow without effects. shadow false puts a skill, trigger or agent live and stops a rule script candidate's shadow runs.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("part", Schema.Choice("What changes.", [ShadowParts.RuleScript, ShadowParts.Skill, ShadowParts.Trigger, ShadowParts.Agent]), true),
                ("shadow", Schema.Flag("true for shadow, false to stop it."), true),
                ("skillId", Schema.Id("The skill: of a skill, or of a skill's rule script."), false),
                ("triggerId", Schema.Id("The trigger."), false),
                ("version", Schema.Integer("For a rule script: the candidate's version."), false),
                ("expectedRevision", Schema.Integer("For a rule script: its current revision."), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SetShadowAsync(call, token))
    ];

    /// <summary>
    /// Whether <paramref name="toolId"/> is one of the tools that save or set something live, each of which
    /// waits for the administrator's approval.
    /// </summary>
    public static bool IsWrite(string toolId)
    {
        return All.Any(tool => tool.Id == toolId && tool.Approval == NhAiApprovalRequirement.Required);
    }

    private static NhAssistantAgentBuilderTool Read(
        string id,
        string exportName,
        string description,
        (JsonObject Schema, string[] Properties) schema,
        Func<NhAssistantAgentBuilderToolHandler, NhAssistantAgentBuilderCall, CancellationToken, Task<TaskResult<System.Text.Json.Nodes.JsonNode>>> handle)
    {
        return new NhAssistantAgentBuilderTool(id, exportName, description, NhAiToolEffect.ReadOnly, NhAiApprovalRequirement.NotRequired, schema, handle);
    }

    private static NhAssistantAgentBuilderTool Write(
        string id,
        string exportName,
        string description,
        (JsonObject Schema, string[] Properties) schema,
        Func<NhAssistantAgentBuilderToolHandler, NhAssistantAgentBuilderCall, CancellationToken, Task<TaskResult<System.Text.Json.Nodes.JsonNode>>> handle)
    {
        return new NhAssistantAgentBuilderTool(id, exportName, description, NhAiToolEffect.Mutation, NhAiApprovalRequirement.Required, schema, handle);
    }

    /// <summary>
    /// JSON schemas of the tools' inputs, built from their properties, which the input check names too.
    /// </summary>
    private static class Schema
    {
        public static (JsonObject Schema, string[] Properties) Object(IReadOnlyList<(string Name, JsonObject Schema, bool Required)> properties)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(properties.Select(property => new KeyValuePair<string, JsonNode?>(property.Name, property.Schema))),
                ["required"] = new JsonArray(properties.Where(property => property.Required).Select(property => (JsonNode?)JsonValue.Create(property.Name)).ToArray()),
                ["additionalProperties"] = false
            };
            return (schema, properties.Select(property => property.Name).ToArray());
        }

        public static JsonObject Id(string description)
        {
            return new JsonObject { ["type"] = "string", ["maxLength"] = 128, ["description"] = description };
        }

        public static JsonObject Text(string description, int maxLength)
        {
            return new JsonObject { ["type"] = "string", ["maxLength"] = maxLength, ["description"] = description };
        }

        public static JsonObject Reason()
        {
            return Text("Why, in one sentence; kept with the change.", 500);
        }

        public static JsonObject Integer(string description)
        {
            return new JsonObject { ["type"] = "integer", ["description"] = description };
        }

        public static JsonObject Flag(string description)
        {
            return new JsonObject { ["type"] = "boolean", ["description"] = description };
        }

        public static JsonObject Texts(string description)
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 },
                ["maxItems"] = 64,
                ["description"] = description
            };
        }

        public static JsonObject Choice(string description, IReadOnlyList<string> values)
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["description"] = description
            };
        }

        public static JsonObject Subjects()
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = NhAssistantRuleScriptLimits.MaxDryRunSubjects,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = Text("The object type, such as one of the catalog's; nh-system for the whole application.", 100),
                        ["id"] = Text("The object's id, exactly as the application shows it; global for the whole application.", 256)
                    },
                    ["required"] = new JsonArray("type", "id"),
                    ["additionalProperties"] = false
                },
                ["description"] = "The subjects to run the version on."
            };
        }
    }
}

/// <summary>
/// One tool of the agent builder: its id, the function name the model calls, what it does, its effect and
/// approval, its input schema with the properties it takes, and how the handler runs it.
/// </summary>
internal sealed record NhAssistantAgentBuilderTool(
    string Id,
    string ExportName,
    string Description,
    NhAiToolEffect Effect,
    NhAiApprovalRequirement Approval,
    (JsonObject Schema, string[] Properties) Input,
    Func<NhAssistantAgentBuilderToolHandler, NhAssistantAgentBuilderCall, CancellationToken, Task<TaskResult<System.Text.Json.Nodes.JsonNode>>> Handle);
