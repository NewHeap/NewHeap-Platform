using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Identity.Claims;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-327: the people of the whole application as the sample lists them for the assistant's
/// notifications (<c>UseSystemSubject(s => s.Members&lt;SampleSystemMembers&gt;())</c>). The whole
/// application keeps no member snapshot, so without this list a company-wide proposal of the project
/// follow-up in a thread nobody opened would reach nobody. The sample names the people who may decide
/// its proposals: the users of the administrator role and of every role that grants
/// <c>app.project.manage</c>, as the administration policy says. It names candidates only; the assistant
/// still evaluates the read and approve policies and the agent's view policy for each of them, so a
/// listed reader never hears what is meant for approvers. The sample lists no divisions for the whole
/// application, so a division's list is empty.
/// </summary>
public sealed class SampleSystemMembers(UserManager<NhUser> users, RoleManager<NhUserRole> roles) : INhAssistantSystemMembers
{
    /// <summary>
    /// The role that passes the administration policy without the manage permission.
    /// </summary>
    public const string AdministratorRole = "administrator";

    /// <summary>
    /// The permission that passes the administration policy.
    /// </summary>
    public const string ManagePermission = "app.project.manage";

    public async IAsyncEnumerable<string> ListAsync(
        Guid? divisionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (divisionId is not null)
        {
            yield break;
        }

        var listed = new HashSet<Guid>();
        foreach (var role in await roles.Roles.AsNoTracking().OrderBy(role => role.Name).ToListAsync(cancellationToken))
        {
            if (role.Name is null || !await DecidesAsync(role))
            {
                continue;
            }

            foreach (var user in (await users.GetUsersInRoleAsync(role.Name)).OrderBy(user => user.Id))
            {
                // The assistant's context resolver gives a person's actor id as their user id.
                if (listed.Add(user.Id))
                {
                    yield return user.Id.ToString("D");
                }
            }
        }
    }

    private async Task<bool> DecidesAsync(NhUserRole role)
    {
        if (string.Equals(role.Name, AdministratorRole, StringComparison.Ordinal))
        {
            return true;
        }

        return (await roles.GetClaimsAsync(role)).Any(claim =>
            claim.Type == NhPlatformClaimTypes.Permission && claim.Value == ManagePermission);
    }
}
