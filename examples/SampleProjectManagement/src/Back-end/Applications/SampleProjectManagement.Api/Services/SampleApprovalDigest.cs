using System.Security.Claims;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// What waits for one person, per status of the approval inbox, as a daily digest message would say
/// it: proposals to decide, approved proposals the order desk has not reported on yet, and executions
/// nobody knows the outcome of.
/// </summary>
public sealed record SampleApprovalDigestCounts(int Pending, int AwaitingApplication, int ExecutionUnknown)
{
    /// <summary>
    /// Whether the person has anything to hear about.
    /// </summary>
    public bool IsEmpty => Pending == 0 && AwaitingApplication == 0 && ExecutionUnknown == 0;
}

/// <summary>
/// SPM-327: the sample's daily approval digest counts what waits for a person with
/// <see cref="INhAssistantProposalCounts"/>, per status and with the inbox's own visibility: proposals of
/// projects the person approves, of runs they started and of the whole application where they decide.
/// The counts carry ids and numbers only, so the digest never shows proposal content; the link it
/// sends opens <c>GET approvals/inbox?status=</c> with the status the person chose. The application
/// vouches for the person, as it does when it sends the digest from its own job.
/// </summary>
public sealed class SampleApprovalDigest(INhAssistantProposalCounts counts)
{
    public async Task<SampleApprovalDigestCounts> CountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var actorId = userId.ToString("D");
        // The tenant is required: a host with tenants passes the person's own, or the counts leave out
        // every proposal stored under a tenant. The sample has no tenants.
        var person = new NhAssistantDecidingPerson(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "sample-digest")),
            actorId,
            TenantId: null);
        var byStatus = await counts.CountAsync(person, NhAssistantInboxStatuses.All, cancellationToken);
        return new SampleApprovalDigestCounts(
            byStatus[NhAssistantInboxStatuses.Pending],
            byStatus[NhAssistantInboxStatuses.AwaitingApplication],
            byStatus[NhAssistantInboxStatuses.ExecutionUnknown]);
    }
}
