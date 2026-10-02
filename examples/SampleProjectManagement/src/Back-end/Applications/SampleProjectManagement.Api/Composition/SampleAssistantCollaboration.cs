using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Lets an owner invite colleagues of the active division directly, next to sharing an invitation
/// link. Only people of the same division are found; the assistant still checks the access policy
/// and the agent's policy of every participant on each request.
/// </summary>
public sealed class SampleAssistantParticipantDirectory(SampleProjectManagementDbContext dbContext) : INhAssistantParticipantDirectory
{
    public async ValueTask<IReadOnlyList<NhAssistantDirectoryEntry>> SearchAsync(
        NhAssistantDirectorySearch search,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (!TryGetDivision(search.Caller, out var divisionId))
        {
            return [];
        }

        var text = search.Query.ToLowerInvariant();
        _ = Guid.TryParse(search.Caller.ActorId, out var callerId);
        var colleagues = await Colleagues(divisionId)
            .Where(user => user.Id != callerId)
            .Where(user => (user.UserName ?? string.Empty).ToLower().Contains(text)
                || (user.Email ?? string.Empty).ToLower().Contains(text))
            .OrderBy(user => user.UserName)
            .ThenBy(user => user.Id)
            .Take(search.Limit)
            .Select(user => new { user.Id, user.UserName, user.Email })
            .ToListAsync(cancellationToken);
        return colleagues
            .Select(user => new NhAssistantDirectoryEntry(user.Id.ToString(), user.UserName ?? user.Email ?? string.Empty, user.Email))
            .ToArray();
    }

    public async ValueTask<NhAssistantDirectoryEntry?> FindAsync(
        NhAssistantDirectoryLookup lookup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (!TryGetDivision(lookup.Caller, out var divisionId) || !Guid.TryParse(lookup.ActorId, out var userId))
        {
            return null;
        }

        var colleague = await Colleagues(divisionId)
            .Where(user => user.Id == userId)
            .Select(user => new { user.Id, user.UserName, user.Email })
            .SingleOrDefaultAsync(cancellationToken);
        return colleague is null
            ? null
            : new NhAssistantDirectoryEntry(colleague.Id.ToString(), colleague.UserName ?? colleague.Email ?? string.Empty, colleague.Email);
    }

    private IQueryable<NewHeap.Platform.AspNet.Common.DAL.Entities.NhUser> Colleagues(Guid divisionId)
    {
        return dbContext.Users
            .AsNoTracking()
            .Where(user => user.DivisionUsers.Any(membership => membership.DivisionId == divisionId));
    }

    private static bool TryGetDivision(NhAiInvocationContext caller, out Guid divisionId)
    {
        divisionId = Guid.Empty;
        return caller.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var value)
            && Guid.TryParse(value, out divisionId);
    }
}

/// <summary>
/// Web Push for the sample. Keys come from <c>NewHeap:AI:Assistant:Push</c> (user secrets or the
/// environment). In Development without keys the sample creates one key pair per run, so push works
/// out of the box; browsers subscribe again after a restart because the key changed.
/// </summary>
internal static class SampleAssistantPush
{
    private static readonly Lazy<(string PublicKey, string PrivateKey)> DevelopmentKeys = new(NhAssistantWebPushKeys.Generate);

    public static void Configure(NhAssistantPushOptions push, IHostEnvironment environment)
    {
        push.Subject ??= "mailto:assistant@sample.localhost";
        if (!push.IsConfigured && environment.IsDevelopment())
        {
            (push.PublicKey, push.PrivateKey) = DevelopmentKeys.Value;
        }
        // Notify after 10 seconds of work, the library default, so short answers stay silent.
        push.MinimumTurnDuration = TimeSpan.FromSeconds(10);
    }
}
