using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// When a person last looked at the approval inbox, per scope: <c>global</c>, or the scope key of one
/// division (<c>division:{id}</c>) for an inbox filtered on that division, as boards keep their visits.
/// The inbox marks the proposals made after the last visit as new. The visits are stored with the
/// boards' visits under the platform's reserved id <see cref="NhAssistantAgentGroupDefinition.InboxVisitId"/>,
/// which no group may use; only the person, the scope and the time are stored, and the run maintenance
/// removes them by age as it removes board visits.
/// </summary>
internal sealed class NhAssistantInboxVisits(
    NhAssistantBoardStore store,
    NhAssistantSubjectAccessService subjects,
    NhAssistantRegistrationState registration,
    IServiceProvider services)
{
    /// <summary>
    /// The scope of an inbox: the division's scope key when it is filtered on a division, otherwise
    /// <c>global</c>.
    /// </summary>
    public static string ScopeKeyOf(Guid? divisionId)
    {
        return NhAiSubjectInfo.CreateScopeKey(null, divisionId);
    }

    /// <summary>
    /// When the person last looked at the inbox in the scope; <see langword="null"/> before the first
    /// visit.
    /// </summary>
    public Task<DateTimeOffset?> LastVisitAsync(string actorId, string scopeKey, CancellationToken cancellationToken)
    {
        return store.FindVisitAsync(actorId, NhAssistantAgentGroupDefinition.InboxVisitId, scopeKey, cancellationToken);
    }

    /// <summary>
    /// Whether a proposal made at <paramref name="createdAt"/> is new after <paramref name="lastVisit"/>:
    /// before the first visit every proposal is.
    /// </summary>
    public static bool IsNew(DateTimeOffset createdAt, DateTimeOffset? lastVisit)
    {
        return lastVisit is null || createdAt > lastVisit;
    }

    /// <summary>
    /// Records that the person looked at the inbox of the scope now. A division must be one the
    /// application lists and the person may work in, as for boards; a person bound to a tenant has no
    /// division scope, unless the single-tenant mode names that tenant. Fails with <see cref="NhAssistantErrorCodes.SubjectNotFound"/> otherwise, so nobody
    /// stores visits of a division they cannot work in.
    /// </summary>
    public async Task<TaskResult<NhAssistantInboxVisitDto>> VisitAsync(
        string actorId,
        string? tenantId,
        Guid? divisionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (divisionId is { } id && !await MayWorkInAsync(actorId, tenantId, id, cancellationToken))
        {
            return TaskResult<NhAssistantInboxVisitDto>.Failed(NhAssistantErrorCodes.SubjectNotFound, "The division was not found.");
        }

        var scopeKey = ScopeKeyOf(divisionId);
        // Whole microseconds, which both providers store exactly.
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        await store.RecordVisitAsync(actorId, NhAssistantAgentGroupDefinition.InboxVisitId, scopeKey, now, cancellationToken);
        return TaskResult<NhAssistantInboxVisitDto>.Succeeded(new NhAssistantInboxVisitDto(scopeKey, now));
    }

    private async Task<bool> MayWorkInAsync(string actorId, string? tenantId, Guid divisionId, CancellationToken cancellationToken)
    {
        if (!NhAssistantWholeApplicationTenancy.TakesPart(registration, tenantId)
            || services.GetService<INhDivisionAccess>() is not { } access)
        {
            return false;
        }

        var division = await subjects.DescribeAsync(NhAiSystemSubjects.ForDivision(divisionId), cancellationToken);
        return division is not null
            && await access.HasAccessAsync(new NhActorRef(NhActorKinds.User, actorId), divisionId, cancellationToken);
    }
}
