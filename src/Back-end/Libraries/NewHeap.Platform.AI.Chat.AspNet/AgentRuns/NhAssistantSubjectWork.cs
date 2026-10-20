using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The agent work on an application's own subjects, such as projects, for the application's back-end:
/// everything on one subject for a detail page and counts for many subjects for a list. It applies
/// the same access rules as the assistant endpoints: the subject access resolver decides live whether
/// the caller sees a subject, the approval policies decide live who may decide a proposal, and list
/// counts follow the member snapshot like the approval inbox.
/// </summary>
public interface INhAssistantSubjectWork
{
    /// <summary>
    /// The agents with a thread on the subject, and <paramref name="agentId"/> when given, with their
    /// open proposals and the caller's decision right on each. Fails with
    /// <c>assistant-subject-not-found</c> when the subject is unknown or the caller has no role on it.
    /// </summary>
    Task<TaskResult<NhAssistantSubjectWorkDto>> GetAsync(
        NhAiSubjectRef subject,
        string actorId,
        ClaimsPrincipal user,
        string? agentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Open proposals, the proposals the caller may decide and active runs for at most 200 subjects
    /// of one type, in one query.
    /// </summary>
    Task<TaskResult<NhAssistantSubjectSummaryListDto>> SummarizeAsync(
        string subjectType,
        IReadOnlyCollection<string> subjectIds,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the agent work on a subject for the caller of the current request.
/// </summary>
public static class NhAssistantSubjectWorkExtensions
{
    public static async Task<TaskResult<NhAssistantSubjectWorkDto>> GetForRequestAsync(
        this INhAssistantSubjectWork work,
        HttpContext httpContext,
        NhAiSubjectRef subject,
        string? agentId = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(httpContext);
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return TaskResult<NhAssistantSubjectWorkDto>.Failed(NhAssistantErrorCodes.ContextUnavailable, "The caller is not available.");
        }

        return await work.GetAsync(subject, caller.Data!.ActorId, httpContext.User, agentId, httpContext.RequestAborted);
    }

    public static async Task<TaskResult<NhAssistantSubjectSummaryListDto>> SummarizeForRequestAsync(
        this INhAssistantSubjectWork work,
        HttpContext httpContext,
        string subjectType,
        IReadOnlyCollection<string> subjectIds)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(httpContext);
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return TaskResult<NhAssistantSubjectSummaryListDto>.Failed(NhAssistantErrorCodes.ContextUnavailable, "The caller is not available.");
        }

        return await work.SummarizeAsync(subjectType, subjectIds, caller.Data!.ActorId, httpContext.User, httpContext.RequestAborted);
    }
}

