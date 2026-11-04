using System.Globalization;
using System.Text.Json;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// Names the agent builder's tools and says on the approval card of every write what changes (RB-8,
/// FB94), in English and Dutch: the agent, the part and how the change takes effect, with what it sets. A
/// change of whose rights an agent uses and every removal say so plainly in the card's notice. The card is
/// read from the call's own input, which is what runs once approved; the input in full stays in its
/// technical details.
/// </summary>
internal sealed class NhAssistantAgentBuilderPresenter(NhAssistantRegistrationState registration) : INhAssistantToolPresenter
{
    private const int MaxValueLength = NhAssistantPresentationLimits.MaxFieldValueLength;

    /// <summary>The run settings that decide whose rights an agent's runs use.</summary>
    private static readonly string[] RightsSettings = ["accountability", "serviceAccountActorId", "delegateFromInitiator", "allowGatewayReads", "elevatedReads"];

    public ValueTask<string?> GetDisplayNameAsync(NhAssistantToolDisplayNameContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!NhAssistantAgentBuilderIds.IsAgentBuilderTool(context.Descriptor.Id))
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult<string?>(NameOf(context.Descriptor.Id, IsDutch(context.Culture)));
    }

    public ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
        NhAssistantApprovalPresentationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var toolId = context.Descriptor.Id;
        if (!NhAssistantAgentBuilderIds.IsAgentBuilderTool(toolId) || context.Arguments is not JsonElement { ValueKind: JsonValueKind.Object } input)
        {
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(null);
        }

        var dutch = IsDutch(context.Culture);
        var texts = new Texts(dutch);
        var agentId = Text(input, "agentId") ?? "?";
        var card = new Card(texts, agentId);
        if (!Describe(toolId, input, card))
        {
            return ValueTask.FromResult<NhAssistantApprovalPresentation?>(null);
        }

        card.Add(texts.Reason, Text(input, "reason"));
        return ValueTask.FromResult<NhAssistantApprovalPresentation?>(new NhAssistantApprovalPresentation(
            NameOf(toolId, dutch),
            Bound(card.Summary, NhAssistantPresentationLimits.MaxSummaryLength),
            card.Fields.Take(NhAssistantPresentationLimits.MaxFields).ToArray(),
            Bound(card.Notice ?? texts.ApprovalNotice, NhAssistantPresentationLimits.MaxNoticeLength)));
    }

    /// <summary>
    /// Fills the card of one write; <see langword="false"/> for a tool that has no card, such as a read.
    /// </summary>
    private bool Describe(string toolId, JsonElement input, Card card)
    {
        var texts = card.Texts;
        var dutch = texts.Dutch;
        var agentId = card.AgentId;
        switch (toolId)
        {
            case NhAssistantAgentBuilderTools.CreateAgent:
                card.Summary = Text(input, "shadowOf") is { } shadowOf
                    ? (dutch
                        ? $"Maakt '{Text(input, "displayName")}' aan als schaduwvariant van agent {shadowOf}: die krijgt de triggers van de live agent en werkt altijd in schaduw."
                        : $"Creates '{Text(input, "displayName")}' as a shadow variant of agent {shadowOf}: it receives the live agent's triggers and always works in shadow.")
                    : (dutch
                        ? $"Maakt de nieuwe agent '{Text(input, "displayName")}' aan in het beheer, zonder run-instellingen, vaardigheden of triggers."
                        : $"Creates the new agent '{Text(input, "displayName")}' in the administration, without run settings, skills or triggers.");
                card.Add(texts.Name, Text(input, "displayName"));
                card.Add(texts.Tools, Join(input, "toolSelectors"));
                card.Add(texts.Autonomy, Text(input, "autonomy") ?? "propose");
                card.Add(texts.WorksOn, WorksOn(input, texts));
                card.Add(texts.RequiredPolicy, Text(input, "requiredPolicy"));
                card.Add(texts.Instructions, Length(input, "instructions", texts));
                card.Add(texts.Starts, Flag(input, "startsInShadow") ? texts.InShadow : Flag(input, "startsOff") ? texts.Off : texts.Live);
                return true;
            case NhAssistantAgentBuilderTools.UpdateAgent:
                card.Summary = dutch
                    ? $"Wijzigt de definitie van agent {agentId} als zijn volgende versie; velden die niet genoemd worden blijven zoals ze zijn."
                    : $"Changes the definition of agent {agentId} as its next version; fields it does not name stay as they are.";
                card.Add(texts.Version, Number(input, "expectedVersion"));
                card.Add(texts.Name, Text(input, "displayName"));
                card.Add(texts.Instructions, input.TryGetProperty("instructions", out _) ? Length(input, "instructions", texts) : null);
                card.Add(texts.Tools, Join(input, "toolSelectors"));
                card.Add(texts.Autonomy, Text(input, "autonomy"));
                card.Add(texts.WorksOn, WorksOn(input, texts));
                var rights = new List<string>();
                if (input.TryGetProperty("requiredPolicy", out _))
                {
                    rights.Add(texts.RequiredPolicy + ": " + (Text(input, "requiredPolicy") ?? texts.None));
                }
                if (input.TryGetProperty("mcpServerIds", out _))
                {
                    rights.Add(texts.McpServers + ": " + (Join(input, "mcpServerIds") ?? texts.None));
                }
                card.Rights(rights);
                return true;
            case NhAssistantAgentBuilderTools.SaveRunSettings:
                card.Summary = dutch
                    ? $"Slaat de run-instellingen van agent {agentId} op; instellingen die niet genoemd worden blijven zoals ze zijn."
                    : $"Saves the run settings of agent {agentId}; settings it does not name stay as they are.";
                card.Add(texts.Changes, Names(input, ["agentId", "expectedVersion", "reason", "alertKinds", .. RightsSettings]));
                card.Add(texts.Version, Number(input, "expectedVersion") ?? texts.New);
                if (input.TryGetProperty("alertKinds", out _))
                {
                    card.Add(texts.AlertKinds, Join(input, "alertKinds") ?? texts.None);
                }
                card.Rights(RunSettingsRights(input, texts));
                return true;
            case NhAssistantAgentBuilderTools.SaveSkill:
                card.Summary = dutch
                    ? $"Slaat vaardigheid {Text(input, "skillId")} van agent {agentId} op: {Apply(input, texts)}."
                    : $"Saves skill {Text(input, "skillId")} of agent {agentId}: {Apply(input, texts)}.";
                card.Add(texts.Skill, Text(input, "skillId"));
                card.Add(texts.Version, Number(input, "expectedVersion") ?? texts.New);
                card.Add(texts.Tools, Join(input, "tools"));
                card.Add(texts.RuleHandler, Text(input, "ruleHandler"));
                card.Add(texts.Instructions, input.TryGetProperty("instructions", out _) ? Length(input, "instructions", texts) : null);
                return true;
            case NhAssistantAgentBuilderTools.SaveTrigger:
                card.Summary = dutch
                    ? $"Slaat trigger {Text(input, "triggerId")} van agent {agentId} op: {Apply(input, texts)}."
                    : $"Saves trigger {Text(input, "triggerId")} of agent {agentId}: {Apply(input, texts)}.";
                card.Add(texts.Trigger, Text(input, "triggerId"));
                card.Add(texts.Version, Number(input, "expectedVersion") ?? texts.New);
                card.Add(texts.Source, Text(input, "source") == "schedule"
                    ? $"{Text(input, "cron")} {Text(input, "timeZone") ?? "UTC"}".Trim()
                    : Text(input, "event"));
                card.Add(texts.SubjectSet, Text(input, "subjectSet"));
                card.Add(texts.Skill, Text(input, "skill"));
                return true;
            case NhAssistantAgentBuilderTools.SaveRuleScript:
                card.Summary = dutch
                    ? $"Slaat een nieuwe versie op van het regelscript van {Owner(input, agentId, texts)}: {RuleScriptMode(input, texts)}."
                    : $"Saves a new version of the rule script of {Owner(input, agentId, texts)}: {RuleScriptMode(input, texts)}.";
                card.Add(texts.Skill, Text(input, "skillId"));
                card.Add(texts.Writes, Join(input, "writes") ?? texts.None);
                card.Add(texts.Script, Lines(input, texts));
                card.Add(texts.Revision, Number(input, "expectedRevision"));
                if (Flag(input, "live") && registration.RuleScriptsAsData?.RequireFourEyes == true)
                {
                    card.Notice = texts.FourEyesSave;
                }
                return true;
            case NhAssistantAgentBuilderTools.SaveConsultation:
                card.Summary = dutch
                    ? $"Slaat de raadpleging over {Text(input, "subjectType")} van agent {agentId} op: andere assistenten mogen de agent daarover raadplegen."
                    : $"Saves the consultation about {Text(input, "subjectType")} of agent {agentId}: other assistants may consult the agent about it.";
                card.Add(texts.SubjectType, Text(input, "subjectType"));
                card.Add(texts.Version, Number(input, "expectedVersion") ?? texts.New);
                card.Add(texts.WhoMayAsk, string.Join(", ", new[] { Text(input, "minimumRole"), Text(input, "askerPolicy") }.OfType<string>()));
                if (input.TryGetProperty("routes", out _))
                {
                    card.Rights([texts.Routes + ": " + (Join(input, "routes") ?? texts.ReadOnly)]);
                }
                return true;
            case NhAssistantAgentBuilderTools.SaveHandOff:
                card.Summary = dutch
                    ? $"Slaat de overdracht van werk aan {Text(input, "subjectType")} naar agent {agentId} op."
                    : $"Saves the hand-off of work on {Text(input, "subjectType")} to agent {agentId}.";
                card.Add(texts.SubjectType, Text(input, "subjectType"));
                card.Add(texts.Sources, Join(input, "sources"));
                card.Add(texts.Version, Number(input, "expectedVersion") ?? texts.New);
                card.Add(texts.DailyLimit, Number(input, "maxPerSubjectPerDay"));
                return true;
            case NhAssistantAgentBuilderTools.SaveMetadata:
                card.Summary = dutch
                    ? $"Slaat het metadata-veld {Text(input, "name")} van agent {agentId} op."
                    : $"Saves the metadata entry {Text(input, "name")} of agent {agentId}.";
                card.Add(texts.Entry, Text(input, "name"));
                card.Add(texts.Value, input.TryGetProperty("value", out var value) ? value.GetRawText() : null);
                card.Add(texts.Version, Number(input, "expectedVersion"));
                return true;
            case NhAssistantAgentBuilderTools.SetLive:
                card.Summary = dutch
                    ? $"Zet versie {Number(input, "version")} van {Part(Text(input, "part"), texts)} van agent {agentId} live; de huidige live versie blijft als geschiedenis."
                    : $"Sets version {Number(input, "version")} of {Part(Text(input, "part"), texts)} of agent {agentId} live; the version that is live now stays as history.";
                card.Add(texts.Skill, Text(input, "skillId"));
                card.Add(texts.Trigger, Text(input, "triggerId"));
                card.Add(texts.Revision, Number(input, "expectedRevision"));
                if (Text(input, "part") == NhAssistantAgentBuilderTools.LiveParts.RuleScript && registration.RuleScriptsAsData?.RequireFourEyes == true)
                {
                    card.Notice = texts.FourEyesLive;
                }
                return true;
            case NhAssistantAgentBuilderTools.SetShadow:
                var shadow = Flag(input, "shadow");
                card.Summary = dutch
                    ? $"{(shadow ? "Zet" : "Haalt")} {Part(Text(input, "part"), texts)} van agent {agentId} {(shadow ? "in schaduw: het werkt zonder effecten" : "uit schaduw: het werkt live")}."
                    : $"{(shadow ? "Puts" : "Takes")} {Part(Text(input, "part"), texts)} of agent {agentId} {(shadow ? "in shadow: it works without effects" : "out of shadow: it works live")}.";
                card.Add(texts.Skill, Text(input, "skillId"));
                card.Add(texts.Trigger, Text(input, "triggerId"));
                card.Add(texts.Version, Number(input, "version"));
                return true;
            case NhAssistantAgentBuilderTools.SetState:
                card.Summary = texts.StateSummary(Part(Text(input, "part"), texts), Text(input, "skillId") ?? Text(input, "subjectType"), agentId, Text(input, "state"));
                card.Add(texts.State, Text(input, "state"));
                card.Add(texts.Skill, Text(input, "skillId"));
                card.Add(texts.SubjectType, Text(input, "subjectType"));
                return true;
            case NhAssistantAgentBuilderTools.SetTechnicalAccount:
                var account = Text(input, "actorId");
                card.Summary = dutch
                    ? $"Wijzigt het technische account van agent {agentId}: {account ?? "weer het account in de code"}."
                    : $"Changes the technical account of agent {agentId}: {account ?? "the account in code again"}.";
                card.Rights([texts.TechnicalAccount + ": " + (account ?? texts.FromCode)]);
                return true;
            case NhAssistantAgentBuilderTools.SetConsultationRoutes:
                card.Summary = dutch
                    ? $"Schakelt de routes van de raadplegingen van agent {agentId}: wie een voorstel beslist en met wiens rechten het wordt uitgevoerd."
                    : $"Switches the routes of agent {agentId}'s consultations: who decides a proposal and with whose rights it executes.";
                card.Rights(new[] { "askerDecides", "subjectAgentRights", "subjectAskerRights" }
                    .Where(route => Text(input, route) is not null)
                    .Select(route => route + ": " + Text(input, route))
                    .ToArray());
                return true;
            case NhAssistantAgentBuilderTools.SetMcpServer:
                var assigned = Flag(input, "assigned");
                var server = Text(input, "serverId");
                card.Summary = assigned
                    ? (dutch
                        ? $"Wijst MCP-server {server} toe aan agent {agentId}: de ingeschakelde tools van de server worden aangeboden in zijn beurten."
                        : $"Assigns MCP server {server} to agent {agentId}: the server's enabled tools are offered in its turns.")
                    : (dutch
                        ? $"Haalt MCP-server {server} weg bij agent {agentId}: zijn beurten krijgen de tools van de server niet meer."
                        : $"Removes MCP server {server} from agent {agentId}: its turns no longer get the server's tools.");
                card.Add(texts.McpServer, server);
                if (assigned)
                {
                    card.Rights([texts.McpServer + ": " + server]);
                }
                else
                {
                    card.Removal(texts.AssignmentOf(server));
                }
                return true;
            case NhAssistantAgentBuilderTools.SetMcpHeadlessUse:
                var mcp = Text(input, "serverId");
                card.Summary = dutch
                    ? $"Wijzigt of de tools van MCP-server {mcp} worden aangeboden in runs zonder persoon: {Text(input, "headlessUse")}."
                    : $"Changes whether the tools of MCP server {mcp} are offered in runs without a person: {Text(input, "headlessUse")}.";
                card.Add(texts.McpServer, mcp);
                card.Add(texts.HeadlessUse, Text(input, "headlessUse"));
                card.Notice = texts.HeadlessUseNotice(mcp ?? "?");
                return true;
            case NhAssistantAgentBuilderTools.SetSwitch:
                var tool = Text(input, "toolId") ?? Text(input, "kindId");
                var mode = Text(input, "mode");
                card.Summary = Text(input, "part") == NhAssistantAgentBuilderTools.SwitchParts.AlertModel
                    ? (dutch
                        ? $"Zet de schakelaar voor waarschuwingen van soort {tool} door het model van agent {agentId} op {mode}."
                        : $"Sets the switch for alerts of kind {tool} by agent {agentId}'s model to {mode}.")
                    : (dutch
                        ? $"Zet de schakelaar van agent {agentId} voor tool {tool} zonder goedkeuring op {mode}."
                        : $"Sets agent {agentId}'s switch for running tool {tool} without approval to {mode}.");
                card.Add(Text(input, "part") == NhAssistantAgentBuilderTools.SwitchParts.AlertModel ? texts.AlertKind : texts.Tool, tool);
                card.Add(texts.Mode, mode);
                card.Add(texts.Version, Number(input, "expectedVersion"));
                if (Text(input, "part") == NhAssistantAgentBuilderTools.SwitchParts.ToolWithoutApproval && mode == "live")
                {
                    card.Notice = texts.WithoutApprovalNotice(agentId, tool ?? "?");
                }
                return true;
            case NhAssistantAgentBuilderTools.SetGroups:
                card.Summary = dutch
                    ? $"Zet agent {agentId} in precies deze groepen en uit de andere."
                    : $"Puts agent {agentId} in exactly these groups and out of the others.";
                card.Add(texts.Groups, Join(input, "groups") ?? texts.None);
                return true;
            case NhAssistantAgentBuilderTools.PromoteShadowVariant:
                card.Summary = dutch
                    ? $"Neemt de definitie van schaduwvariant {agentId} over op zijn live agent als diens volgende versie{(Flag(input, "moveSkills") ? ", met zijn vaardigheden" : string.Empty)}."
                    : $"Copies the definition of shadow variant {agentId} onto its live agent as the live agent's next version{(Flag(input, "moveSkills") ? ", with its skills" : string.Empty)}.";
                card.Add(texts.Version, Number(input, "expectedVersion"));
                card.Removal(texts.VariantItself);
                return true;
            case NhAssistantAgentBuilderTools.Remove:
                var what = RemovalOf(input, texts);
                card.Summary = dutch
                    ? $"Verwijdert {what} van agent {agentId}."
                    : $"Removes {what} of agent {agentId}.";
                card.Add(texts.Version, Number(input, "version") ?? Number(input, "expectedVersion"));
                card.Removal(what);
                return true;
            default:
                return false;
        }
    }

    /// <summary>What a run settings call changes of whose rights the agent uses, in plain words.</summary>
    private static IReadOnlyList<string> RunSettingsRights(JsonElement input, Texts texts)
    {
        var rights = new List<string>();
        if (Text(input, "accountability") is { } accountability)
        {
            rights.Add(texts.Accountability + ": " + texts.AccountabilityOf(accountability));
        }
        if (input.TryGetProperty("serviceAccountActorId", out _))
        {
            rights.Add(texts.TechnicalAccount + ": " + (Text(input, "serviceAccountActorId") ?? texts.None));
        }
        if (input.TryGetProperty("delegateFromInitiator", out _))
        {
            rights.Add(texts.Delegation + ": " + texts.OnOff(Flag(input, "delegateFromInitiator")));
        }
        if (input.TryGetProperty("allowGatewayReads", out _))
        {
            rights.Add(texts.GatewayReads + ": " + texts.OnOff(Flag(input, "allowGatewayReads")));
        }
        if (Text(input, "elevatedReads") is { } elevated)
        {
            rights.Add(texts.ElevatedReads + ": " + elevated);
        }

        return rights;
    }

    private static string RemovalOf(JsonElement input, Texts texts)
    {
        var skill = Text(input, "skillId");
        var trigger = Text(input, "triggerId");
        var subjectType = Text(input, "subjectType");
        var version = Number(input, "version");
        return Text(input, "part") switch
        {
            NhAssistantAgentBuilderTools.RemoveParts.Agent => texts.TheAgentItself,
            NhAssistantAgentBuilderTools.RemoveParts.Override => texts.TheOverride,
            NhAssistantAgentBuilderTools.RemoveParts.RunSettings => texts.TheRunSettings,
            NhAssistantAgentBuilderTools.RemoveParts.Skill => texts.TheSkill(skill),
            NhAssistantAgentBuilderTools.RemoveParts.Trigger => texts.TheTrigger(trigger),
            NhAssistantAgentBuilderTools.RemoveParts.Consultation => texts.TheConsultation(subjectType),
            NhAssistantAgentBuilderTools.RemoveParts.HandOff => texts.TheHandOff(subjectType),
            NhAssistantAgentBuilderTools.RemoveParts.Metadata => texts.TheMetadata(Text(input, "name")),
            NhAssistantAgentBuilderTools.RemoveParts.SkillCandidate => texts.TheSkillCandidate(skill, version),
            NhAssistantAgentBuilderTools.RemoveParts.TriggerCandidate => texts.TheTriggerCandidate(trigger, version),
            NhAssistantAgentBuilderTools.RemoveParts.RuleScriptVersion => texts.TheRuleScriptVersion(skill, version),
            var other => other ?? "?"
        };
    }

    private static string NameOf(string toolId, bool dutch)
    {
        return toolId switch
        {
            NhAssistantAgentBuilderTools.ReadCatalog => dutch ? "Bouwstenen lezen" : "Read the building blocks",
            NhAssistantAgentBuilderTools.ListAgents => dutch ? "Agents opsommen" : "List the agents",
            NhAssistantAgentBuilderTools.ReadAgent => dutch ? "Agent lezen" : "Read an agent",
            NhAssistantAgentBuilderTools.ReadRuleScript => dutch ? "Regelscript lezen" : "Read a rule script",
            NhAssistantAgentBuilderTools.SearchAccounts => dutch ? "Accounts zoeken" : "Find accounts",
            NhAssistantAgentBuilderTools.ReadGateway => dutch ? "Gegevens van de applicatie verkennen" : "Explore the application's data",
            NhAssistantAgentBuilderTools.DryRunRuleScript => dutch ? "Regelscript proefdraaien" : "Dry-run a rule script",
            NhAssistantAgentBuilderTools.CreateAgent => dutch ? "Agent aanmaken" : "Create an agent",
            NhAssistantAgentBuilderTools.UpdateAgent => dutch ? "Agent wijzigen" : "Change an agent",
            NhAssistantAgentBuilderTools.SaveRunSettings => dutch ? "Run-instellingen opslaan" : "Save run settings",
            NhAssistantAgentBuilderTools.SaveSkill => dutch ? "Vaardigheid opslaan" : "Save a skill",
            NhAssistantAgentBuilderTools.SaveTrigger => dutch ? "Trigger opslaan" : "Save a trigger",
            NhAssistantAgentBuilderTools.SaveRuleScript => dutch ? "Regelscript opslaan" : "Save a rule script",
            NhAssistantAgentBuilderTools.SaveConsultation => dutch ? "Raadpleging opslaan" : "Save a consultation",
            NhAssistantAgentBuilderTools.SaveHandOff => dutch ? "Overdracht opslaan" : "Save a hand-off",
            NhAssistantAgentBuilderTools.SaveMetadata => dutch ? "Metadata opslaan" : "Save metadata",
            NhAssistantAgentBuilderTools.SetLive => dutch ? "Versie live zetten" : "Set a version live",
            NhAssistantAgentBuilderTools.SetShadow => dutch ? "Schaduw wijzigen" : "Change shadow",
            NhAssistantAgentBuilderTools.SetState => dutch ? "Status wijzigen" : "Change the state",
            NhAssistantAgentBuilderTools.SetTechnicalAccount => dutch ? "Technisch account wijzigen" : "Change the technical account",
            NhAssistantAgentBuilderTools.SetConsultationRoutes => dutch ? "Routes van raadplegingen schakelen" : "Switch consultation routes",
            NhAssistantAgentBuilderTools.SetMcpServer => dutch ? "MCP-server toewijzen" : "Assign an MCP server",
            NhAssistantAgentBuilderTools.SetMcpHeadlessUse => dutch ? "Gebruik van een MCP-server zonder persoon wijzigen" : "Change an MCP server's use without a person",
            NhAssistantAgentBuilderTools.SetSwitch => dutch ? "Schakelaar wijzigen" : "Change a switch",
            NhAssistantAgentBuilderTools.SetGroups => dutch ? "Groepen wijzigen" : "Change the groups",
            NhAssistantAgentBuilderTools.PromoteShadowVariant => dutch ? "Schaduwvariant overnemen" : "Promote a shadow variant",
            NhAssistantAgentBuilderTools.Remove => dutch ? "Verwijderen" : "Remove",
            _ => toolId
        };
    }

    private static bool IsDutch(CultureInfo culture)
    {
        return string.Equals(culture.TwoLetterISOLanguageName, "nl", StringComparison.OrdinalIgnoreCase);
    }

    private static string Bound(string value, int maximum)
    {
        var printable = new string(value.Where(character => !char.IsControl(character)).ToArray());
        return printable.Length <= maximum ? printable : printable[..(maximum - 1)] + "…";
    }

    private static string? Text(JsonElement input, string name)
    {
        return input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
    }

    private static string? Number(JsonElement input, string name)
    {
        return input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }

    private static bool Flag(JsonElement input, string name)
    {
        return input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static string? Join(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        return items.Length == 0 ? null : string.Join(", ", items);
    }

    /// <summary>The settings a call names, by name.</summary>
    private static string? Names(JsonElement input, IReadOnlyCollection<string> ignored)
    {
        var names = input.EnumerateObject().Select(property => property.Name).Where(name => !ignored.Contains(name)).ToArray();
        return names.Length == 0 ? null : string.Join(", ", names);
    }

    private static string? Length(JsonElement input, string name, Texts texts)
    {
        return input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? texts.Characters(value.GetString()!.Length)
            : null;
    }

    private static string? Lines(JsonElement input, Texts texts)
    {
        if (!input.TryGetProperty("script", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var script = value.GetString()!;
        return texts.ScriptSize(script.Split('\n').Length, script.Length);
    }

    private static string? WorksOn(JsonElement input, Texts texts)
    {
        var parts = new List<string>();
        if (Join(input, "worksOnSubjectTypes") is { } types)
        {
            parts.Add(types);
        }
        if (Flag(input, "worksOnAllSubjects"))
        {
            parts.Add(texts.AllTypes);
        }
        if (Flag(input, "worksOnApplication"))
        {
            parts.Add(texts.Application);
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string Owner(JsonElement input, string agentId, Texts texts)
    {
        return Text(input, "skillId") is { } skillId ? texts.SkillOf(skillId, agentId) : texts.AgentRules(agentId);
    }

    private static string Apply(JsonElement input, Texts texts)
    {
        if (!input.TryGetProperty("expectedVersion", out var version) || version.ValueKind != JsonValueKind.Number)
        {
            return texts.NewInShadow;
        }

        return Text(input, "apply") == NhAssistantSkillApplyModes.Shadow ? texts.AsCandidate : texts.LiveAtOnce;
    }

    private static string RuleScriptMode(JsonElement input, Texts texts)
    {
        if (Flag(input, "live"))
        {
            return texts.StraightLive;
        }

        return Flag(input, "shadow") ? texts.CandidateInShadow : texts.Candidate;
    }

    private static string Part(string? part, Texts texts)
    {
        return part switch
        {
            NhAssistantAgentBuilderTools.LiveParts.RuleScript => texts.PartRuleScript,
            NhAssistantAgentBuilderTools.LiveParts.SkillCandidate => texts.PartSkillCandidate,
            NhAssistantAgentBuilderTools.LiveParts.TriggerCandidate => texts.PartTriggerCandidate,
            NhAssistantAgentBuilderTools.ShadowParts.Skill => texts.PartSkill,
            NhAssistantAgentBuilderTools.ShadowParts.Trigger => texts.PartTrigger,
            NhAssistantAgentBuilderTools.ShadowParts.Agent => texts.PartAgent,
            NhAssistantAgentBuilderTools.StateParts.Consultation => texts.PartConsultation,
            _ => part ?? "?"
        };
    }

    /// <summary>
    /// One card while it is filled: its summary, fields and notice. A change of whose rights the agent
    /// uses and a removal set the notice that says so, unless four eyes already set one.
    /// </summary>
    private sealed class Card(Texts texts, string agentId)
    {
        public Texts Texts { get; } = texts;

        public string AgentId { get; } = agentId;

        public string Summary { get; set; } = string.Empty;

        public string? Notice { get; set; }

        public List<NhAssistantPresentationField> Fields { get; } = [new(texts.Agent, Bound(agentId, MaxValueLength))];

        public void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Fields.Add(new NhAssistantPresentationField(label, Bound(value, MaxValueLength)));
            }
        }

        /// <summary>Says that the change alters whose rights the agent uses, and what.</summary>
        public void Rights(IReadOnlyList<string> changes)
        {
            if (changes.Count == 0)
            {
                return;
            }

            var what = string.Join("; ", changes);
            Add(Texts.RightsLabel, what);
            Notice ??= Texts.RightsNotice(AgentId);
        }

        /// <summary>Says that the change removes something, and what.</summary>
        public void Removal(string what)
        {
            Add(Texts.RemovesLabel, what);
            Notice = Texts.RemovalNotice(what, AgentId);
        }
    }

    /// <summary>
    /// The labels and phrases of the cards in one language.
    /// </summary>
    private sealed class Texts(bool dutch)
    {
        public bool Dutch => dutch;
        public string Agent => "Agent";
        public string Name => dutch ? "Naam" : "Name";
        public string Tools => "Tools";
        public string Tool => "Tool";
        public string Autonomy => dutch ? "Autonomie" : "Autonomy";
        public string WorksOn => dutch ? "Werkt aan" : "Works on";
        public string Instructions => dutch ? "Instructies" : "Instructions";
        public string Starts => dutch ? "Start" : "Starts";
        public string Version => dutch ? "Versie" : "Version";
        public string Changes => dutch ? "Wijzigt" : "Changes";
        public string Skill => dutch ? "Vaardigheid" : "Skill";
        public string Trigger => "Trigger";
        public string Source => dutch ? "Bron" : "Source";
        public string SubjectSet => dutch ? "Onderwerpset" : "Subject set";
        public string SubjectType => dutch ? "Objecttype" : "Object type";
        public string RuleHandler => dutch ? "Regelhandler" : "Rule handler";
        public string Writes => dutch ? "Schrijfacties" : "Writes";
        public string Script => "Script";
        public string Revision => dutch ? "Revisie" : "Revision";
        public string Reason => dutch ? "Reden" : "Reason";
        public string RequiredPolicy => dutch ? "Vereist beleid" : "Required policy";
        public string McpServers => dutch ? "MCP-servers" : "MCP servers";
        public string McpServer => dutch ? "MCP-server" : "MCP server";
        public string HeadlessUse => dutch ? "Zonder persoon" : "Without a person";
        public string Accountability => dutch ? "Verantwoording" : "Accountability";
        public string TechnicalAccount => dutch ? "Technisch account" : "Technical account";
        public string Delegation => dutch ? "Rechten van wie de run start" : "Rights of whoever starts a run";
        public string GatewayReads => dutch ? "Lezen via de gateway" : "Gateway reads";
        public string ElevatedReads => dutch ? "Lezen met verhoogde rechten" : "Elevated reads";
        public string Routes => dutch ? "Routes" : "Routes";
        public string WhoMayAsk => dutch ? "Wie mag vragen" : "Who may ask";
        public string Sources => dutch ? "Bronnen" : "Sources";
        public string DailyLimit => dutch ? "Daglimiet" : "Daily limit";
        public string Entry => dutch ? "Veld" : "Entry";
        public string Value => dutch ? "Waarde" : "Value";
        public string State => dutch ? "Status" : "State";
        public string Mode => dutch ? "Stand" : "Mode";
        public string AlertKind => dutch ? "Soort waarschuwing" : "Alert kind";
        public string AlertKinds => dutch ? "Soorten waarschuwingen" : "Alert kinds";
        public string Groups => dutch ? "Groepen" : "Groups";
        public string RightsLabel => dutch ? "Rechten" : "Rights";
        public string RemovesLabel => dutch ? "Verwijdert" : "Removes";
        public string None => dutch ? "geen" : "none";
        public string New => dutch ? "nieuw" : "new";
        public string FromCode => dutch ? "uit de code" : "from code";
        public string ReadOnly => dutch ? "alleen lezen" : "read only";
        public string InShadow => dutch ? "in schaduw" : "in shadow";
        public string Live => "live";
        public string Off => dutch ? "uit" : "off";
        public string AllTypes => dutch ? "alle objecttypen" : "every object type";
        public string Application => dutch ? "de hele applicatie" : "the whole application";
        public string NewInShadow => dutch ? "nieuw, start in schaduw" : "new, it starts in shadow";
        public string AsCandidate => dutch ? "als kandidaat in schaduw naast de live versie" : "as a candidate in shadow next to the live version";
        public string LiveAtOnce => dutch ? "direct live" : "live at once";
        public string StraightLive => dutch ? "direct live" : "straight live";
        public string Candidate => dutch ? "als kandidaat, nog niet live" : "as a candidate, not live yet";
        public string CandidateInShadow => dutch ? "als kandidaat die in schaduw meedraait" : "as a candidate that runs in shadow";
        public string PartRuleScript => dutch ? "het regelscript" : "the rule script";
        public string PartSkillCandidate => dutch ? "de kandidaat van een vaardigheid" : "a skill's candidate";
        public string PartTriggerCandidate => dutch ? "de kandidaat van een trigger" : "a trigger's candidate";
        public string PartSkill => dutch ? "een vaardigheid" : "a skill";
        public string PartTrigger => dutch ? "een trigger" : "a trigger";
        public string PartAgent => dutch ? "de agent" : "the agent";
        public string PartConsultation => dutch ? "een raadpleging" : "a consultation";
        public string TheAgentItself => dutch ? "de agent zelf, met zijn run-instellingen, vaardigheden, triggers en metadata" : "the agent itself, with its run settings, skills, triggers and metadata";
        public string TheOverride => dutch ? "de aanpassingen van het beheer: de definitie uit de code geldt weer en de MCP-servers gaan eraf" : "the administration's override: the definition in code applies again and its MCP servers are removed";
        public string TheRunSettings => dutch ? "de run-instellingen: de agent chat daarna alleen nog" : "the run settings: the agent then only chats";
        public string VariantItself => dutch ? "de schaduwvariant zelf, na het overnemen" : "the shadow variant itself, once promoted";

        public string ApprovalNotice => dutch
            ? "De agentbouwer werkt met jouw eigen rechten. Er verandert niets tot je dit goedkeurt."
            : "The agent builder works with your own rights. Nothing changes until you approve this.";

        public string FourEyesSave => dutch
            ? "De code vraagt om vier ogen: niemand slaat een regelscript direct live op. Sla het op als kandidaat."
            : "The code requires four eyes: nobody saves a rule script straight live. Save it as a candidate.";

        public string FourEyesLive => dutch
            ? "De code vraagt om vier ogen: wie een versie opsloeg, ook via de agentbouwer, zet die niet zelf live."
            : "The code requires four eyes: whoever saved a version, also through the agent builder, does not set it live.";

        public string RightsNotice(string agentId)
        {
            return dutch
                ? $"Let op: dit verandert met wiens rechten agent {agentId} werkt. Zijn runs lezen en handelen daarna met die rechten. Er verandert niets tot je dit goedkeurt."
                : $"Note: this changes whose rights agent {agentId} works with. Its runs then read and act with those rights. Nothing changes until you approve this.";
        }

        public string RemovalNotice(string what, string agentId)
        {
            return dutch
                ? $"Let op: dit verwijdert {what} (agent {agentId}). In de chat is dat niet ongedaan te maken; versies blijven als geschiedenis waar het beheer die bewaart. Er verandert niets tot je dit goedkeurt."
                : $"Note: this removes {what} (agent {agentId}). It cannot be undone in the chat; versions stay as history where the administration keeps them. Nothing changes until you approve this.";
        }

        public string HeadlessUseNotice(string serverId)
        {
            return dutch
                ? $"Let op: dit verandert met wiens verbinding de tools van MCP-server {serverId} werken in runs zonder persoon, voor elke agent waaraan de server is toegewezen. De verbinding en de geheimen blijven zoals ze zijn."
                : $"Note: this changes whose connection the tools of MCP server {serverId} use in runs without a person, for every agent the server is assigned to. Its connection and secrets stay as they are.";
        }

        public string WithoutApprovalNotice(string agentId, string toolId)
        {
            return dutch
                ? $"Let op: de eigen runs van agent {agentId} roepen {toolId} daarna aan zonder dat iemand het goedkeurt. Er verandert niets tot je dit goedkeurt."
                : $"Note: agent {agentId}'s own runs then call {toolId} without anyone's approval. Nothing changes until you approve this.";
        }

        public string AccountabilityOf(string accountability)
        {
            return accountability switch
            {
                "agent" => dutch ? "de agent zelf" : "the agent itself",
                "subject-responsible" => dutch ? "de verantwoordelijke van het onderwerp" : "the subject's responsible person",
                "service-account" => dutch ? "een technisch account" : "a technical account",
                _ => accountability
            };
        }

        public string OnOff(bool on)
        {
            return on ? (dutch ? "aan" : "on") : (dutch ? "uit" : "off");
        }

        public string StateSummary(string part, string? name, string agentId, string? state)
        {
            var of = name is null ? part : $"{part} {name}";
            return state switch
            {
                "stopped" => dutch
                    ? $"Stopt {of} van agent {agentId}: lopende runs worden afgebroken."
                    : $"Stops {of} of agent {agentId}: its active runs are cancelled.",
                "paused" => dutch
                    ? $"Pauzeert {of} van agent {agentId}: er start niets nieuws en gebeurtenissen wachten."
                    : $"Pauses {of} of agent {agentId}: nothing new starts and events wait.",
                "off" => dutch
                    ? $"Zet {of} van agent {agentId} uit: triggers worden genegeerd."
                    : $"Switches {of} of agent {agentId} off: its triggers are ignored.",
                "on" => dutch
                    ? $"Zet {of} van agent {agentId} aan."
                    : $"Switches {of} of agent {agentId} on.",
                _ => dutch
                    ? $"Maakt {of} van agent {agentId} weer actief."
                    : $"Makes {of} of agent {agentId} active again."
            };
        }

        public string AssignmentOf(string? serverId)
        {
            return dutch ? $"de toewijzing van MCP-server {serverId}" : $"the assignment of MCP server {serverId}";
        }

        public string TheSkill(string? skillId)
        {
            return dutch ? $"vaardigheid {skillId}" : $"skill {skillId}";
        }

        public string TheTrigger(string? triggerId)
        {
            return dutch ? $"trigger {triggerId}" : $"trigger {triggerId}";
        }

        public string TheConsultation(string? subjectType)
        {
            return dutch ? $"de raadpleging over {subjectType}" : $"the consultation about {subjectType}";
        }

        public string TheHandOff(string? subjectType)
        {
            return dutch ? $"de overdracht van werk aan {subjectType}" : $"the hand-off of work on {subjectType}";
        }

        public string TheMetadata(string? name)
        {
            return dutch ? $"de opgeslagen waarde van metadata-veld {name}" : $"the stored value of metadata entry {name}";
        }

        public string TheSkillCandidate(string? skillId, string? version)
        {
            return dutch ? $"kandidaat-versie {version} van vaardigheid {skillId}" : $"candidate version {version} of skill {skillId}";
        }

        public string TheTriggerCandidate(string? triggerId, string? version)
        {
            return dutch ? $"kandidaat-versie {version} van trigger {triggerId}" : $"candidate version {version} of trigger {triggerId}";
        }

        public string TheRuleScriptVersion(string? skillId, string? version)
        {
            var owner = skillId is null ? string.Empty : (dutch ? $" van vaardigheid {skillId}" : $" of skill {skillId}");
            return dutch ? $"kandidaat-versie {version} van het regelscript{owner}" : $"candidate version {version} of the rule script{owner}";
        }

        public string Characters(int count)
        {
            return dutch
                ? count.ToString("N0", CultureInfo.GetCultureInfo("nl-NL")) + " tekens"
                : count.ToString("N0", CultureInfo.InvariantCulture) + " characters";
        }

        public string ScriptSize(int lines, int characters)
        {
            return dutch
                ? $"{lines} regels, {Characters(characters)}"
                : $"{lines} lines, {Characters(characters)}";
        }

        public string SkillOf(string skillId, string agentId)
        {
            return dutch ? $"vaardigheid {skillId} van agent {agentId}" : $"skill {skillId} of agent {agentId}";
        }

        public string AgentRules(string agentId)
        {
            return dutch ? $"de regels van agent {agentId}" : $"the rules of agent {agentId}";
        }
    }
}
