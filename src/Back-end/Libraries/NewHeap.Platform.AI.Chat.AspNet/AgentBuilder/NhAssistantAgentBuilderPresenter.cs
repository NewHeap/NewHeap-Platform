using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// Names the agent builder's tools and says on the approval card of every write what changes (RB-8,
/// FB94), in English and Dutch. The card is built from the call's input checked against the tool's schema
/// with the accessors the handler uses (<see cref="NhAssistantAgentBuilderInput"/>), so it says what runs: a
/// summary of the change, and every property the call gives with its value; what it leaves out stays as it
/// is. A change of whose rights an agent uses and every removal say so plainly in the card's notice, and
/// every value of whose rights and what is removed has a field of its own, in full. Other values share
/// fields and are shortened when the card would not fit otherwise, and the notice says the technical
/// details show them in full. A write whose card cannot be built so within the presentation's limits is
/// never proposed: the catalog refuses the call first (<see cref="RefusalOfCardAsync"/>).
/// </summary>
internal sealed class NhAssistantAgentBuilderPresenter(NhAssistantRegistrationState registration, IServiceProvider services)
    : INhAssistantToolPresenter
{
    private const int MaxValueLength = NhAssistantPresentationLimits.MaxFieldValueLength;

    /// <summary>The shortest a shortened value gets, so it still says what it is.</summary>
    private const int MinShownLength = 16;

    /// <summary>The run settings that decide whose rights an agent's runs use.</summary>
    private static readonly string[] RunSettingsRights = ["accountability", "serviceAccountActorId", "delegateFromInitiator", "allowGatewayReads", "elevatedReads"];

    /// <summary>The parts of an agent's definition that decide who reaches it, what it may use and who sees its results.</summary>
    private static readonly string[] DefinitionRights = ["requiredPolicy", "mcpServerIds", "runPolicy", "viewPolicy"];

    /// <summary>Who may consult an agent.</summary>
    private static readonly string[] ConsultationRights = ["routes", "askerPolicy", "minimumRole"];

    /// <summary>Who decides a consultation's proposal and with whose rights it executes.</summary>
    private static readonly string[] RouteRights = ["askerDecides", "subjectAgentRights", "subjectAskerRights"];

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

    public async ValueTask<NhAssistantApprovalPresentation?> PresentApprovalAsync(
        NhAssistantApprovalPresentationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var toolId = context.Descriptor.Id;
        var tool = NhAssistantAgentBuilderTools.All.FirstOrDefault(item => string.Equals(item.Id, toolId, StringComparison.Ordinal));
        if (tool is null || context.Arguments is not JsonElement { ValueKind: JsonValueKind.Object } arguments)
        {
            return null;
        }

        // The same check and accessors as the handler: input that would be refused gets no card of its own,
        // and the catalog never proposes it.
        var (input, _) = NhAssistantAgentBuilderInput.Read(arguments, tool.Input.Schema);
        if (input is null)
        {
            return null;
        }

        var (card, _) = await BuildAsync(tool, input, IsDutch(context.Culture), cancellationToken);
        return card;
    }

    /// <summary>
    /// Why the approval card of a write cannot be built within the presentation's limits in English or
    /// Dutch, the languages it is shown in, as a message the model can act on; <see langword="null"/> when it
    /// can, or for a tool without a card. A write is proposed only with its own valid card that shows every
    /// value of whose rights it changes and what it removes, never with the generic summary.
    /// </summary>
    internal static async Task<string?> RefusalOfCardAsync(
        IServiceProvider services,
        NhAssistantAgentBuilderTool tool,
        NhAssistantAgentBuilderInput input,
        CancellationToken cancellationToken)
    {
        var presenter = new NhAssistantAgentBuilderPresenter(services.GetRequiredService<NhAssistantRegistrationState>(), services);
        foreach (var dutch in new[] { false, true })
        {
            var (card, refusal) = await presenter.BuildAsync(tool, input, dutch, cancellationToken);
            if (refusal is not null)
            {
                return refusal;
            }
            if (card is null && tool.Approval == NhAiApprovalRequirement.Required)
            {
                return Refusals.NoCard;
            }
        }

        return null;
    }

    /// <summary>
    /// The card of one write in one language, or why it cannot be built; neither for a tool without a card.
    /// </summary>
    internal async Task<(NhAssistantApprovalPresentation? Card, string? Refusal)> BuildAsync(
        NhAssistantAgentBuilderTool tool,
        NhAssistantAgentBuilderInput input,
        bool dutch,
        CancellationToken cancellationToken)
    {
        try
        {
            var texts = new Texts(dutch);
            var card = new Card(texts, input.Text("agentId") ?? "?");
            if (!await DescribeAsync(tool.Id, input, card, cancellationToken))
            {
                return (null, null);
            }

            card.AddGiven(input, tool.Input.Properties, ShownInFullOf(tool));
            return card.ToPresentation(NameOf(tool.Id, dutch));
        }
        catch (NhAssistantAgentBuilderInputException exception)
        {
            // A value the schema allows but an accessor cannot read, such as a number beyond its range: the
            // handler refuses it as well.
            return (null, $"'{exception.Property}' cannot be read as its schema says. Send it as a value its schema allows.");
        }
    }

    /// <summary>
    /// The properties of a tool whose values the card shows in full, each in a field of its own: those that
    /// decide whose rights an agent uses or who reaches it, and what a removal names.
    /// </summary>
    private static IReadOnlySet<string> ShownInFullOf(NhAssistantAgentBuilderTool tool)
    {
        string[] names = tool.Id switch
        {
            NhAssistantAgentBuilderTools.CreateAgent => ["requiredPolicy", "runPolicy", "viewPolicy", "shadowOf"],
            NhAssistantAgentBuilderTools.UpdateAgent => DefinitionRights,
            NhAssistantAgentBuilderTools.SaveRunSettings => RunSettingsRights,
            NhAssistantAgentBuilderTools.SaveConsultation => ConsultationRights,
            NhAssistantAgentBuilderTools.SaveHandOff => ["sources"],
            NhAssistantAgentBuilderTools.SetTechnicalAccount => ["actorId"],
            NhAssistantAgentBuilderTools.SetConsultationRoutes => RouteRights,
            NhAssistantAgentBuilderTools.SetMcpServer => ["serverId", "assigned"],
            NhAssistantAgentBuilderTools.SetMcpHeadlessUse => ["serverId", "headlessUse"],
            NhAssistantAgentBuilderTools.SetSwitch => ["part", "toolId", "kindId", "mode"],
            NhAssistantAgentBuilderTools.Remove => tool.Input.Properties.Where(name => name is not "agentId" and not "reason").ToArray(),
            _ => []
        };
        return names.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The summary and the notice of one write; <see langword="false"/> for a tool that has no card, such as
    /// a read.
    /// </summary>
    private async Task<bool> DescribeAsync(string toolId, NhAssistantAgentBuilderInput input, Card card, CancellationToken cancellationToken)
    {
        var texts = card.Texts;
        var dutch = texts.Dutch;
        var agentId = card.AgentId;
        switch (toolId)
        {
            case NhAssistantAgentBuilderTools.CreateAgent:
                card.Summary = input.Text("shadowOf") is { } shadowOf
                    ? (dutch
                        ? $"Maakt '{input.Text("displayName")}' aan als schaduwvariant van agent {shadowOf}: die krijgt de triggers van de live agent en werkt altijd in schaduw."
                        : $"Creates '{input.Text("displayName")}' as a shadow variant of agent {shadowOf}: it receives the live agent's triggers and always works in shadow.")
                    : (dutch
                        ? $"Maakt de nieuwe agent '{input.Text("displayName")}' aan in het beheer, zonder run-instellingen, vaardigheden, triggers of MCP-servers."
                        : $"Creates the new agent '{input.Text("displayName")}' in the administration, without run settings, skills, triggers or MCP servers.");
                return true;
            case NhAssistantAgentBuilderTools.UpdateAgent:
                card.Summary = dutch
                    ? $"Wijzigt de definitie van agent {agentId} als zijn volgende versie; velden die niet genoemd worden blijven zoals ze zijn."
                    : $"Changes the definition of agent {agentId} as its next version; fields it does not name stay as they are.";
                card.Rights(DefinitionRights.Where(input.Given).ToArray());
                return true;
            case NhAssistantAgentBuilderTools.SaveRunSettings:
                card.Summary = dutch
                    ? $"Slaat de run-instellingen van agent {agentId} op; instellingen die niet genoemd worden blijven zoals ze zijn."
                    : $"Saves the run settings of agent {agentId}; settings it does not name stay as they are.";
                var settings = RunSettingsRights.Where(input.Given).ToList();
                // Another accountability without a technical account named leaves the technical account out.
                if (input.Text("accountability") is { } accountability
                    && !string.Equals(accountability, NhAssistantAccountabilities.ServiceAccount, StringComparison.Ordinal)
                    && !input.Given("serviceAccountActorId"))
                {
                    card.AddField(texts.LabelOf("serviceAccountActorId"), texts.NoTechnicalAccount, "serviceAccountActorId");
                    settings.Add("serviceAccountActorId");
                }
                card.Rights(settings);
                return true;
            case NhAssistantAgentBuilderTools.SaveSkill:
                card.Summary = dutch
                    ? $"Slaat vaardigheid {input.Text("skillId")} van agent {agentId} op: {Apply(input, texts)}; velden die niet genoemd worden blijven zoals ze zijn."
                    : $"Saves skill {input.Text("skillId")} of agent {agentId}: {Apply(input, texts)}; fields it does not name stay as they are.";
                return true;
            case NhAssistantAgentBuilderTools.SaveTrigger:
                card.Summary = dutch
                    ? $"Slaat trigger {input.Text("triggerId")} van agent {agentId} op: {Apply(input, texts)}; velden die niet genoemd worden blijven zoals ze zijn zolang de bron dezelfde blijft."
                    : $"Saves trigger {input.Text("triggerId")} of agent {agentId}: {Apply(input, texts)}; fields it does not name stay as they are while the source stays the same.";
                return true;
            case NhAssistantAgentBuilderTools.SaveRuleScript:
                card.Summary = dutch
                    ? $"Slaat een nieuwe versie op van het regelscript van {Owner(input, agentId, texts)}: {RuleScriptMode(input, texts)}."
                    : $"Saves a new version of the rule script of {Owner(input, agentId, texts)}: {RuleScriptMode(input, texts)}.";
                if (input.Bool("live") == true && registration.RuleScriptsAsData?.RequireFourEyes == true)
                {
                    card.Notice = texts.FourEyesSave;
                }
                return true;
            case NhAssistantAgentBuilderTools.SaveConsultation:
                card.Summary = dutch
                    ? $"Slaat de raadpleging over {input.Text("subjectType")} van agent {agentId} op: andere assistenten mogen de agent daarover raadplegen; velden die niet genoemd worden blijven zoals ze zijn."
                    : $"Saves the consultation about {input.Text("subjectType")} of agent {agentId}: other assistants may consult the agent about it; fields it does not name stay as they are.";
                card.Rights(ConsultationRights.Where(input.Given).ToArray());
                return true;
            case NhAssistantAgentBuilderTools.SaveHandOff:
                card.Summary = dutch
                    ? $"Slaat de overdracht van werk aan {input.Text("subjectType")} naar agent {agentId} op; velden die niet genoemd worden blijven zoals ze zijn."
                    : $"Saves the hand-off of work on {input.Text("subjectType")} to agent {agentId}; fields it does not name stay as they are.";
                return true;
            case NhAssistantAgentBuilderTools.SaveMetadata:
                card.Summary = dutch
                    ? $"Slaat het metadata-veld {input.Text("name")} van agent {agentId} op."
                    : $"Saves the metadata entry {input.Text("name")} of agent {agentId}.";
                return true;
            case NhAssistantAgentBuilderTools.SetLive:
                card.Summary = dutch
                    ? $"Zet versie {input.Int("version")} van {Part(input.Text("part"), texts)} van agent {agentId} live; de huidige live versie blijft als geschiedenis."
                    : $"Sets version {input.Int("version")} of {Part(input.Text("part"), texts)} of agent {agentId} live; the version that is live now stays as history.";
                if (input.Text("part") == NhAssistantAgentBuilderTools.LiveParts.RuleScript && registration.RuleScriptsAsData?.RequireFourEyes == true)
                {
                    card.Notice = texts.FourEyesLive;
                }
                return true;
            case NhAssistantAgentBuilderTools.SetShadow:
                var shadow = input.Bool("shadow") == true;
                card.Summary = dutch
                    ? $"{(shadow ? "Zet" : "Haalt")} {Part(input.Text("part"), texts)} van agent {agentId} {(shadow ? "in schaduw: het werkt zonder effecten" : "uit schaduw: het werkt live")}."
                    : $"{(shadow ? "Puts" : "Takes")} {Part(input.Text("part"), texts)} of agent {agentId} {(shadow ? "in shadow: it works without effects" : "out of shadow: it works live")}.";
                return true;
            case NhAssistantAgentBuilderTools.SetState:
                card.Summary = texts.StateSummary(Part(input.Text("part"), texts), input.Text("skillId") ?? input.Text("subjectType"), agentId, input.Text("state"));
                return true;
            case NhAssistantAgentBuilderTools.SetTechnicalAccount:
                var account = input.Text("actorId");
                card.Summary = dutch
                    ? $"Wijzigt het technische account van agent {agentId}: {account ?? "weer het account in de code"}."
                    : $"Changes the technical account of agent {agentId}: {account ?? "the account in code again"}.";
                card.Rights(["actorId"]);
                return true;
            case NhAssistantAgentBuilderTools.SetConsultationRoutes:
                card.Summary = dutch
                    ? $"Schakelt de routes van de raadplegingen van agent {agentId}: wie een voorstel beslist en met wiens rechten het wordt uitgevoerd."
                    : $"Switches the routes of agent {agentId}'s consultations: who decides a proposal and with whose rights it executes.";
                card.Rights(RouteRights.Where(input.Given).ToArray());
                return true;
            case NhAssistantAgentBuilderTools.SetMcpServer:
                var assigned = input.Bool("assigned") == true;
                var server = input.Text("serverId");
                card.Summary = assigned
                    ? (dutch
                        ? $"Wijst MCP-server {server} toe aan agent {agentId}: de ingeschakelde tools van de server worden aangeboden in zijn beurten."
                        : $"Assigns MCP server {server} to agent {agentId}: the server's enabled tools are offered in its turns.")
                    : (dutch
                        ? $"Haalt MCP-server {server} weg bij agent {agentId}: zijn beurten krijgen de tools van de server niet meer."
                        : $"Removes MCP server {server} from agent {agentId}: its turns no longer get the server's tools.");
                if (assigned)
                {
                    card.Rights(["serverId"]);
                }
                else
                {
                    card.Removal(texts.AssignmentOf(server));
                }
                return true;
            case NhAssistantAgentBuilderTools.SetMcpHeadlessUse:
                var mcp = input.Text("serverId");
                card.Summary = dutch
                    ? $"Wijzigt alleen of de tools van MCP-server {mcp} worden aangeboden in runs zonder persoon: {input.Text("headlessUse")}."
                    : $"Changes only whether the tools of MCP server {mcp} are offered in runs without a person: {input.Text("headlessUse")}.";
                card.Notice = texts.HeadlessUseNotice(mcp ?? "?");
                return true;
            case NhAssistantAgentBuilderTools.SetSwitch:
                var tool = input.Text("toolId") ?? input.Text("kindId");
                var mode = input.Text("mode");
                card.Summary = input.Text("part") == NhAssistantAgentBuilderTools.SwitchParts.AlertModel
                    ? (dutch
                        ? $"Zet de schakelaar voor waarschuwingen van soort {tool} door het model van agent {agentId} op {mode}."
                        : $"Sets the switch for alerts of kind {tool} by agent {agentId}'s model to {mode}.")
                    : (dutch
                        ? $"Zet de schakelaar van agent {agentId} voor tool {tool} zonder goedkeuring op {mode}."
                        : $"Sets agent {agentId}'s switch for running tool {tool} without approval to {mode}.");
                if (input.Text("part") == NhAssistantAgentBuilderTools.SwitchParts.ToolWithoutApproval && mode == "live")
                {
                    card.Notice = texts.WithoutApprovalNotice(agentId, tool ?? "?");
                }
                return true;
            case NhAssistantAgentBuilderTools.SetGroups:
                card.Summary = dutch
                    ? $"Zet agent {agentId} in precies deze groepen en uit de andere."
                    : $"Puts agent {agentId} in exactly these groups and out of the others.";
                return true;
            case NhAssistantAgentBuilderTools.PromoteShadowVariant:
                await DescribePromotionAsync(input, card, cancellationToken);
                return true;
            case NhAssistantAgentBuilderTools.Remove:
                var what = RemovalOf(input, texts);
                card.Summary = dutch
                    ? $"Verwijdert {what} van agent {agentId}."
                    : $"Removes {what} of agent {agentId}.";
                card.Removal(what);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A promotion changes the live agent: the card names it and what it takes over from the variant as the
    /// variant is now, at the version the call names, which the promotion holds it to; it says that this
    /// changes whose rights the live agent works with, and that the variant goes. A variant that is not
    /// there or has another version is refused before it is proposed.
    /// </summary>
    private async Task DescribePromotionAsync(NhAssistantAgentBuilderInput input, Card card, CancellationToken cancellationToken)
    {
        var texts = card.Texts;
        var variantId = card.AgentId;
        var directory = services.GetService<INhAssistantAgentDirectory>();
        var variant = directory is null ? null : await directory.GetAsync(variantId, cancellationToken);
        if (variant?.ShadowOf is not { } liveId)
        {
            card.Refuse($"Agent {variantId} is not a shadow variant that can be promoted now: read it again with builder_read_agent.");
            return;
        }
        if (input.Int("expectedVariantVersion") is not { } expected || expected != variant.Version)
        {
            card.Refuse($"Shadow variant {variantId} is at version {variant.Version.ToString(CultureInfo.InvariantCulture)} now: read it again and name the version you evaluated as expectedVariantVersion.");
            return;
        }

        var live = await directory!.GetAsync(liveId, cancellationToken);
        var toAgentInAdministration = string.Equals(live?.Source, NhAssistantAgentSources.Admin, StringComparison.Ordinal);
        var definition = services.GetService<NhAssistantAgentCatalog>() is { } catalog
            ? await catalog.FindAsync(variantId, includeDisabled: true, cancellationToken)
            : null;
        card.Summary = texts.PromotionSummary(variantId, liveId, input.Bool("moveSkills") == true, input.Bool("asPackageVersion") == true);
        card.AddField(texts.LiveAgent, liveId, "the live agent");
        var (settings, tools) = texts.TakesOverOf(variant, definition, toAgentInAdministration);
        card.AddField(texts.TakesOver, settings, "what the live agent takes over");
        card.AddField(texts.TakesOverTools, tools, "the tools the live agent takes over");
        card.Notice = texts.PromotionNotice(variantId, liveId, toAgentInAdministration);
    }

    private static string RemovalOf(NhAssistantAgentBuilderInput input, Texts texts)
    {
        var skill = input.Text("skillId");
        var trigger = input.Text("triggerId");
        var subjectType = input.Text("subjectType");
        var version = input.Int("version")?.ToString(CultureInfo.InvariantCulture);
        return input.Text("part") switch
        {
            NhAssistantAgentBuilderTools.RemoveParts.Agent => texts.TheAgentItself,
            NhAssistantAgentBuilderTools.RemoveParts.Override => texts.TheOverride,
            NhAssistantAgentBuilderTools.RemoveParts.RunSettings => texts.TheRunSettings,
            NhAssistantAgentBuilderTools.RemoveParts.Skill => texts.TheSkill(skill),
            NhAssistantAgentBuilderTools.RemoveParts.Trigger => texts.TheTrigger(trigger),
            NhAssistantAgentBuilderTools.RemoveParts.Consultation => texts.TheConsultation(subjectType),
            NhAssistantAgentBuilderTools.RemoveParts.HandOff => texts.TheHandOff(subjectType),
            NhAssistantAgentBuilderTools.RemoveParts.Metadata => texts.TheMetadata(input.Text("name")),
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

    /// <summary><paramref name="value"/> without control characters, as the card shows it.</summary>
    private static string Printable(string value)
    {
        return new string(value.Where(character => !char.IsControl(character)).ToArray());
    }

    /// <summary>
    /// <paramref name="value"/> in at most <paramref name="maximum"/> characters: as it is, or shortened
    /// with an ellipsis, never between the halves of a character.
    /// </summary>
    private static string Fit(string value, int maximum)
    {
        if (value.Length <= maximum)
        {
            return value;
        }

        var length = maximum - 1;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }
        return value[..length] + "…";
    }

    /// <summary>The characters <paramref name="value"/> takes in the stored JSON of a card, escapes included.</summary>
    private static int EscapedLength(string value)
    {
        return JsonSerializer.Serialize(value, NhAssistantContent.JsonOptions).Length - 2;
    }

    private static string Owner(NhAssistantAgentBuilderInput input, string agentId, Texts texts)
    {
        return input.Text("skillId") is { } skillId ? texts.SkillOf(skillId, agentId) : texts.AgentRules(agentId);
    }

    private static string Apply(NhAssistantAgentBuilderInput input, Texts texts)
    {
        if (input.Int("expectedVersion") is null)
        {
            return texts.NewInShadow;
        }

        return input.Text("apply") == NhAssistantSkillApplyModes.Shadow ? texts.AsCandidate : texts.LiveAtOnce;
    }

    private static string RuleScriptMode(NhAssistantAgentBuilderInput input, Texts texts)
    {
        if (input.Bool("live") == true)
        {
            return texts.StraightLive;
        }

        return input.Bool("shadow") == true ? texts.CandidateInShadow : texts.Candidate;
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
    /// The messages of a call whose card cannot be built, for the model, which can act on them.
    /// </summary>
    private static class Refusals
    {
        public const string NoCard = "The approval card of this change cannot be built, so it is not proposed. Read the agent again and make the change in smaller steps.";

        public const string Summary = "The approval card cannot name this change in full: its ids or names are too long to show. Use shorter ones.";

        public const string Notice = "The approval card cannot say in full what this change does to whose rights the agent uses or what it removes. Use shorter ids or make the change in smaller steps.";

        public const string TooMany = "The approval card cannot show every value of whose rights this change alters in a field of its own. Make the change in smaller steps.";

        public const string Size = "The approval card cannot show this change within its size: the values of whose rights it alters or what it removes take too much room. Make them shorter or make the change in smaller steps.";

        public static string TooLong(string what)
        {
            return $"The approval card cannot show {what} in full, and a change of whose rights an agent uses or a removal is only proposed with every such value on its card. Make the value shorter or make the change in smaller steps.";
        }

        public static string Broken(string rule)
        {
            return $"The approval card of this change cannot be built ({rule}), so it is not proposed. Make the values shorter or make the change in smaller steps.";
        }
    }

    /// <summary>
    /// One card while it is filled: its summary, notice and fields. The agent, what identifies the change and
    /// every value of whose rights it alters or what it removes are fields of their own, in full; a value
    /// that does not fit refuses the card. The other values the call gives follow, a field each while there
    /// is room and sharing fields otherwise, each shortened as far as the card needs to keep within the
    /// presentation's limits, also as stored JSON with its escapes, and the notice then says the technical
    /// details show them in full. A change of whose rights the agent uses and a removal set the notice that
    /// says so, unless four eyes already set one.
    /// </summary>
    private sealed class Card(Texts texts, string agentId)
    {
        private readonly List<NhAssistantPresentationField> _full = [];

        private readonly List<(string Label, string Value)> _others = [];

        private string? _refusal;

        public Texts Texts { get; } = texts;

        public string AgentId { get; } = agentId;

        public string Summary { get; set; } = string.Empty;

        public string? Notice { get; set; }

        /// <summary>Refuses the card, with the first reason given.</summary>
        public void Refuse(string reason)
        {
            _refusal ??= reason;
        }

        /// <summary>
        /// A field the card shows in full, such as the live agent of a promotion; <paramref name="what"/>
        /// names it in the refusal when it does not fit.
        /// </summary>
        public void AddField(string label, string? value, string what)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var printable = Printable(value);
            if (printable.Length > MaxValueLength)
            {
                Refuse(Refusals.TooLong(what.StartsWith("the ", StringComparison.Ordinal) ? what : $"'{what}'"));
                return;
            }
            _full.Add(new NhAssistantPresentationField(label, printable));
        }

        /// <summary>
        /// Every property the call gives but its agent, in the order of the tool's schema, with its value:
        /// those of <paramref name="inFull"/> in full in fields of their own, the others after them.
        /// </summary>
        public void AddGiven(NhAssistantAgentBuilderInput input, IReadOnlyList<string> properties, IReadOnlySet<string> inFull)
        {
            foreach (var name in properties.Where(name => name != "agentId" && input.Given(name)))
            {
                var label = Texts.LabelOf(name);
                var value = Texts.ValueOf(input, name);
                if (inFull.Contains(name))
                {
                    AddField(label, value, name);
                }
                else
                {
                    _others.Add((label, Printable(value)));
                }
            }
        }

        /// <summary>Says that the change alters whose rights the agent uses, and through which settings.</summary>
        public void Rights(IReadOnlyList<string> settings)
        {
            if (settings.Count > 0)
            {
                Notice ??= Texts.RightsNotice(AgentId, settings.Select(Texts.LabelOf).Distinct(StringComparer.Ordinal).ToArray());
            }
        }

        /// <summary>Says that the change removes something, and what.</summary>
        public void Removal(string what)
        {
            Notice = Texts.RemovalNotice(what, AgentId);
        }

        public (NhAssistantApprovalPresentation? Card, string? Refusal) ToPresentation(string toolName)
        {
            var summary = Printable(Summary);
            if (summary.Length > NhAssistantPresentationLimits.MaxSummaryLength)
            {
                Refuse(Refusals.Summary);
            }
            var fields = new List<NhAssistantPresentationField> { new(Texts.Agent, Printable(AgentId)) };
            if (fields[0].Value.Length > MaxValueLength)
            {
                Refuse(Refusals.Summary);
            }
            fields.AddRange(_full);
            if (fields.Count > NhAssistantPresentationLimits.MaxFields)
            {
                Refuse(Refusals.TooMany);
            }
            if (_refusal is not null)
            {
                return (null, _refusal);
            }

            var (others, cut) = Pack(NhAssistantPresentationLimits.MaxFields - fields.Count);
            var notice = Printable(Notice ?? Texts.ApprovalNotice);
            while (true)
            {
                var shown = cut ? notice + " " + Texts.CutNotice : notice;
                if (shown.Length > NhAssistantPresentationLimits.MaxNoticeLength)
                {
                    return (null, Refusals.Notice);
                }

                var presentation = new NhAssistantApprovalPresentation(toolName, summary, [.. fields, .. others], shown);
                var broken = NhAssistantPresentationLimits.FindBrokenRule(presentation);
                if (broken is null)
                {
                    return (presentation, null);
                }
                if (broken != NhAssistantPresentationRules.TooLong)
                {
                    return (null, Refusals.Broken(broken));
                }
                if (!cut)
                {
                    // Shortening a value adds the note that the technical details show it in full.
                    cut = true;
                    continue;
                }

                // Too long as stored JSON, escapes included: the longest other value gets shorter.
                var index = -1;
                var longest = 0;
                for (var position = 0; position < others.Count; position++)
                {
                    var length = EscapedLength(others[position].Value);
                    if (others[position].Value.Length > MinShownLength && length > longest)
                    {
                        (index, longest) = (position, length);
                    }
                }
                if (index < 0)
                {
                    return (null, Refusals.Size);
                }

                var value = others[index].Value;
                var overflow = JsonSerializer.Serialize(presentation, NhAssistantContent.JsonOptions).Length - NhAssistantPresentationLimits.MaxSerializedLength;
                var perCharacter = (double)longest / value.Length;
                var keep = Math.Max(MinShownLength, value.Length - (int)Math.Ceiling(overflow / perCharacter) - 1);
                others[index] = others[index] with { Value = Fit(value, keep) };
            }
        }

        /// <summary>
        /// The other values in at most <paramref name="room"/> fields: a field each while they fit, otherwise
        /// as "label: value" sharing fields, each value shortened as far as they need to fit; and whether a
        /// value was shortened or left out.
        /// </summary>
        private (List<NhAssistantPresentationField> Fields, bool Cut) Pack(int room)
        {
            if (_others.Count == 0)
            {
                return ([], false);
            }
            if (room <= 0)
            {
                return ([], true);
            }
            if (_others.Count <= room)
            {
                return (
                    _others.Select(other => new NhAssistantPresentationField(other.Label, Fit(other.Value, MaxValueLength))).ToList(),
                    _others.Any(other => other.Value.Length > MaxValueLength));
            }

            List<string> groups = [];
            var shortened = false;
            foreach (var budget in new[] { MaxValueLength, 128, 64, 32, MinShownLength })
            {
                (groups, shortened) = Group(budget);
                if (groups.Count <= room)
                {
                    return (groups.Select(group => new NhAssistantPresentationField(Texts.Changes, group)).ToList(), shortened);
                }
            }

            // Even shortened as far as they go, not every value has room: those that do are shown.
            return (groups.Take(room).Select(group => new NhAssistantPresentationField(Texts.Changes, group)).ToList(), true);
        }

        private (List<string> Groups, bool Shortened) Group(int budget)
        {
            var groups = new List<string>();
            var current = string.Empty;
            var shortened = false;
            foreach (var (label, value) in _others)
            {
                var space = Math.Max(MinShownLength, Math.Min(budget, MaxValueLength - label.Length - 2));
                shortened |= value.Length > space;
                var item = Fit(label + ": " + Fit(value, space), MaxValueLength);
                if (current.Length > 0 && current.Length + 2 + item.Length > MaxValueLength)
                {
                    groups.Add(current);
                    current = string.Empty;
                }

                current = current.Length == 0 ? item : current + "; " + item;
            }
            groups.Add(current);
            return (groups, shortened);
        }
    }
    /// <summary>
    /// The labels and phrases of the cards in one language.
    /// </summary>
    private sealed class Texts(bool dutch)
    {
        private static readonly Dictionary<string, (string English, string Dutch)> Labels = new(StringComparer.Ordinal)
        {
            ["expectedVersion"] = ("Version", "Versie"),
            ["displayName"] = ("Name", "Naam"),
            ["description"] = ("Description", "Omschrijving"),
            ["instructions"] = ("Instructions", "Instructies"),
            ["toolSelectors"] = ("Tools", "Tools"),
            ["autonomy"] = ("Autonomy", "Autonomie"),
            ["worksOnSubjectTypes"] = ("Works on object types", "Werkt aan objecttypen"),
            ["worksOnAllSubjects"] = ("Works on every object type", "Werkt aan alle objecttypen"),
            ["worksOnApplication"] = ("Works on the whole application", "Werkt aan de hele applicatie"),
            ["perDivision"] = ("Per division", "Per divisie"),
            ["runPolicy"] = ("Who may run it for the whole application", "Wie hem voor de hele applicatie mag starten"),
            ["viewPolicy"] = ("Who sees its results for the whole application", "Wie zijn resultaten voor de hele applicatie ziet"),
            ["offeredInChat"] = ("Offered in the chat", "Aangeboden in de chat"),
            ["startsOff"] = ("Starts off", "Start uit"),
            ["startsInShadow"] = ("Starts in shadow", "Start in schaduw"),
            ["profile"] = ("Model profile", "Modelprofiel"),
            ["requiredPolicy"] = ("Required policy", "Vereist beleid"),
            ["mcpServerIds"] = ("MCP servers", "MCP-servers"),
            ["dataClassification"] = ("Classification", "Classificatie"),
            ["compositions"] = ("Compositions", "Composities"),
            ["scripts"] = ("Scripts", "Scripts"),
            ["metadata"] = ("Metadata", "Metadata"),
            ["shadowOf"] = ("Shadow variant of", "Schaduwvariant van"),
            ["accountability"] = ("Accountability", "Verantwoording"),
            ["serviceAccountActorId"] = ("Technical account", "Technisch account"),
            ["actorId"] = ("Technical account", "Technisch account"),
            ["delegateFromInitiator"] = ("Rights of whoever starts a run", "Rechten van wie de run start"),
            ["allowGatewayReads"] = ("Gateway reads", "Lezen via de gateway"),
            ["elevatedReads"] = ("Elevated reads", "Lezen met verhoogde rechten"),
            ["approvalPolicy"] = ("Approval policy", "Goedkeuringsbeleid"),
            ["retryCount"] = ("Retries", "Herhalingen"),
            ["dailyToolCalls"] = ("Daily tool calls", "Toolaanroepen per dag"),
            ["dailyTokens"] = ("Daily tokens", "Tokens per dag"),
            ["maxConcurrentRuns"] = ("Runs at the same time", "Gelijktijdige runs"),
            ["maxRunsPerSubjectPerDay"] = ("Runs per subject per day", "Runs per onderwerp per dag"),
            ["maxTokensPerRun"] = ("Tokens per run", "Tokens per run"),
            ["triageProfile"] = ("Triage profile", "Triageprofiel"),
            ["ruleHandler"] = ("Rule handler", "Regelhandler"),
            ["alertKinds"] = ("Alert kinds", "Soorten waarschuwingen"),
            ["acceptsFiles"] = ("Accepts files", "Accepteert bestanden"),
            ["maxFileBytes"] = ("Largest file in bytes", "Grootste bestand in bytes"),
            ["maxFilePages"] = ("Pages per file", "Pagina's per bestand"),
            ["maxFilesPerTurn"] = ("Files per turn", "Bestanden per beurt"),
            ["maxFileTokensPerTurn"] = ("File tokens per turn", "Bestandstokens per beurt"),
            ["skillId"] = ("Skill", "Vaardigheid"),
            ["tools"] = ("Tools", "Tools"),
            ["apply"] = ("Applies", "Toepassen"),
            ["triggerId"] = ("Trigger", "Trigger"),
            ["source"] = ("Source", "Bron"),
            ["skill"] = ("Skill", "Vaardigheid"),
            ["event"] = ("Event", "Gebeurtenis"),
            ["filters"] = ("Filters", "Filters"),
            ["cron"] = ("Schedule", "Planning"),
            ["timeZone"] = ("Time zone", "Tijdzone"),
            ["subjectSet"] = ("Subject set", "Onderwerpset"),
            ["message"] = ("Message", "Bericht"),
            ["conflict"] = ("While a run is active", "Terwijl een run loopt"),
            ["debounceSeconds"] = ("Coalesces events for seconds", "Bundelt gebeurtenissen seconden"),
            ["script"] = ("Script", "Script"),
            ["writes"] = ("Writes", "Schrijfacties"),
            ["expectedRevision"] = ("Revision", "Revisie"),
            ["live"] = ("Straight live", "Direct live"),
            ["shadow"] = ("In shadow", "In schaduw"),
            ["subjectType"] = ("Object type", "Objecttype"),
            ["minimumRole"] = ("Role of who may ask", "Rol van wie mag vragen"),
            ["askerPolicy"] = ("Policy of who may ask", "Beleid van wie mag vragen"),
            ["routes"] = ("Routes", "Routes"),
            ["proposalRole"] = ("Role to propose", "Rol om voor te stellen"),
            ["proposalPolicy"] = ("Approval policy of proposals", "Goedkeuringsbeleid van voorstellen"),
            ["maxToolCalls"] = ("Tool calls", "Toolaanroepen"),
            ["timeoutSeconds"] = ("Time limit in seconds", "Tijdslimiet in seconden"),
            ["maxAnswerChars"] = ("Answer length", "Lengte van het antwoord"),
            ["maxHistoryMessages"] = ("History messages", "Berichten uit de geschiedenis"),
            ["allowRequests"] = ("Takes requests", "Neemt verzoeken aan"),
            ["requestsOnly"] = ("Requests only", "Alleen verzoeken"),
            ["exposeToMcp"] = ("Visible to MCP clients", "Zichtbaar voor MCP-clients"),
            ["sources"] = ("Sources", "Bronnen"),
            ["notifySource"] = ("Notifies the source", "Laat de bron weten"),
            ["maxPerSubjectPerDay"] = ("Per subject per day", "Per onderwerp per dag"),
            ["name"] = ("Entry", "Veld"),
            ["value"] = ("Value", "Waarde"),
            ["part"] = ("Part", "Onderdeel"),
            ["version"] = ("Version", "Versie"),
            ["state"] = ("State", "Status"),
            ["askerDecides"] = ("Asker decides", "Vrager beslist"),
            ["subjectAgentRights"] = ("Approvers decide, agent's rights", "Goedkeurders beslissen, rechten van de agent"),
            ["subjectAskerRights"] = ("Approvers decide, asker's rights", "Goedkeurders beslissen, rechten van de vrager"),
            ["serverId"] = ("MCP server", "MCP-server"),
            ["assigned"] = ("Assigned", "Toegewezen"),
            ["headlessUse"] = ("Without a person", "Zonder persoon"),
            ["toolId"] = ("Tool", "Tool"),
            ["kindId"] = ("Alert kind", "Soort waarschuwing"),
            ["mode"] = ("Mode", "Stand"),
            ["groups"] = ("Groups", "Groepen"),
            ["expectedVariantVersion"] = ("Variant's version", "Versie van de variant"),
            ["moveSkills"] = ("Moves its skills", "Neemt de vaardigheden mee"),
            ["asPackageVersion"] = ("As the package's next version", "Als volgende versie van het pakket"),
            ["uncarriedParts"] = ("Linked parts it does not carry", "Gekoppelde onderdelen die het niet meeneemt"),
            ["reason"] = ("Reason", "Reden")
        };

        public bool Dutch => dutch;
        public string Agent => "Agent";
        public string Changes => dutch ? "Wijzigt" : "Changes";
        public string LiveAgent => "Live agent";
        public string TakesOver => dutch ? "Neemt over" : "Takes over";
        public string TakesOverTools => dutch ? "Neemt de tools over" : "Takes over the tools";
        public string Cleared => dutch ? "gewist (de standaard geldt)" : "cleared (the default applies)";
        public string NoTechnicalAccount => dutch ? "geen (een eerder ingesteld technisch account vervalt)" : "none (a technical account set before is removed)";
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

        public string ApprovalNotice => dutch
            ? "De agentbouwer werkt met jouw eigen rechten. Er verandert niets tot je dit goedkeurt."
            : "The agent builder works with your own rights. Nothing changes until you approve this.";

        public string CutNotice => dutch
            ? "Niet elke waarde past op de kaart: de technische details tonen ze volledig."
            : "Not every value fits the card: the technical details show them in full.";

        public string FourEyesSave => dutch
            ? "De code vraagt om vier ogen: niemand slaat een regelscript direct live op. Sla het op als kandidaat."
            : "The code requires four eyes: nobody saves a rule script straight live. Save it as a candidate.";

        public string FourEyesLive => dutch
            ? "De code vraagt om vier ogen: wie een versie opsloeg, ook via de agentbouwer, zet die niet zelf live."
            : "The code requires four eyes: whoever saved a version, also through the agent builder, does not set it live.";

        public string LabelOf(string name)
        {
            return Labels.TryGetValue(name, out var label) ? (dutch ? label.Dutch : label.English) : name;
        }

        /// <summary>
        /// The value of a given property as the handler reads it: a text, a number, yes or no, a list, the
        /// length of instructions or a script, or that <c>null</c> clears it.
        /// </summary>
        public string ValueOf(NhAssistantAgentBuilderInput input, string name)
        {
            var element = input.Element(name)!.Value;
            switch (element.ValueKind)
            {
                case JsonValueKind.Null:
                    return Cleared;
                case JsonValueKind.True:
                    return dutch ? "ja" : "yes";
                case JsonValueKind.False:
                    return dutch ? "nee" : "no";
                case JsonValueKind.Number:
                    return element.GetRawText();
                case JsonValueKind.Array when element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String):
                    var items = input.Texts(name)!;
                    return items.Count == 0 ? (dutch ? "geen" : "none") : string.Join(", ", items);
                case JsonValueKind.String when name is "instructions":
                    return Characters(input.RawText(name)!.Length);
                case JsonValueKind.String when name is "script":
                    var script = input.RawText(name)!;
                    return ScriptSize(script.Split('\n').Length, script.Length);
                case JsonValueKind.String:
                    // The handler reads a blank text as null, which the check refuses; the card never shows blank.
                    return input.Text(name) ?? Cleared;
                default:
                    return element.GetRawText();
            }
        }

        public string RightsNotice(string agentId, IReadOnlyList<string> settings)
        {
            var what = string.Join(", ", settings.Distinct(StringComparer.Ordinal));
            return dutch
                ? $"Let op: dit verandert met wiens rechten agent {agentId} werkt ({what}). Zijn runs lezen en handelen daarna met die rechten. Er verandert niets tot je dit goedkeurt."
                : $"Note: this changes whose rights agent {agentId} works with ({what}). Its runs then read and act with those rights. Nothing changes until you approve this.";
        }

        public string RemovalNotice(string what, string agentId)
        {
            return dutch
                ? $"Let op: dit verwijdert {what} (agent {agentId}). In de chat is dat niet ongedaan te maken; versies blijven als geschiedenis waar het beheer die bewaart. Er verandert niets tot je dit goedkeurt."
                : $"Note: this removes {what} (agent {agentId}). It cannot be undone in the chat; versions stay as history where the administration keeps them. Nothing changes until you approve this.";
        }

        public string PromotionSummary(string variantId, string liveId, bool moveSkills, bool asPackageVersion)
        {
            var skills = moveSkills ? (dutch ? ", met haar vaardigheden" : ", with its skills") : string.Empty;
            var package = asPackageVersion ? (dutch ? ", als volgende versie van haar pakket" : ", as its package's next version") : string.Empty;
            return dutch
                ? $"Neemt de definitie van schaduwvariant {variantId} over op live agent {liveId} als diens volgende versie{skills}{package}, en verwijdert daarna de variant."
                : $"Copies the definition of shadow variant {variantId} onto live agent {liveId} as its next version{skills}{package}, then deletes the variant.";
        }

        /// <summary>
        /// What the live agent takes over from the variant, as the promotion copies it: the settings of
        /// whose rights first (required policy, who may run it and see its results, autonomy, what it works
        /// on, model profile, classification, compositions and scripts; only the required policy and the
        /// autonomy for an agent in code), then its tools.
        /// </summary>
        public (string Settings, string Tools) TakesOverOf(NhAssistantAgentInEffectDto variant, NhAssistantAgent? definition, bool toAgentInAdministration)
        {
            var none = dutch ? "geen" : "none";
            var parts = new List<string>
            {
                (dutch ? "vereist beleid: " : "required policy: ") + (variant.RequiredPolicy ?? none)
            };
            if (toAgentInAdministration && definition?.WorksOn is { Declared: true } worksOn)
            {
                parts.Add((dutch ? "wie hem start: " : "run policy: ") + (worksOn.RunPolicy ?? none));
                parts.Add((dutch ? "wie resultaten ziet: " : "view policy: ") + (worksOn.ViewPolicy ?? none));
            }
            parts.Add((dutch ? "autonomie: " : "autonomy: ") + variant.Autonomy.ToString().ToLowerInvariant());
            if (toAgentInAdministration)
            {
                if (definition?.WorksOn is { Declared: true } on)
                {
                    var subjects = new List<string>(on.SubjectTypes);
                    if (on.AllSubjects)
                    {
                        subjects.Add(dutch ? "alle objecttypen" : "every object type");
                    }
                    if (on.Application)
                    {
                        subjects.Add(dutch ? "de hele applicatie" : "the whole application");
                    }
                    parts.Add((dutch ? "werkt aan: " : "works on: ") + (subjects.Count == 0 ? none : string.Join(", ", subjects)));
                }
                parts.Add((dutch ? "modelprofiel: " : "model profile: ") + variant.Profile);
                parts.Add((dutch ? "classificatie: " : "classification: ")
                    + (variant.DataClassification is { } classification ? NhAssistantDataClassificationNames.NameOf(classification) : none));
                var compositions = definition?.Compositions ?? NhAssistantAgentCompositionSettings.None;
                parts.Add((dutch ? "composities: " : "compositions: ") + (compositions.Compositions ? (dutch ? "ja" : "yes") : (dutch ? "nee" : "no")));
                parts.Add("scripts: " + (compositions.Scripts ? (dutch ? "ja" : "yes") : (dutch ? "nee" : "no")));
            }

            return (string.Join("; ", parts), variant.ToolSelectors.Count == 0 ? none : string.Join(", ", variant.ToolSelectors));
        }

        public string PromotionNotice(string variantId, string liveId, bool toAgentInAdministration)
        {
            var what = toAgentInAdministration
                ? (dutch
                    ? "het vereiste beleid, wie hem start en zijn resultaten ziet, de autonomie, waar hij aan werkt, het modelprofiel, de classificatie, de composities en scripts en de tools"
                    : "the required policy, who may run it and see its results, the autonomy, what it works on, the model profile, the classification, the compositions and scripts and the tools")
                : (dutch ? "het vereiste beleid, de autonomie en de tools" : "the required policy, the autonomy and the tools");
            return dutch
                ? $"Let op: dit verandert met wiens rechten agent {liveId} werkt: hij neemt {what} over van schaduwvariant {variantId}, en de variant wordt verwijderd. Er verandert niets tot je dit goedkeurt."
                : $"Note: this changes whose rights agent {liveId} works with: it takes over {what} of shadow variant {variantId}, and the variant is deleted. Nothing changes until you approve this.";
        }
        public string HeadlessUseNotice(string serverId)
        {
            return dutch
                ? $"Let op: dit verandert met wiens verbinding de tools van MCP-server {serverId} werken in runs zonder persoon, voor elke agent waaraan de server is toegewezen. De verbinding, de geheimen en de andere instellingen blijven zoals ze zijn."
                : $"Note: this changes whose connection the tools of MCP server {serverId} use in runs without a person, for every agent the server is assigned to. Its connection, secrets and other settings stay as they are.";
        }

        public string WithoutApprovalNotice(string agentId, string toolId)
        {
            return dutch
                ? $"Let op: de eigen runs van agent {agentId} roepen {toolId} daarna aan zonder dat iemand het goedkeurt. Er verandert niets tot je dit goedkeurt."
                : $"Note: agent {agentId}'s own runs then call {toolId} without anyone's approval. Nothing changes until you approve this.";
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
            return $"trigger {triggerId}";
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
