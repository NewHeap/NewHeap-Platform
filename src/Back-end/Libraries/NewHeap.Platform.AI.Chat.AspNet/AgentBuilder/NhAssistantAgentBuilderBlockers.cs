using System.Text.RegularExpressions;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentBuilder;

/// <summary>
/// One thing that stops an agent from working as it should (FB95): a stable code, the setting it is about,
/// what it means in plain words and the tool that changes it, or <see langword="null"/> when only code
/// changes it.
/// </summary>
internal sealed record NhAssistantAgentBuilderBlocker(string Code, string Setting, string Message, string? Fix);

/// <summary>
/// The codes of <see cref="NhAssistantAgentBuilderBlocker"/>, which the builder's instructions explain.
/// </summary>
internal static class NhAssistantAgentBuilderBlockerCodes
{
    public const string NotConfigured = "not-configured";
    public const string RunSettingsOutOfEffect = "run-settings-out-of-effect";
    public const string ConfigurationProblem = "configuration-problem";
    public const string InstructionsUnavailable = "instructions-unavailable";
    public const string GatewayReadsOff = "gateway-reads-off";
    public const string GatewayReadsNeedAccountability = "gateway-reads-need-accountability";
    public const string TechnicalAccountMissing = "technical-account-missing";
    public const string AlertKindNotListed = "alert-kind-not-listed";
    public const string NotActive = "not-active";
    public const string AgentWorkOff = "agent-work-off";
    public const string NoTrigger = "no-trigger";
    public const string InShadow = "in-shadow";
}

/// <summary>
/// Finds the blockers of one agent from what the administration reads of it: its definition and state, its
/// run configuration, the gateway's tools and the alert kinds its rules may raise. It only reads.
/// </summary>
internal static partial class NhAssistantAgentBuilderBlockers
{
    public static IReadOnlyList<NhAssistantAgentBuilderBlocker> Of(
        NhAssistantAgentInEffectDto agent,
        NhAssistantAgentConfigurationDto? configuration,
        IReadOnlyCollection<string> gatewayToolIds,
        IReadOnlyCollection<string> listedAlertKinds,
        IReadOnlyList<string> ruleScripts)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var blockers = new List<NhAssistantAgentBuilderBlocker>();
        var fromAdministration = string.Equals(agent.Source, NhAssistantAgentSources.Admin, StringComparison.Ordinal);
        var runSettings = configuration?.RunSettings;
        var codeRun = configuration?.CodeRunSettings;
        var runs = !agent.ChatOnly && (runSettings is not null || codeRun is not null);

