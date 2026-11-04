using System.Text.Json.Nodes;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// The tools of the agent builder (RB-8, FB94, FB95): what the administrator can do for an agent in the
/// administration's screens, except secrets. Reads need no approval. A dry run changes nothing and needs
/// none either. Every save, every step live, every change of whose rights an agent uses and every removal
/// is a governed write that waits for the administrator's approval in the chat, and the approval card
/// says what changes. No tool takes or returns MCP credentials, keys or other secrets, and none goes
/// beyond what the code allows.
/// </summary>
internal static class NhAssistantAgentBuilderTools
{
    public const string ReadCatalog = NhAssistantAgentBuilderIds.ToolIdPrefix + "read-catalog";
    public const string ListAgents = NhAssistantAgentBuilderIds.ToolIdPrefix + "list-agents";
    public const string ReadAgent = NhAssistantAgentBuilderIds.ToolIdPrefix + "read-agent";
    public const string ReadRuleScript = NhAssistantAgentBuilderIds.ToolIdPrefix + "read-rule-script";
    public const string SearchAccounts = NhAssistantAgentBuilderIds.ToolIdPrefix + "search-accounts";
    public const string ReadGateway = NhAssistantAgentBuilderIds.ToolIdPrefix + "read-gateway";
    public const string DryRunRuleScript = NhAssistantAgentBuilderIds.ToolIdPrefix + "dry-run-rule-script";
    public const string CreateAgent = NhAssistantAgentBuilderIds.ToolIdPrefix + "create-agent";
    public const string UpdateAgent = NhAssistantAgentBuilderIds.ToolIdPrefix + "update-agent";
    public const string SaveRunSettings = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-run-settings";
    public const string SaveSkill = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-skill";
    public const string SaveTrigger = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-trigger";
    public const string SaveRuleScript = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-rule-script";
    public const string SaveConsultation = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-consultation";
    public const string SaveHandOff = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-hand-off";
    public const string SaveMetadata = NhAssistantAgentBuilderIds.ToolIdPrefix + "save-metadata";
    public const string SetLive = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-live";
    public const string SetShadow = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-shadow";
    public const string SetState = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-state";
    public const string SetTechnicalAccount = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-technical-account";
    public const string SetConsultationRoutes = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-consultation-routes";
    public const string SetMcpServer = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-mcp-server";
    public const string SetMcpHeadlessUse = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-mcp-headless-use";
    public const string SetSwitch = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-switch";
    public const string SetGroups = NhAssistantAgentBuilderIds.ToolIdPrefix + "set-groups";
    public const string PromoteShadowVariant = NhAssistantAgentBuilderIds.ToolIdPrefix + "promote-shadow-variant";
    public const string Remove = NhAssistantAgentBuilderIds.ToolIdPrefix + "remove";

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

    /// <summary>Whose state <see cref="SetState"/> changes.</summary>
    public static class StateParts
    {
        public const string Agent = "agent";
        public const string Skill = "skill";
        public const string Consultation = "consultation";
    }

    /// <summary>Which per-agent switch <see cref="SetSwitch"/> changes.</summary>
    public static class SwitchParts
    {
        public const string ToolWithoutApproval = "tool-without-approval";
        public const string AlertModel = "alert-model";
    }

    /// <summary>What <see cref="Remove"/> removes or discards.</summary>
    public static class RemoveParts
    {
        public const string Agent = "agent";
        public const string Override = "override";
        public const string RunSettings = "run-settings";
        public const string Skill = "skill";
        public const string Trigger = "trigger";
        public const string Consultation = "consultation";
        public const string HandOff = "hand-off";
        public const string Metadata = "metadata";
        public const string SkillCandidate = "skill-candidate";
        public const string TriggerCandidate = "trigger-candidate";
        public const string RuleScriptVersion = "rule-script-version";

        public static IReadOnlyList<string> All { get; } =
            [Agent, Override, RunSettings, Skill, Trigger, Consultation, HandOff, Metadata, SkillCandidate, TriggerCandidate, RuleScriptVersion];
    }

