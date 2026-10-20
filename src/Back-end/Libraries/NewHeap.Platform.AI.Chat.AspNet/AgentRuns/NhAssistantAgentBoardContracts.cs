using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// A group of agents as people see it, from <c>AddAgentGroup</c>.
/// </summary>
/// <param name="Id">The dash-case group id, such as <c>project-management</c>.</param>
/// <param name="Name">The group's name in the request's language or the fallback language.</param>
/// <param name="Description">What the group's agents do, when the application described it.</param>
public sealed record NhAssistantAgentGroupDto(string Id, string Name, string? Description);

/// <summary>
/// The groups one agent is in.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Groups">The groups the agent is in now, ordered by id: an administrator's choice per
/// group, otherwise the code.</param>
/// <param name="CodeGroups">The groups the agent joins in code with <c>InGroup</c>, ordered by id;
/// empty for an agent created in the administration.</param>
public sealed record NhAssistantAgentGroupMembershipDto(
    string AgentId,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> CodeGroups);

/// <summary>
/// The groups an administrator puts an agent in (<c>PUT admin/agents/{id}/groups</c>): every group
/// the agent should be in; the others it leaves.
/// </summary>
public sealed record NhAssistantAgentGroupsRequest(IReadOnlyList<string>? Groups);

/// <summary>
/// A group on <c>GET boards</c>: one the person sees at least one agent of.
/// </summary>
/// <param name="Id">The group id.</param>
/// <param name="Name">The group's name in the request's language.</param>
/// <param name="Description">What the group's agents do, when the application described it.</param>
/// <param name="AgentCount">How many of the group's agents the person sees on its board.</param>
public sealed record NhAssistantBoardSummaryDto(string Id, string Name, string? Description, int AgentCount);

/// <summary>
/// The groups the person may open a board of, ordered by name.
/// </summary>
public sealed record NhAssistantBoardListDto(IReadOnlyList<NhAssistantBoardSummaryDto> Items);

/// <summary>
/// The board of one group in one scope (<c>GET boards/{group}</c>).
/// </summary>
/// <param name="Id">The group id.</param>
/// <param name="Name">The group's name in the request's language.</param>
/// <param name="Description">What the group's agents do, when the application described it.</param>
/// <param name="ScopeKey">The board's scope: <c>global</c>, or <c>division:{id}</c> for a board of one
/// division.</param>
/// <param name="DivisionId">The division of a board of one division.</param>
/// <param name="LastVisitedAt">When the person last looked at this board in this scope, before this
/// read; <see langword="null"/> before the first visit.</param>
/// <param name="Agents">The group's agents the person sees, ordered by id.</param>
public sealed record NhAssistantBoardDto(
    string Id,
    string Name,
    string? Description,
    string ScopeKey,
    string? DivisionId,
    DateTimeOffset? LastVisitedAt,
    IReadOnlyList<NhAssistantBoardAgentDto> Agents);

/// <summary>
/// One agent on a board.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="DisplayNameKey">The agent's name: a translation key for an agent in code, the text
/// for one created in the administration.</param>
/// <param name="DescriptionKey">The agent's description, the same way.</param>
/// <param name="WorksOn">What the agent works on.</param>
/// <param name="SubjectType"><c>nh-system</c> when the agent works on the whole application in the
/// board's scope, otherwise <see langword="null"/>.</param>
/// <param name="SubjectId">The whole application in the board's scope: <c>global</c> for an app-wide
/// agent, <c>division-{id}</c> for a per-division agent on a board of a division.</param>
/// <param name="SubjectDisplayName">The name of that subject in the request's language.</param>
/// <param name="CanStart">Whether the person may run the agent for that subject now, by the rule of
/// manual starts (K9).</param>
/// <param name="ConversationId">The agent's thread of that subject, when the person may read it.</param>
/// <param name="LatestRun">The agent's newest run for that subject, with the beginning of its
/// result.</param>
/// <param name="OpenProposals">The open proposals of that thread and those its runs placed at
/// objects, counted only: the inbox shows those the person may decide.</param>
/// <param name="IsNew">Whether the newest run ended, or an open proposal was made, after the person's
/// last visit of the board in this scope; before the first visit everything shown is new.</param>
public sealed record NhAssistantBoardAgentDto(
    string AgentId,
    string DisplayNameKey,
    string DescriptionKey,
    NhAssistantBoardWorksOnDto WorksOn,
    string? SubjectType,
    string? SubjectId,
    string? SubjectDisplayName,
    bool CanStart,
    Guid? ConversationId,
    NhAssistantLatestRunDto? LatestRun,
    int OpenProposals,
    bool IsNew);

/// <summary>
/// What an agent on a board works on (W2), with the kinds of objects named for people.
/// </summary>
/// <param name="AllObjects">The agent works on every kind of object.</param>
/// <param name="Application">The agent works on the whole application.</param>
/// <param name="PerDivision">The agent works per division.</param>
/// <param name="SubjectTypes">The kinds of objects it names, ordered by id.</param>
public sealed record NhAssistantBoardWorksOnDto(
    bool AllObjects,
    bool Application,
    bool PerDivision,
    IReadOnlyList<NhAssistantBoardSubjectTypeDto> SubjectTypes);

/// <summary>
/// A kind of object as a board names it.
/// </summary>
/// <param name="Id">The subject type.</param>
/// <param name="Name">The name of one object in the request's language; the id without a name.</param>
/// <param name="PluralName">The name of several objects, when the application gave one.</param>
public sealed record NhAssistantBoardSubjectTypeDto(string Id, string Name, string? PluralName);

/// <summary>
/// A recorded visit of a board (<c>POST boards/{group}/visit</c>).
/// </summary>
public sealed record NhAssistantBoardVisitDto(string GroupId, string ScopeKey, DateTimeOffset VisitedAt);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NhAssistantAgentGroupMembershipDto))]
[JsonSerializable(typeof(NhAssistantAgentGroupsRequest))]
[JsonSerializable(typeof(NhAssistantBoardListDto))]
[JsonSerializable(typeof(NhAssistantBoardDto))]
[JsonSerializable(typeof(NhAssistantBoardVisitDto))]
public sealed partial class NhAssistantAgentBoardJsonSerializerContext : JsonSerializerContext;
