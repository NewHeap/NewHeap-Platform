using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Actors;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

/// <summary>
/// Default owner directory. Shows the names that the platform's <see cref="INhActorDirectory"/>
/// knows; with NewHeap Identity that is the user name, falling back to the e-mail address.
/// </summary>
internal sealed class NhActorBackgroundOperationOwnerDirectory : INhBackgroundOperationOwnerDirectory
{
    private readonly IServiceProvider _services;

    public NhActorBackgroundOperationOwnerDirectory(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<IReadOnlyDictionary<Guid, NhBackgroundOperationOwner>> GetOwnersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var owners = new Dictionary<Guid, NhBackgroundOperationOwner>();
        var directory = _services.GetService<INhActorDirectory>();
        if (userIds.Count == 0 || directory is null)
        {
            return owners;
        }

        var actors = userIds
            .Distinct()
            .Select(NhActorRef.ForUser)
            .ToArray();
        var known = await directory.GetAsync(actors, cancellationToken);
        foreach (var (actor, info) in known)
        {
            if (actor.TryGetUserId(out var userId) && !string.IsNullOrWhiteSpace(info.DisplayName))
            {
                owners[userId] = new NhBackgroundOperationOwner(userId, info.DisplayName);
            }
        }

        return owners;
    }
}
