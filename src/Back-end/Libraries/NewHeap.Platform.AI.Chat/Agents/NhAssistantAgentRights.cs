using System.Text.Json;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// What a change of an agent's rights is, for <see cref="NhAssistantBuilder.UseAgentRightsPolicy"/>: whose rights
/// an agent uses, who reaches it, who decides its proposals and what runs without a person's approval. Every
/// save path compares what it would store with what is stored through this class, so saving a value again
/// changes nothing and needs no permission. A new part compares with what it would get without these settings,
/// so a person without the policy still creates an agent with plain run settings. What only narrows, such as
/// removing an MCP server or switching a tool's run without approval off, is no change here: anybody with the
/// administration policy can always take rights away.
/// </summary>
internal static class NhAssistantAgentRights
{
    /// <summary>The name of a change of the routes of an agent's consultations.</summary>
    public const string ConsultationRoutes = "consultationRoutes";

    /// <summary>The name of a change of the technical account an agent answers to.</summary>
    public const string ServiceAccount = "serviceAccountActorId";

    /// <summary>The name of an agent's MCP servers, of which adding one is a change.</summary>
    public const string McpServerIds = "mcpServerIds";

    /// <summary>The name of a switch that lets an agent run a tool without approval, going live.</summary>
    public const string WithoutApprovalMode = "mode";

    /// <summary>The name of a change of an MCP server's settings or connection.</summary>
    public const string McpServer = "mcpServer";

    /// <summary>The name of enabling or reclassifying a tool of an MCP server.</summary>
    public const string McpTool = "mcpTool";

    /// <summary>The name of a change of whether an MCP server's tools are used without a person.</summary>
    public const string HeadlessUse = "headlessUse";

    private const string ToolSelectors = "toolSelectors";

    private const string Autonomy = "autonomy";

    /// <summary>The parts of an agent's definition that decide who reaches it, what it may use and who sees its results.</summary>
    public static readonly IReadOnlyList<string> DefinitionFields = ["requiredPolicy", McpServerIds, "runPolicy", "viewPolicy"];

    /// <summary>The run settings that decide whose rights an agent's runs use and who decides their proposals.</summary>
    public static readonly IReadOnlyList<string> RunSettingsFields =
        ["accountability", ServiceAccount, "delegateFromInitiator", "allowGatewayReads", "elevatedReads", "approvalPolicy"];

    /// <summary>What decides who may consult an agent, who reaches the consultation and who decides its proposals.</summary>
    public static readonly IReadOnlyList<string> ConsultationFields =
        ["routes", "askerPolicy", "minimumRole", "proposalRole", "proposalPolicy", "exposeToMcp"];

    /// <summary>The routes that decide who decides a consultation's proposal and with whose rights it executes.</summary>
    public static readonly IReadOnlyList<string> RouteFields = ["askerDecides", "subjectAgentRights", "subjectAskerRights"];

    /// <summary>The setting of a skill that decides who decides its proposals.</summary>
    public static readonly IReadOnlyList<string> SkillFields = ["approvalPolicy"];

    /// <summary>
    /// The changes of an agent's definition. A new agent may name a required policy, which only narrows who
    /// reaches it; its run and view policies and its MCP servers count. On an agent that exists every change of
    /// a policy counts, and so does an added MCP server. When <paramref name="lendsRights"/> says its runs use
    /// another account's rights, so do more tools and more autonomy: they put those rights to more use.
    /// </summary>
    /// <param name="mcpServersAfter">The MCP servers it would have; <see langword="null"/> keeps them.</param>
    public static IReadOnlyList<string> OfDefinition(
        AssistantAgent? before,
        AssistantAgent after,
        IReadOnlyCollection<string>? mcpServersAfter,
        bool lendsRights)
    {
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<string>();
        if (before is not null)
        {
            Compare(changes, "requiredPolicy", before.RequiredPolicy, after.RequiredPolicy);
        }

        Compare(changes, "runPolicy", before?.RunPolicy, after.RunPolicy);
        Compare(changes, "viewPolicy", before?.ViewPolicy, after.ViewPolicy);
        var mcpServersBefore = before?.McpServers.Select(server => server.McpServerId).ToArray() ?? [];
        if (mcpServersAfter is not null && mcpServersAfter.Except(mcpServersBefore, StringComparer.Ordinal).Any())
        {
            changes.Add(McpServerIds);
        }

        if (before is not null && lendsRights)
        {
            if (Widens(SelectorsOf(before.ToolSelectorsJson), SelectorsOf(after.ToolSelectorsJson)))
            {
                changes.Add(ToolSelectors);
            }
            if (after.Autonomy > before.Autonomy)
            {
                changes.Add(Autonomy);
            }
        }

        return changes;
    }

    /// <summary>
    /// The changes of an agent's tools and autonomy, for what an agent that lends rights would take over; a
    /// builder's card asks this before it is shown.
    /// </summary>
    public static IReadOnlyList<string> OfCapabilities(
        IReadOnlyList<string> selectorsBefore,
        IReadOnlyList<string> selectorsAfter,
        NhAiAutonomyLevel autonomyBefore,
        NhAiAutonomyLevel autonomyAfter)
    {
        var changes = new List<string>();
        if (Widens(selectorsBefore, selectorsAfter))
        {
            changes.Add(ToolSelectors);
        }
        if (autonomyAfter > autonomyBefore)
        {
            changes.Add(Autonomy);
        }

        return changes;
    }

