using System.Globalization;
using System.Text.Json;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// Names the agent builder's tools and says on the approval card of every write what changes (RB-8), in
/// English and Dutch: the agent, the part and how the change takes effect, with what it sets. The card is
/// read from the call's own input, which is what runs once approved; the input in full stays in its
/// technical details.
/// </summary>
internal sealed class NhAssistantAgentBuilderPresenter(NhAssistantRegistrationState registration) : INhAssistantToolPresenter
{
    private const int MaxValueLength = NhAssistantPresentationLimits.MaxFieldValueLength;

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
        var fields = new List<NhAssistantPresentationField> { new(texts.Agent, agentId) };
        string summary;
        string? notice = null;
        switch (toolId)
        {
            case NhAssistantAgentBuilderTools.CreateAgent:
                summary = dutch
                    ? $"Maakt de nieuwe agent '{Text(input, "displayName")}' aan in het beheer, zonder run-instellingen, vaardigheden of triggers."
                    : $"Creates the new agent '{Text(input, "displayName")}' in the administration, without run settings, skills or triggers.";
                Add(fields, texts.Name, Text(input, "displayName"));
                Add(fields, texts.Tools, Join(input, "toolSelectors"));
                Add(fields, texts.Autonomy, Text(input, "autonomy") ?? "propose");
                Add(fields, texts.WorksOn, WorksOn(input, texts));
                Add(fields, texts.Instructions, Length(input, "instructions", texts));
                Add(fields, texts.Starts, Flag(input, "startsInShadow") ? texts.InShadow : texts.Live);
                break;
            case NhAssistantAgentBuilderTools.UpdateAgent:
                summary = dutch
                    ? $"Wijzigt de definitie van agent {agentId} als zijn volgende versie; velden die niet genoemd worden blijven zoals ze zijn."
                    : $"Changes the definition of agent {agentId} as its next version; fields it does not name stay as they are.";
                Add(fields, texts.Version, Number(input, "expectedVersion"));
                Add(fields, texts.Name, Text(input, "displayName"));
                Add(fields, texts.Description, Text(input, "description"));
                Add(fields, texts.Instructions, input.TryGetProperty("instructions", out _) ? Length(input, "instructions", texts) : null);
                Add(fields, texts.Tools, Join(input, "toolSelectors"));
                Add(fields, texts.Autonomy, Text(input, "autonomy"));
                break;
            case NhAssistantAgentBuilderTools.SaveRunSettings:
                summary = dutch
                    ? $"Slaat de run-instellingen van agent {agentId} op. Met wiens rechten de agent werkt, blijft zoals het is."
                    : $"Saves the run settings of agent {agentId}. Whose rights the agent works with stays as it is.";
                Add(fields, texts.Changes, Names(input, ["agentId", "expectedVersion", "reason"]));
                Add(fields, texts.Version, Number(input, "expectedVersion") ?? texts.New);
                break;
            case NhAssistantAgentBuilderTools.SaveSkill:
                summary = dutch
                    ? $"Slaat vaardigheid {Text(input, "skillId")} van agent {agentId} op: {Apply(input, texts)}."
                    : $"Saves skill {Text(input, "skillId")} of agent {agentId}: {Apply(input, texts)}.";
                Add(fields, texts.Skill, Text(input, "skillId"));
                Add(fields, texts.Version, Number(input, "expectedVersion") ?? texts.New);
                Add(fields, texts.Tools, Join(input, "tools"));
                Add(fields, texts.RuleHandler, Text(input, "ruleHandler"));
                Add(fields, texts.Instructions, input.TryGetProperty("instructions", out _) ? Length(input, "instructions", texts) : null);
                break;
            case NhAssistantAgentBuilderTools.SaveTrigger:
                summary = dutch
                    ? $"Slaat trigger {Text(input, "triggerId")} van agent {agentId} op: {Apply(input, texts)}."
                    : $"Saves trigger {Text(input, "triggerId")} of agent {agentId}: {Apply(input, texts)}.";
                Add(fields, texts.Trigger, Text(input, "triggerId"));
                Add(fields, texts.Version, Number(input, "expectedVersion") ?? texts.New);
                Add(fields, texts.Source, Text(input, "source") == "schedule"
                    ? $"{Text(input, "cron")} {Text(input, "timeZone") ?? "UTC"}".Trim()
                    : Text(input, "event"));
                Add(fields, texts.SubjectSet, Text(input, "subjectSet"));
                Add(fields, texts.Skill, Text(input, "skill"));
                break;
            case NhAssistantAgentBuilderTools.SaveRuleScript:
                summary = dutch
                    ? $"Slaat een nieuwe versie op van het regelscript van {Owner(input, agentId, texts)}: {RuleScriptMode(input, texts)}."
                    : $"Saves a new version of the rule script of {Owner(input, agentId, texts)}: {RuleScriptMode(input, texts)}.";
                Add(fields, texts.Skill, Text(input, "skillId"));
                Add(fields, texts.Writes, Join(input, "writes") ?? texts.None);
                Add(fields, texts.Script, Lines(input, texts));
                Add(fields, texts.Revision, Number(input, "expectedRevision"));
                if (Flag(input, "live") && registration.RuleScriptsAsData?.RequireFourEyes == true)
                {
                    notice = texts.FourEyesSave;
                }
                break;
            case NhAssistantAgentBuilderTools.SetLive:
                summary = dutch
                    ? $"Zet versie {Number(input, "version")} van {Part(Text(input, "part"), texts)} van agent {agentId} live; de huidige live versie blijft als geschiedenis."
                    : $"Sets version {Number(input, "version")} of {Part(Text(input, "part"), texts)} of agent {agentId} live; the version that is live now stays as history.";
                Add(fields, texts.Skill, Text(input, "skillId"));
                Add(fields, texts.Trigger, Text(input, "triggerId"));
                Add(fields, texts.Revision, Number(input, "expectedRevision"));
                if (Text(input, "part") == NhAssistantAgentBuilderTools.LiveParts.RuleScript && registration.RuleScriptsAsData?.RequireFourEyes == true)
                {
                    notice = texts.FourEyesLive;
                }
                break;
            case NhAssistantAgentBuilderTools.SetShadow:
                var shadow = Flag(input, "shadow");
                summary = dutch
                    ? $"{(shadow ? "Zet" : "Haalt")} {Part(Text(input, "part"), texts)} van agent {agentId} {(shadow ? "in schaduw: het werkt zonder effecten" : "uit schaduw: het werkt live")}."
                    : $"{(shadow ? "Puts" : "Takes")} {Part(Text(input, "part"), texts)} of agent {agentId} {(shadow ? "in shadow: it works without effects" : "out of shadow: it works live")}.";
                Add(fields, texts.Skill, Text(input, "skillId"));
                Add(fields, texts.Trigger, Text(input, "triggerId"));
                Add(fields, texts.Version, Number(input, "version"));
                break;
            default:
                return ValueTask.FromResult<NhAssistantApprovalPresentation?>(null);
        }