    private static readonly string[] Autonomies = ["observe", "explain", "propose", "simulate", "execute"];

    private static readonly string[] Classifications = ["public", "internal", "confidential", "restricted"];

    private static readonly string[] Accountabilities = ["agent", "subject-responsible", "service-account"];

    private static readonly string[] RunStates = ["active", "paused", "stopped", "off"];

    private static readonly string[] SwitchModes = ["off", "shadow", "live"];

    private static readonly string[] RouteSwitches = [NhAssistantRouteSwitches.On, NhAssistantRouteSwitches.Off, NhAssistantRouteSwitches.Code];

    /// <summary>
    /// Every tool, in the order the model reads them.
    /// </summary>
    public static IReadOnlyList<NhAssistantAgentBuilderTool> All { get; } =
    [
        Read(
            ReadCatalog,
            "builder_read_catalog",
            "Lists the building blocks the application registered in code that an agent may use: tools with their effect and description, trigger events with their filters, object types, subject sets, rule handlers, approval policies, model profiles, agent policies, alert kinds, agent groups, which agents may have rule scripts with the writes the code allows, whether the application has an API gateway, and the MCP servers an agent may be assigned (without their connections or credentials). Changes nothing.",
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
            "Reads one agent in full: its definition with instructions, tools, required policy, MCP servers, what it works on and version; its state, technical account and consultation routes; its run settings, skills, triggers and consultations in code and as data with their versions and candidates; its hand-offs, metadata, shadow variants, switches for tools without approval and for alerts by the model, and its rule scripts; and the blockers: settings that stop it from working as it should, each with the setting and the tool that changes it. Changes nothing.",
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
        Read(
            SearchAccounts,
            "builder_search_accounts",
            "Finds the application's accounts, people and technical accounts, by name or e-mail address (query, 2 to 100 characters), or names the accounts of given actor ids (ids), as the administration's account search does, so an agent can answer to a technical account. Whether an account may answer for an agent is checked when the setting is saved. Changes nothing.",
            Schema.Object([
                ("query", Schema.Text("Words of the account's name or e-mail address.", NhAssistantAgentBuilderToolHandler.MaxAccountQueryLength), false),
                ("ids", Schema.Texts("Actor ids to name, at most 50."), false),
                ("limit", Schema.Integer("The most accounts a search returns, 1 to 50; 20 by default."), false)
            ]),
            (handler, call, token) => handler.SearchAccountsAsync(call, token)),
        Read(
            ReadGateway,
            "builder_read_gateway",
            "Searches and describes the application's data through its API gateway with the administrator's own rights: with query it lists the resources whose name, title, summary or fields match, with their operations (query, get); with resource it describes one: its filter fields with their operators, search and order fields, whether a query can only count (countOnly), the id parameter of get and its result fields. Use it to learn what an agent can read through the gateway before you write its instructions or a rule script. Without a gateway it says there is none. Changes nothing.",
            Schema.Object([
                ("query", Schema.Text("Words to look for among the resources.", 200), false),
                ("resource", Schema.Text("The resource to describe, as the search names it.", 128), false),
                ("limit", Schema.Integer("The most resources a search returns, 1 to 20; 10 by default."), false)
            ]),
            (handler, call, token) => handler.ReadGatewayAsync(call, token)),
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
            "Creates a new agent in the administration, after the administrator approves it in the chat. It starts without run settings, skills, triggers and MCP servers, which later steps add. Tool selectors are tool ids or a prefix ending in .*, chosen from the catalog. With shadowOf it is a shadow variant of that live agent: it receives the live agent's triggers, always works in shadow and is never offered in the chat.",
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
                ("perDivision", Schema.Flag("The agent works per division, when the catalog lists divisions."), false),
                ("runPolicy", Schema.Text("An agent policy from the catalog: who may run it for the whole application.", 256), false),
                ("viewPolicy", Schema.Text("An agent policy from the catalog: who sees its results for the whole application.", 256), false),
                ("offeredInChat", Schema.Flag("People can choose the agent in the chat; true by default."), false),
                ("startsOff", Schema.Flag("The agent starts off: its triggers are ignored until it is switched on."), false),
                ("startsInShadow", Schema.Flag("Its runs work in shadow, without effects, until it is put live."), false),
                ("profile", Schema.Text("A model profile from the catalog; the application's chat profile by default.", 128), false),
                ("requiredPolicy", Schema.Text("The policy a person needs to see and use the agent; none by default.", 256), false),
                ("dataClassification", Schema.Choice("The classification of the data its model calls carry; internal by default.", Classifications), false),
                ("compositions", Schema.Flag("The agent uses compositions, within what the code allows."), false),
                ("scripts", Schema.Flag("The agent uses the script tool, within what the code allows."), false),
                ("metadata", Schema.Map("Values of the application's agent metadata entries, by entry name."), false),
                ("shadowOf", Schema.Id("The live agent the new agent is a shadow variant of."), false)
            ]),
            (handler, call, token) => handler.CreateAgentAsync(call, token)),
        Write(
            UpdateAgent,
            "builder_update_agent",
            "Changes an agent's definition, after the administrator approves it in the chat: its name, description, instructions, tool selectors, autonomy, required policy, MCP servers, what it works on, model profile, classification, compositions and scripts, or whether it is offered in the chat. Fields left out keep their value; requiredPolicy null removes the policy. mcpServerIds replaces the agent's MCP servers in full. expectedVersion is the version builder_read_agent showed. An agent in code gets an override of the fields it can override.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("expectedVersion", Schema.Integer("The agent's current version."), true),
                ("displayName", Schema.Text("The new name.", 256), false),
                ("description", Schema.Text("The new description.", 512), false),
                ("instructions", Schema.Text("The new instructions, in full.", 20_000), false),
                ("toolSelectors", Schema.Texts("The new tool selectors, in full."), false),
                ("autonomy", Schema.Choice("The new autonomy.", Autonomies), false),
                ("requiredPolicy", Schema.NullableText("The policy a person needs to see and use the agent; null for none.", 256), false),
                ("mcpServerIds", Schema.Texts("The MCP servers the agent may use, in full; an empty list removes them all."), false),
                ("offeredInChat", Schema.Flag("For an agent created in the administration: whether people can choose it in the chat."), false),
                ("worksOnSubjectTypes", Schema.Texts("For an agent created in the administration: the object types it works on."), false),
                ("worksOnAllSubjects", Schema.Flag("For an agent created in the administration: it works on every object type."), false),
                ("worksOnApplication", Schema.Flag("For an agent created in the administration: it works on the whole application."), false),
                ("perDivision", Schema.Flag("For an agent created in the administration: it works per division."), false),
                ("runPolicy", Schema.NullableText("For an agent created in the administration: who may run it for the whole application.", 256), false),
                ("viewPolicy", Schema.NullableText("For an agent created in the administration: who sees its results for the whole application.", 256), false),
                ("profile", Schema.Text("For an agent created in the administration: a model profile from the catalog.", 128), false),
                ("dataClassification", Schema.Choice("For an agent created in the administration: the classification of its data.", Classifications), false),
                ("compositions", Schema.Flag("For an agent created in the administration: it uses compositions."), false),
                ("scripts", Schema.Flag("For an agent created in the administration: it uses the script tool."), false)
            ]),
            (handler, call, token) => handler.UpdateAgentAsync(call, token)),
        Write(
            SaveRunSettings,
            "builder_save_run_settings",
            "Adds or changes the run settings of an agent created in the administration, after the administrator approves it in the chat: whose rights its runs use (accountability, the technical account of service-account found with builder_search_accounts, delegateFromInitiator, allowGatewayReads and elevatedReads), approval policy, retries, daily budgets, concurrency, runs per subject per day, the token ceiling of a run, triage, the rule handler and the file settings. Settings left out keep their value; new settings answer to the agent itself unless they say otherwise. Gateway reads need an accountability other than agent.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("expectedVersion", Schema.Integer("The settings' current version; leave it out for new settings."), false),
                ("accountability", Schema.Choice("Whose rights the agent's runs use: the agent itself, the subject's responsible person or a technical account.", Accountabilities), false),
                ("serviceAccountActorId", Schema.NullableText("For service-account: the technical account's actor id, from builder_search_accounts.", 256), false),
                ("delegateFromInitiator", Schema.Flag("Runs also use what the person who started them may use."), false),
                ("allowGatewayReads", Schema.Flag("Runs read through the application's API gateway with the accountable person's or account's rights."), false),
                ("elevatedReads", Schema.Choice("restricted shields reads with the accountable rights from thread readers without the tool's policies; deny refuses them.", ["restricted", "deny"]), false),
                ("approvalPolicy", Schema.NullableText("An approval policy from the catalog.", 128), false),
                ("retryCount", Schema.Integer("Automatic retries after a transient failure, 0 to 10."), false),
                ("dailyToolCalls", Schema.Integer("With dailyTokens, the daily budget of tool calls per scope."), false),
                ("dailyTokens", Schema.Integer("With dailyToolCalls, the daily budget of tokens per scope."), false),
                ("maxConcurrentRuns", Schema.Integer("1 to 100."), false),
                ("maxRunsPerSubjectPerDay", Schema.Integer("1 to 1,000."), false),
                ("maxTokensPerRun", Schema.Integer("The token ceiling of one run."), false),
                ("triageProfile", Schema.NullableText("A model profile that chooses the skill of a run no trigger routed.", 128), false),
                ("ruleHandler", Schema.NullableText("A rule handler from the catalog.", 128), false),
                ("acceptsFiles", Schema.Flag("People's messages and the agent's tools may give it files, within the application's limits."), false),
                ("maxFileBytes", Schema.Integer("With acceptsFiles: the largest file in bytes, at most the application's."), false),
                ("maxFilePages", Schema.Integer("With acceptsFiles: the most pages of one file."), false),
                ("maxFilesPerTurn", Schema.Integer("With acceptsFiles: the most files of one turn."), false),
                ("maxFileTokensPerTurn", Schema.Integer("With acceptsFiles: the most estimated file tokens of one turn."), false),
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
                ("dataClassification", Schema.Choice("The classification of the data its model calls carry.", Classifications), false),
                ("acceptsFiles", Schema.Flag("A run in the skill reads files, when its agent accepts them too."), false),
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
            SaveConsultation,
            "builder_save_consultation",
            "Adds or changes the consultation defined as data that lets other assistants consult the agent about subjects of one type, within the ceiling in code, after the administrator approves it in the chat: who may ask (minimumRole and askerPolicy), how its proposals are decided (routes), the proposal role and policy, its limits, the request tool, MCP exposure and the classification of its answers. Send the whole consultation; expectedVersion is its current version, left out for a new one.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("subjectType", Schema.Id("The object type the consultation is about."), true),
                ("expectedVersion", Schema.Integer("The consultation's current version; leave it out for a new one."), false),
                ("description", Schema.Text("What asking agents may consult it about.", NhAssistantAgentConfigurationLimits.MaxConsultationDescriptionLength), true),
                ("instructions", Schema.Text("Instructions for the consulted agent's turn.", NhAssistantAgentConfigurationLimits.MaxConsultationInstructionsLength), false),
                ("minimumRole", Schema.Choice("The role on the subject the asking person needs.", ["reader", "participant", "approver"]), false),
                ("askerPolicy", Schema.Text("An agent policy from the catalog the asking person must pass.", 256), false),
                ("routes", Schema.Texts("How its proposals are decided: asker-decides and at most one of subject-asker-rights and subject-agent-rights; none keeps it read-only."), false),
                ("proposalRole", Schema.Choice("The role the asking person needs before the agent may propose.", ["reader", "participant", "approver"]), false),
                ("proposalPolicy", Schema.Text("An approval policy from the catalog.", 128), false),
                ("toolSelectors", Schema.Texts("The tools the consulted agent is offered, within its own; all of them by default."), false),
                ("maxToolCalls", Schema.Integer("0 to 32."), false),
                ("timeoutSeconds", Schema.Integer("1 to 600."), false),
                ("maxAnswerChars", Schema.Integer("200 to 32,000."), false),
                ("maxHistoryMessages", Schema.Integer("0 to 200."), false),
                ("allowRequests", Schema.Flag("Also offers the request tool, when the ceiling allows requests."), false),
                ("requestsOnly", Schema.Flag("It takes requests only; needs allowRequests."), false),
                ("exposeToMcp", Schema.Flag("MCP clients may see its tools, when the ceiling allows it."), false),
                ("dataClassification", Schema.Choice("The classification of its answers; the agent's by default.", Classifications), false),
                ("startsOff", Schema.Flag("For a new consultation: it starts off until it is switched on."), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SaveConsultationAsync(call, token)),
        Write(
            SaveHandOff,
            "builder_save_hand_off",
            "Adds or changes a hand-off defined as data, after the administrator approves it in the chat: the agents in sources may hand work on subjects of the type to this agent, within the ceiling in code, with a description for their models, a follow-up to the source and a daily limit per source and subject. expectedVersion is its current version, left out for a new one.",
            Schema.Object([
                ("agentId", Schema.Id("The agent that accepts the work."), true),
                ("subjectType", Schema.Id("The object type of the work."), true),
                ("expectedVersion", Schema.Integer("The hand-off's current version; leave it out for a new one."), false),
                ("sources", Schema.Texts("The agents that may hand off to it."), true),
                ("description", Schema.Text("When the sources hand off, for their models.", 512), false),
                ("notifySource", Schema.Flag("The source hears back when the work is done."), false),
                ("maxPerSubjectPerDay", Schema.Integer("The daily limit per source and subject, at most the ceiling's."), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SaveHandOffAsync(call, token)),
        Write(
            SaveMetadata,
            "builder_save_metadata",
            "Saves one entry of the agent's metadata that the application registered, after the administrator approves it in the chat. value has the shape of the entry's type, as builder_read_agent shows the entry. expectedVersion is the entry's version as builder_read_agent shows it: the agent's version for an agent created in the administration, left out for a new override of an agent in code.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("name", Schema.Id("The entry's name."), true),
                ("value", Schema.Any("The value, in the shape of the entry's type."), true),
                ("expectedVersion", Schema.Integer("The entry's version."), false)
            ]),
            (handler, call, token) => handler.SaveMetadataAsync(call, token)),
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
            (handler, call, token) => handler.SetShadowAsync(call, token)),
        Write(
            SetState,
            "builder_set_state",
            "Changes a run state, after the administrator approves it in the chat: the agent or one of its skills becomes active, paused (nothing new starts, events wait), stopped (active runs are cancelled) or off (triggers are ignored); a consultation defined as data is switched on or off.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("part", Schema.Choice("Whose state changes.", [StateParts.Agent, StateParts.Skill, StateParts.Consultation]), true),
                ("state", Schema.Choice("For the agent or a skill: active, paused, stopped or off; for a consultation: on or off.", [.. RunStates, NhAssistantConsultationStates.On]), true),
                ("skillId", Schema.Id("For a skill: its id."), false),
                ("subjectType", Schema.Id("For a consultation: its object type."), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SetStateAsync(call, token)),
        Write(
            SetTechnicalAccount,
            "builder_set_technical_account",
            "For an agent in code that answers to a technical account in code: makes actorId, an account from builder_search_accounts, the technical account whose rights its new runs use, or with actorId null the account in code again, after the administrator approves it in the chat. An agent created in the administration sets its technical account with builder_save_run_settings.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("actorId", Schema.NullableText("The technical account's actor id; null for the account in code.", 256), true),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SetTechnicalAccountAsync(call, token)),
        Write(
            SetConsultationRoutes,
            "builder_set_consultation_routes",
            "Switches the routes of an agent's consultations in code within what the code allows, after the administrator approves it in the chat: askerDecides lets the asking person decide a proposal in their own chat, with their own rights; subjectAgentRights lets the subject's approvers decide, after which it executes with the agent's own rights; subjectAskerRights lets them decide, after which it executes with the asker's rights. Each on, off or code; a route left out keeps its switch.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("askerDecides", Schema.Choice("on, off or code.", RouteSwitches), false),
                ("subjectAgentRights", Schema.Choice("on, off or code.", RouteSwitches), false),
                ("subjectAskerRights", Schema.Choice("on, off or code.", RouteSwitches), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SetConsultationRoutesAsync(call, token)),
        Write(
            SetMcpServer,
            "builder_set_mcp_server",
            "Assigns an existing MCP server from the catalog to the agent, so its enabled tools are offered in the agent's turns, or with assigned false removes that assignment, after the administrator approves it in the chat. Only the assignment changes; connecting a server and its credentials stay in the administration's screens.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("serverId", Schema.Id("The MCP server's id, from the catalog."), true),
                ("assigned", Schema.Flag("true to assign the server, false to remove the assignment."), true)
            ]),
            (handler, call, token) => handler.SetMcpServerAsync(call, token)),
        Write(
            SetMcpHeadlessUse,
            "builder_set_mcp_headless_use",
            "Changes whether the tools of an existing MCP server are offered in runs without a person, after the administrator approves it in the chat: never, organization (with the organization's connection, so agent runs may use them with their own grants) or personal (with the connection of the person a run acts for, after their consent). It applies to every agent the server is assigned to. Its connection and credentials stay as they are.",
            Schema.Object([
                ("serverId", Schema.Id("The MCP server's id, from the catalog."), true),
                ("headlessUse", Schema.Choice("never, organization or personal.", [NhAssistantMcpHeadlessUses.Never, NhAssistantMcpHeadlessUses.Organization, NhAssistantMcpHeadlessUses.Personal]), true)
            ]),
            (handler, call, token) => handler.SetMcpHeadlessUseAsync(call, token)),
        Write(
            SetSwitch,
            "builder_set_switch",
            "Changes a switch of the agent, after the administrator approves it in the chat: whether its own runs call a tool whose code allows it without a person's approval (tool-without-approval with toolId), or whether its model raises alerts of a kind whose code allows it (alert-model with kindId). Each off, shadow (recorded what would happen) or live, and live only from shadow. expectedVersion is the switch's version as builder_read_agent shows it, 0 before the first switch.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("part", Schema.Choice("Which switch.", [SwitchParts.ToolWithoutApproval, SwitchParts.AlertModel]), true),
                ("toolId", Schema.Text("For tool-without-approval: the tool's id.", 256), false),
                ("kindId", Schema.Id("For alert-model: the alert kind's id."), false),
                ("mode", Schema.Choice("off, shadow or live.", SwitchModes), true),
                ("expectedVersion", Schema.Integer("The switch's current version."), true),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.SetSwitchAsync(call, token)),
        Write(
            SetGroups,
            "builder_set_groups",
            "Puts the agent in exactly the given agent groups from the catalog, which boards show, and out of the others, after the administrator approves it in the chat.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("groups", Schema.Texts("The groups, in full; an empty list takes it out of all."), true)
            ]),
            (handler, call, token) => handler.SetGroupsAsync(call, token)),
        Write(
            PromoteShadowVariant,
            "builder_promote_shadow_variant",
            "Copies the definition of a shadow variant created in the administration (agentId) onto its live agent as the live agent's next version, after the administrator approves it in the chat, and then deletes the variant. expectedVersion is the live agent's version. With moveSkills the variant's data skills become versions of the live agent's skills; with asPackageVersion a variant linked to one agent package becomes that package's next version.",
            Schema.Object([
                ("agentId", Schema.Id("The shadow variant's id."), true),
                ("expectedVersion", Schema.Integer("The live agent's current version."), true),
                ("expectedVariantVersion", Schema.Integer("The variant's version that was evaluated."), false),
                ("moveSkills", Schema.Flag("The variant's data skills move to the live agent."), false),
                ("asPackageVersion", Schema.Flag("The variant becomes its package's next version."), false),
                ("uncarriedParts", Schema.Choice("With asPackageVersion: unlink (default) or relink the live agent's linked parts the variant does not carry.", ["unlink", "relink"]), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.PromoteShadowVariantAsync(call, token)),
        Write(
            Remove,
            "builder_remove",
            "Removes or discards something, after the administrator approves it in the chat; the card says what goes: an agent created in the administration (agent), the administrator's override of an agent in code with its MCP assignments (override), the run settings of an agent that then only chats, a data skill, a data trigger, a data consultation (subjectType), a hand-off (subjectType), a metadata entry (name), a skill's or trigger's candidate (version) or a rule script's candidate (rule-script-version, with version and expectedRevision). Versions stay as history where the administration keeps them.",
            Schema.Object([
                ("agentId", Schema.Id("The agent's id."), true),
                ("part", Schema.Choice("What goes.", RemoveParts.All), true),
                ("skillId", Schema.Id("For a skill, a skill's candidate or a skill's rule script."), false),
                ("triggerId", Schema.Id("For a trigger or a trigger's candidate."), false),
                ("subjectType", Schema.Id("For a consultation or a hand-off."), false),
                ("name", Schema.Id("For a metadata entry."), false),
                ("version", Schema.Integer("For a candidate or a rule script version."), false),
                ("expectedVersion", Schema.Integer("The version of what goes, as builder_read_agent shows it."), false),
                ("expectedRevision", Schema.Integer("For a rule script version: the rule script's revision."), false),
                ("reason", Schema.Reason(), false)
            ]),
            (handler, call, token) => handler.RemoveAsync(call, token))
    ];

    /// <summary>
    /// Whether <paramref name="toolId"/> is one of the tools that change something, each of which waits
    /// for the administrator's approval.
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
        Func<NhAssistantAgentBuilderToolHandler, NhAssistantAgentBuilderCall, CancellationToken, Task<TaskResult<JsonNode>>> handle)
    {
        return new NhAssistantAgentBuilderTool(id, exportName, description, NhAiToolEffect.ReadOnly, NhAiApprovalRequirement.NotRequired, schema, handle);
    }

    private static NhAssistantAgentBuilderTool Write(
        string id,
        string exportName,
        string description,
        (JsonObject Schema, string[] Properties) schema,
        Func<NhAssistantAgentBuilderToolHandler, NhAssistantAgentBuilderCall, CancellationToken, Task<TaskResult<JsonNode>>> handle)
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

        /// <summary>A text that <c>null</c> clears.</summary>
        public static JsonObject NullableText(string description, int maxLength)
        {
            return new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["maxLength"] = maxLength,
                ["description"] = description
            };
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

        /// <summary>A value of any JSON type, checked by the service it reaches.</summary>
        public static JsonObject Any(string description)
        {
            return new JsonObject { ["description"] = description };
        }

        /// <summary>An object of values of any JSON type by name.</summary>
        public static JsonObject Map(string description)
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = true,
                ["maxProperties"] = 64,
                ["description"] = description
            };
        }

        public static JsonObject Texts(string description)
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256 },
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
    Func<NhAssistantAgentBuilderToolHandler, NhAssistantAgentBuilderCall, CancellationToken, Task<TaskResult<JsonNode>>> Handle);