internal sealed class NhAssistantSubjectWork(
    NhAssistantRegistrationState registration,
    NhAssistantSubjectAccessService subjects,
    NhAssistantRunStore runs,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentAccess agentAccess,
    NhAssistantApproverCheck approvers,
    NhAssistantManualStarts starts,
    NhAssistantSystemApprovers systemApprovers) : INhAssistantSubjectWork
{
    public const int MaxSummarySubjects = 200;
    private const int MaxProposalsPerThread = 50;

    public async Task<TaskResult<NhAssistantSubjectWorkDto>> GetAsync(
        NhAiSubjectRef subject,
        string actorId,
        ClaimsPrincipal user,
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(user);
        if (!subjects.IsKnownType(subject.Type))
        {
            return TaskResult<NhAssistantSubjectWorkDto>.Failed(NhAssistantErrorCodes.SubjectTypeUnknown, "The subject type is not registered.");
        }

        var info = await subjects.DescribeAsync(subject, cancellationToken);
        var access = info is null ? NhAiSubjectAccess.None : await subjects.ResolveAsync(subject, actorId, cancellationToken);
        if (info is null || access.Role == NhAiSubjectRole.None)
        {
            return TaskResult<NhAssistantSubjectWorkDto>.Failed(NhAssistantErrorCodes.SubjectNotFound, "The subject was not found.");
        }

        var administrator = await agentAccess.CanAdministerAsync(user);
        // Shadow threads, and every thread of a shadow variant, are for administrators only.
        var visible = (await runs.FindThreadsBySubjectAsync(subject, cancellationToken))
            .Where(thread => string.Equals(thread.ScopeKey, info.ScopeKey, StringComparison.Ordinal)
                && registration.AgentRuns.ContainsKey(thread.AgentId)
                && IsVisible(thread.AgentId, administrator)
                && (administrator || !thread.Shadow))
            .ToArray();
        var threads = visible
            .Where(thread => !thread.Shadow || IsVariant(thread.AgentId))
            .ToDictionary(thread => thread.AgentId, StringComparer.Ordinal);
        var shadowThreads = visible
            .Where(thread => thread.Shadow && !IsVariant(thread.AgentId))
            .ToDictionary(thread => thread.AgentId, StringComparer.Ordinal);
        var agentIds = threads.Keys.Union(shadowThreads.Keys, StringComparer.Ordinal).ToList();
        if (agentId is not null
            && registration.AgentRuns.ContainsKey(agentId)
            && IsVisible(agentId, administrator)
            && !agentIds.Contains(agentId, StringComparer.Ordinal))
        {
            agentIds.Add(agentId);
        }

        var agentEntries = new List<NhAssistantSubjectAgentDto>(agentIds.Count);
        var proposals = new List<NhAssistantSubjectProposalDto>();
        foreach (var id in agentIds.Order(StringComparer.Ordinal))
        {
            // The whole application shows an agent's thread only to people who pass its view policy.
            if (!await subjects.MayReadAgentThreadAsync(subject, id, actorId, cancellationToken))
            {
                continue;
            }

            var thread = threads.GetValueOrDefault(id);
            var canStart = await CanStartAsync(id, subject, access, actorId, user, cancellationToken);
            var shadowThread = shadowThreads.GetValueOrDefault(id);
            agentEntries.Add(new NhAssistantSubjectAgentDto(
                id,
                thread is null ? null : await NhAssistantAgentRunEndpoints.ToThreadDtoAsync(thread, access.Role, runs, subjects, cancellationToken),
                canStart)
            {
                ShadowThread = shadowThread is null ? null : await NhAssistantAgentRunEndpoints.ToThreadDtoAsync(shadowThread, access.Role, runs, subjects, cancellationToken)
            });
            if (thread is not null)
            {
                proposals.AddRange(await OpenProposalsAsync(thread, actorId, user, cancellationToken));
            }
        }

        var (typeName, link) = NhAssistantAgentRunDtoMapper.SubjectOf(subjects, subject.Type, subject.Id);
        return TaskResult<NhAssistantSubjectWorkDto>.Succeeded(new NhAssistantSubjectWorkDto(
            subject.Type,
            subject.Id,
            NhAssistantAgentRunDtoMapper.DisplayNameOf(subjects, subject.Type, subject.Id, info.DisplayName),
            NhAssistantAgentRunDtoMapper.RoleName(access.Role),
            agentEntries,
            proposals.OrderBy(item => item.Proposal.ExpiresAt).ToArray())
        {
            SubjectTypeName = typeName,
            SubjectLink = link
        });
    }

    public async Task<TaskResult<NhAssistantSubjectSummaryListDto>> SummarizeAsync(
        string subjectType,
        IReadOnlyCollection<string> subjectIds,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subjectIds);
        ArgumentNullException.ThrowIfNull(user);
        var ids = subjectIds.Distinct(StringComparer.Ordinal).ToArray();
        if (!NhAiSubjectRef.IsValidType(subjectType)
            || ids.Length is 0 or > MaxSummarySubjects
            || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > NhAiSubjectRef.MaxIdLength || id.Trim() != id))
        {
            return TaskResult<NhAssistantSubjectSummaryListDto>.Failed(NhAssistantErrorCodes.MessageInvalid, "Summarize between 1 and 200 valid subject ids.");
        }
        if (!subjects.IsKnownType(subjectType))
        {
            return TaskResult<NhAssistantSubjectSummaryListDto>.Failed(NhAssistantErrorCodes.SubjectTypeUnknown, "The subject type is not registered.");
        }

        var administrator = await agentAccess.CanAdministerAsync(user);
        var hidden = registration.AgentRuns
            .Where(entry => !IsVisible(entry.Key, administrator))
            .Select(entry => entry.Key)
            .ToArray();
        // The whole application lists no members: the caller's live roles stand in for the snapshot,
        // and only the threads of agents whose view policy the caller passes count.
        var system = string.Equals(subjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal);
        var liveRoles = system ? await systemApprovers.RolesAsync(ids, actorId, cancellationToken) : null;
        var visibleThreads = system ? await systemApprovers.VisibleThreadsAsync(ids, actorId, cancellationToken) : null;
        var rows = (await runs.SummarizeSubjectsAsync(
                actorId,
                subjectType,
                ids,
                hidden,
                cancellationToken,
                includeShadow: administrator,
                liveRoles: liveRoles,
                visibleConversationIds: visibleThreads))
            .ToDictionary(row => row.SubjectId, StringComparer.Ordinal);
        var items = subjectIds
            .Select(id => rows.TryGetValue(id, out var row)
                ? new NhAssistantSubjectSummaryDto(id, row.OpenProposals, row.ProposalsToDecide, row.RunActive)
                : new NhAssistantSubjectSummaryDto(id, 0, 0, false))
            .ToArray();
        return TaskResult<NhAssistantSubjectSummaryListDto>.Succeeded(new NhAssistantSubjectSummaryListDto(subjectType, items));
    }

    /// <summary>
    /// Shadow variants work for administrators only; nobody else learns that they ran.
    /// </summary>
    private bool IsVisible(string agentId, bool administrator)
    {
        return administrator || !IsVariant(agentId);
    }

    private bool IsVariant(string agentId)
    {
        return registration.AgentRuns.TryGetValue(agentId, out var options) && options.ShadowOfAgentId is not null;
    }

    /// <summary>
    /// Whether the caller may start the agent on the subject, by the one rule of manual starts (K9).
    /// </summary>
    private async Task<bool> CanStartAsync(
        string agentId,
        NhAiSubjectRef subject,
        NhAiSubjectAccess access,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(agentId, includeDisabled: false, cancellationToken);
        return await starts.CanStartAsync(agent, subject, access, actorId, user, cancellationToken);
    }

    private async Task<IReadOnlyList<NhAssistantSubjectProposalDto>> OpenProposalsAsync(
        AssistantThread thread,
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var approvals = await runs.GetOpenThreadApprovalsAsync(thread.ConversationId, MaxProposalsPerThread, cancellationToken);
        var proposals = await NhAssistantAgentRunDtoMapper.CompleteAsync(
            approvals.Select(approval => NhAssistantAgentRunDtoMapper.ToDto(approval, thread)).ToArray(),
            runs,
            subjects,
            cancellationToken);
        var items = new List<NhAssistantSubjectProposalDto>(approvals.Count);
        for (var index = 0; index < approvals.Count; index++)
        {
            var approval = approvals[index];
            var check = approval.Mode == NhAssistantApprovalModes.Inline && approval.RunId is null
                ? TaskResult.Failed(NhAssistantErrorCodes.ApprovalDecisionInvalid, "A person's own turn decides this proposal.")
                : await approvers.CheckAsync(approval, thread, actorId, user, cancellationToken);
            items.Add(new NhAssistantSubjectProposalDto(
                proposals[index],
                check.Success,
                check.Success ? null : check.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))));
        }

        return items;
    }
}