    /// <summary>
    /// The changes of an agent's run settings; new settings compare with an agent that answers to itself, without
    /// anyone's rights to use and without an approval policy of its own.
    /// </summary>
    public static IReadOnlyList<string> OfRunSettings(AssistantAgentRunConfiguration? before, AssistantAgentRunConfiguration after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<string>();
        Compare(changes, "accountability", before?.Accountability ?? NhAssistantAccountabilities.Agent, after.Accountability);
        Compare(changes, ServiceAccount, before?.ServiceAccountActorId, after.ServiceAccountActorId);
        if ((before?.DelegateFromInitiator ?? false) != after.DelegateFromInitiator)
        {
            changes.Add("delegateFromInitiator");
        }
        if ((before?.AllowGatewayReads ?? false) != after.AllowGatewayReads)
        {
            changes.Add("allowGatewayReads");
        }

        Compare(changes, "elevatedReads", before?.ElevatedReads ?? NhAssistantElevatedReadNames.Restricted, after.ElevatedReads);
        Compare(changes, "approvalPolicy", before?.ApprovalPolicyId, after.ApprovalPolicyId);
        return changes;
    }

    /// <summary>The changes of a skill; a new skill compares with one that uses its agent's approval policy.</summary>
    public static IReadOnlyList<string> OfSkill(AssistantAgentSkillDefinition? before, AssistantAgentSkillDefinition after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<string>();
        Compare(changes, "approvalPolicy", before?.ApprovalPolicyId, after.ApprovalPolicyId);
        return changes;
    }

    /// <summary>
    /// The changes of a consultation; a new one compares with the defaults it gets without these settings: no
    /// routes, readers may ask, participants see proposals, the agent's approval policy and no MCP clients.
    /// </summary>
    public static IReadOnlyList<string> OfConsultation(AssistantAgentConsultationDefinition? before, AssistantAgentConsultationDefinition after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<string>();
        Compare(changes, "routes", before?.RoutesJson ?? NhAssistantDataConsultationRoutes.Write([]), after.RoutesJson);
        Compare(changes, "askerPolicy", before?.AskerPolicy, after.AskerPolicy);
        Compare(changes, "minimumRole", before?.MinimumRole ?? NhAssistantSubjectRoleNames.Reader, after.MinimumRole);
        Compare(changes, "proposalRole", before?.ProposalRole ?? NhAssistantSubjectRoleNames.Participant, after.ProposalRole);
        Compare(changes, "proposalPolicy", before?.ProposalPolicyId, after.ProposalPolicyId);
        if ((before?.ExposeToMcp ?? false) != after.ExposeToMcp)
        {
            changes.Add("exposeToMcp");
        }

        return changes;
    }

    /// <summary>
    /// The refusal of <paramref name="changes"/>: <c>assistant-agent-rights-forbidden</c> with a field error
    /// <c>rights-required</c> per change.
    /// </summary>
    public static TaskResult Refused(IEnumerable<string> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var errors = new NhAssistantValidationErrors();
        foreach (var change in changes)
        {
            errors.Add(change, NhAssistantFieldErrors.RightsRequired);
        }

        return errors.ToResult(
            NhAssistantErrorCodes.AgentRightsForbidden,
            "This person may not change whose rights an agent uses, who reaches it, who decides its proposals or what runs without approval.");
    }

    private static void Compare(List<string> changes, string field, string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add(field);
        }
    }

    /// <summary>
    /// Whether <paramref name="after"/> selects a tool that <paramref name="before"/> does not: a narrower
    /// selector under one it had is no widening.
    /// </summary>
    public static bool Widens(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        return after.Any(selector => !before.Any(existing => NhAssistantRegistrationState.Covers(existing, selector)));
    }

    /// <summary>The tool selectors of an agent as its row stores them.</summary>
    public static string[] SelectorsOf(string json)
    {
        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }
}

/// <summary>
/// Whether a person may give an agent that answers to a technical account more tools: they put the account's rights
/// to more use, so the rule of binding an account holds for the tools they add. The ASP.NET integration implements
/// it, where a person's rights are known; without it nothing is refused. It holds whether or not the application set
/// <see cref="NhAssistantBuilder.UseAgentRightsPolicy"/>.
/// </summary>
internal interface INhAssistantLentToolsCheck
{
    /// <summary>
    /// <see cref="NhAssistantErrorCodes.ServiceAccountNotPermitted"/> when <paramref name="accountActorId"/> passes a
    /// policy of a tool that <paramref name="selectorsAfter"/> adds which <paramref name="callerActorId"/> does not.
    /// Tools the agent already had are not compared, so a person can still add what they hold to an agent that
    /// someone with more rights set up. The account in code gets no exception here: its developers chose it for the
    /// tools in code, not for the ones an administrator adds. An account that may no longer sign in lends nothing;
    /// the person themself, a person who may act as any user and an actor the platform knows as no person may.
    /// </summary>
    Task<TaskResult> CheckAddedToolsAsync(
        string agentId,
        IReadOnlyList<string> selectorsBefore,
        IReadOnlyList<string> selectorsAfter,
        string accountActorId,
        string callerActorId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Whether a change of an agent's rights may go ahead for its actor, under
/// <see cref="NhAssistantBuilder.UseAgentRightsPolicy"/>. The ASP.NET integration implements it, where a person's
/// rights are known; without it nothing is refused.
/// </summary>
internal interface INhAssistantAgentRightsGate
{
    /// <summary>Whether the application set the policy; without it nothing is refused.</summary>
    bool Enabled { get; }

    /// <summary>
    /// The refusal of <paramref name="changes"/> by <paramref name="actorId"/>, or <see langword="null"/> when they
    /// may go ahead: there are none, the application set no policy, the person passes it, or the platform knows the
    /// actor as no person, which makes it application code and the application's own decision.
    /// </summary>
    /// <param name="objectId">The agent or MCP server the change is about.</param>
    ValueTask<TaskResult?> CheckAsync(
        string objectId,
        IReadOnlyCollection<string> changes,
        string actorId,
        CancellationToken cancellationToken);
}
