using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

/// <summary>
/// Default owner directory. Shows the identity user name, falling back to the email
/// address, of the application's user type.
/// </summary>
internal sealed class NhIdentityBackgroundOperationOwnerDirectory<TUser> : INhBackgroundOperationOwnerDirectory
    where TUser : IdentityUser<Guid>
{
    private readonly IRepository<NhBackgroundOperation> _repository;

    public NhIdentityBackgroundOperationOwnerDirectory(IRepository<NhBackgroundOperation> repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyDictionary<Guid, NhBackgroundOperationOwner>> GetOwnersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, NhBackgroundOperationOwner>();
        }

        var ids = userIds.Distinct().ToList();
        var users = await _repository.GetDbSet<TUser>()
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                x.UserName,
                x.Email
            })
            .ToListAsync(cancellationToken);

        var owners = new Dictionary<Guid, NhBackgroundOperationOwner>();
        foreach (var user in users)
        {
            var displayName = !string.IsNullOrWhiteSpace(user.UserName)
                ? user.UserName
                : user.Email;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            owners[user.Id] = new NhBackgroundOperationOwner(user.Id, displayName);
        }

        return owners;
    }
}