        if (agent.InstructionsProblem is { } instructionsProblem)
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.InstructionsUnavailable,
                "instructions",
                $"The instructions the agent takes from an application asset are not available ({instructionsProblem}), so it starts no model turn. Send its instructions as text.",
                NhAssistantAgentBuilderTools.UpdateAgent));
        }

        if (fromAdministration && !agent.ChatOnly && runSettings is null && agent.ShadowOf is null)
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.NotConfigured,
                "run-settings",
                "The agent has no run settings, so it only chats: no trigger, schedule or rule script starts it.",
                NhAssistantAgentBuilderTools.SaveRunSettings));
        }

        if (runSettings is { InEffect: false } outOfEffect)
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.RunSettingsOutOfEffect,
                "run-settings",
                $"The run settings no longer fit the code ({outOfEffect.Problem ?? "unknown"}), so they are out of effect.",
                NhAssistantAgentBuilderTools.SaveRunSettings));
        }

        foreach (var problem in configuration?.Problems ?? [])
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.ConfigurationProblem,
                problem.Kind + ":" + problem.Id,
                $"The {problem.Kind} {problem.Id} no longer fits the code ({problem.Code}) and is out of effect.",
                FixOf(problem.Kind)));
        }

        if (runs)
        {
            AddRights(blockers, agent, runSettings, codeRun, gatewayToolIds, fromAdministration);
            AddAlerts(blockers, listedAlertKinds, ruleScripts, fromAdministration);
            AddWork(blockers, agent, configuration!);
        }

        return blockers;
    }

    /// <summary>
    /// The alert kinds the scripts raise by name, from <c>alerts.raise({ kind: '...' })</c>.
    /// </summary>
    internal static IReadOnlyList<string> RaisedKindsOf(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return RaisedKind().Matches(script)
            .Select(match => match.Groups["kind"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddRights(
        List<NhAssistantAgentBuilderBlocker> blockers,
        NhAssistantAgentInEffectDto agent,
        NhAssistantAgentRunSettingsDto? runSettings,
        NhAssistantCodeRunSettingsDto? codeRun,
        IReadOnlyCollection<string> gatewayToolIds,
        bool fromAdministration)
    {
        var accountability = runSettings?.Accountability ?? codeRun?.Accountability ?? NhAssistantAccountabilities.Agent;
        var gatewayReads = runSettings?.AllowGatewayReads ?? codeRun?.AllowGatewayReads ?? false;
        var fix = fromAdministration ? NhAssistantAgentBuilderTools.SaveRunSettings : null;
        var usesGateway = gatewayToolIds.Any(toolId => agent.ToolSelectors.Any(selector => NhAssistantTurnRunner.IsSelected(selector, toolId)));
        if (usesGateway && !gatewayReads)
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.GatewayReadsOff,
                "allowGatewayReads",
                "The agent's tools read through the application's API gateway, but gateway reads are off: its runs without a person cannot read through the gateway.",
                fix));
        }

        if (usesGateway && string.Equals(accountability, NhAssistantAccountabilities.Agent, StringComparison.Ordinal))
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.GatewayReadsNeedAccountability,
                "accountability",
                "The agent answers to itself, so a run without a person has nobody whose rights a gateway read uses. Gateway reads need the subject's responsible person or a technical account (service-account) as its accountability.",
                fix));
        }

        var technicalAccount = runSettings is not null ? runSettings.ServiceAccountActorId : agent.State.ServiceAccount?.ActorId;
        if (string.Equals(accountability, NhAssistantAccountabilities.ServiceAccount, StringComparison.Ordinal) && string.IsNullOrEmpty(technicalAccount))
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.TechnicalAccountMissing,
                "serviceAccountActorId",
                "The agent answers to a technical account, but none is set.",
                fromAdministration ? NhAssistantAgentBuilderTools.SaveRunSettings : NhAssistantAgentBuilderTools.SetTechnicalAccount));
        }
    }

    private static void AddAlerts(
        List<NhAssistantAgentBuilderBlocker> blockers,
        IReadOnlyCollection<string> listedAlertKinds,
        IReadOnlyList<string> ruleScripts,
        bool fromAdministration)
    {
        var raises = ruleScripts.Any(script => script.Contains("alerts.raise", StringComparison.Ordinal));
        if (!raises)
        {
            return;
        }

        var raised = ruleScripts.SelectMany(RaisedKindsOf).Distinct(StringComparer.Ordinal).ToArray();
        var missing = raised.Where(kind => !listedAlertKinds.Contains(kind)).ToArray();
        if (listedAlertKinds.Count == 0 || missing.Length > 0)
        {
            var kinds = missing.Length > 0 ? string.Join(", ", missing) : "any kind";
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.AlertKindNotListed,
                "alertKinds",
                $"A rule script raises alerts ({kinds}) that the agent does not list, so every such alert is refused with assistant-alert-kind-not-listed.",
                fromAdministration ? NhAssistantAgentBuilderTools.SaveRunSettings : null));
        }
    }

    private static void AddWork(
        List<NhAssistantAgentBuilderBlocker> blockers,
        NhAssistantAgentInEffectDto agent,
        NhAssistantAgentConfigurationDto configuration)
    {
        if (!agent.State.AgentWork)
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.AgentWorkOff,
                "agent-work",
                "Agents do not work on this host (agent work is off in the application's configuration), so no run starts here.",
                null));
        }

        if (!string.Equals(agent.RunState, NhAssistantAgentRunStates.Active, StringComparison.Ordinal))
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.NotActive,
                "state",
                $"The agent is {agent.RunState}, so its triggers do not start runs as they would while it is active.",
                string.Equals(agent.RunState, NhAssistantAgentRunStates.ConfiguredOff, StringComparison.Ordinal) ? null : NhAssistantAgentBuilderTools.SetState));
        }

        if (configuration.CodeTriggers.Count == 0 && configuration.DataTriggers.Count == 0 && agent.ShadowOf is null)
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.NoTrigger,
                "triggers",
                "Nothing starts the agent on its own: it has no trigger or schedule. People can still start it by hand.",
                NhAssistantAgentBuilderTools.SaveTrigger));
        }

        if (agent.InShadow && agent.ShadowOf is null)
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.InShadow,
                "shadow",
                "The agent works in shadow: its runs record what they would do and have no effects, and nobody is notified.",
                NhAssistantAgentBuilderTools.SetShadow));
        }

        foreach (var trigger in configuration.DataTriggers.Where(trigger => trigger.Shadow && !agent.InShadow))
        {
            blockers.Add(new(
                NhAssistantAgentBuilderBlockerCodes.InShadow,
                "trigger:" + trigger.TriggerId,
                $"The trigger {trigger.TriggerId} works in shadow: the runs it starts have no effects.",
                NhAssistantAgentBuilderTools.SetShadow));
        }
    }

    private static string? FixOf(string kind)
    {
        return kind switch
        {
            "skill" => NhAssistantAgentBuilderTools.SaveSkill,
            "trigger" => NhAssistantAgentBuilderTools.SaveTrigger,
            "consultation" => NhAssistantAgentBuilderTools.SaveConsultation,
            "run-settings" => NhAssistantAgentBuilderTools.SaveRunSettings,
            _ => null
        };
    }

    [GeneratedRegex("""kind\s*:\s*['"](?<kind>[a-z0-9][a-z0-9.\-]{0,99})['"]""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex RaisedKind();
}