        Add(fields, texts.Reason, Text(input, "reason"));
        return ValueTask.FromResult<NhAssistantApprovalPresentation?>(new NhAssistantApprovalPresentation(
            NameOf(toolId, dutch),
            Bound(summary, NhAssistantPresentationLimits.MaxSummaryLength),
            fields.Take(NhAssistantPresentationLimits.MaxFields).ToArray(),
            notice ?? texts.ApprovalNotice));
    }

    private static string NameOf(string toolId, bool dutch)
    {
        return toolId switch
        {
            NhAssistantAgentBuilderTools.ReadCatalog => dutch ? "Bouwstenen lezen" : "Read the building blocks",
            NhAssistantAgentBuilderTools.ListAgents => dutch ? "Agents opsommen" : "List the agents",
            NhAssistantAgentBuilderTools.ReadAgent => dutch ? "Agent lezen" : "Read an agent",
            NhAssistantAgentBuilderTools.ReadRuleScript => dutch ? "Regelscript lezen" : "Read a rule script",
            NhAssistantAgentBuilderTools.DryRunRuleScript => dutch ? "Regelscript proefdraaien" : "Dry-run a rule script",
            NhAssistantAgentBuilderTools.CreateAgent => dutch ? "Agent aanmaken" : "Create an agent",
            NhAssistantAgentBuilderTools.UpdateAgent => dutch ? "Agent wijzigen" : "Change an agent",
            NhAssistantAgentBuilderTools.SaveRunSettings => dutch ? "Run-instellingen opslaan" : "Save run settings",
            NhAssistantAgentBuilderTools.SaveSkill => dutch ? "Vaardigheid opslaan" : "Save a skill",
            NhAssistantAgentBuilderTools.SaveTrigger => dutch ? "Trigger opslaan" : "Save a trigger",
            NhAssistantAgentBuilderTools.SaveRuleScript => dutch ? "Regelscript opslaan" : "Save a rule script",
            NhAssistantAgentBuilderTools.SetLive => dutch ? "Versie live zetten" : "Set a version live",
            NhAssistantAgentBuilderTools.SetShadow => dutch ? "Schaduw wijzigen" : "Change shadow",
            _ => toolId
        };
    }

    private static bool IsDutch(CultureInfo culture)
    {
        return string.Equals(culture.TwoLetterISOLanguageName, "nl", StringComparison.OrdinalIgnoreCase);
    }

    private static void Add(List<NhAssistantPresentationField> fields, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Add(new NhAssistantPresentationField(label, Bound(value, MaxValueLength)));
        }
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
            _ => part ?? "?"
        };
    }

    /// <summary>
    /// The labels and phrases of the cards in one language.
    /// </summary>
    private sealed class Texts(bool dutch)
    {
        public string Agent => "Agent";
        public string Name => dutch ? "Naam" : "Name";
        public string Description => dutch ? "Omschrijving" : "Description";
        public string Tools => "Tools";
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
        public string RuleHandler => dutch ? "Regelhandler" : "Rule handler";
        public string Writes => dutch ? "Schrijfacties" : "Writes";
        public string Script => "Script";
        public string Revision => dutch ? "Revisie" : "Revision";
        public string Reason => dutch ? "Reden" : "Reason";
        public string None => dutch ? "geen" : "none";
        public string New => dutch ? "nieuw" : "new";
        public string InShadow => dutch ? "in schaduw" : "in shadow";
        public string Live => "live";
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

        public string ApprovalNotice => dutch
            ? "De agentbouwer werkt met jouw eigen rechten. Er verandert niets tot je dit goedkeurt."
            : "The agent builder works with your own rights. Nothing changes until you approve this.";

        public string FourEyesSave => dutch
            ? "De code vraagt om vier ogen: niemand slaat een regelscript direct live op. Sla het op als kandidaat."
            : "The code requires four eyes: nobody saves a rule script straight live. Save it as a candidate.";

        public string FourEyesLive => dutch
            ? "De code vraagt om vier ogen: wie een versie opsloeg, ook via de agentbouwer, zet die niet zelf live."
            : "The code requires four eyes: whoever saved a version, also through the agent builder, does not set it live.";

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
