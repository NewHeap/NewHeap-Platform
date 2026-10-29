using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using SampleProjectManagement.Api.Composition;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-378: the people outside a project's members who may approve, as the sample knows them
/// (<c>UseApproverCandidates</c>). The approval policies of the sample admit people outside the members
/// through two authorization policies only: <c>app.project.manage</c>, a policy rule of the intake review's
/// portfolio policy, and the administration policy <c>app.project.edit-or-admin</c>, which makes a person an
/// approver of the whole application. Both are passed only by the users of the administrator role and of
/// every role that grants <c>app.project.manage</c>, the people <see cref="SampleSystemMembers"/> lists, so
/// the sample names them and declares the list complete. For any other policy it cannot tell, and says so.
/// The names grant nothing: the approver directory checks each person against the policy as it checks a
/// project member, so an administrator without the permission is never an approver under
/// <c>app.project.manage</c>.
/// </summary>
public sealed class SampleApproverCandidates(UserManager<NhUser> users, RoleManager<NhUserRole> roles) : INhAssistantApproverCandidates
{
    /// <summary>
    /// The authorization policies whose people the sample can list completely.
    /// </summary>
    private static readonly HashSet<string> KnownPolicies = new(StringComparer.Ordinal)
    {
        SampleProjectStarterComposition.ProjectManagePolicy,
        SampleAssistantComposition.AdminPolicy
    };

    public async ValueTask<NhAssistantApproverCandidateList> ListAsync(
        NhAssistantApproverCandidateQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.TenantId is not null || !query.PolicyNames.All(KnownPolicies.Contains))
        {
            // The sample has no tenants, and it cannot list who passes another policy.
            return NhAssistantApproverCandidateList.Unknown;
        }

        // The whole application's roles follow its approve policy, the administration policy: the same
        // people.
        var actorIds = new List<string>();
        await foreach (var actorId in new SampleSystemMembers(users, roles).ListAsync(null, cancellationToken))
        {
            actorIds.Add(actorId);
            if (actorIds.Count > query.MaxCandidates)
            {
                break;
            }
        }

        var userIds = actorIds.Select(Guid.Parse).ToArray();
        var names = await users.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.UserName })
            .ToDictionaryAsync(user => user.Id.ToString("D"), user => user.UserName, cancellationToken);
        var candidates = actorIds
            .Take(query.MaxCandidates)
            .Select(actorId => new NhAssistantApproverCandidate(actorId) { DisplayName = names.GetValueOrDefault(actorId) })
            .ToArray();

        // More people than the directory checks is never everyone.
        return new NhAssistantApproverCandidateList(candidates, Complete: actorIds.Count <= query.MaxCandidates);
    }
}
