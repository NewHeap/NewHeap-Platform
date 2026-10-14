using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Identity.Claims;

namespace NewHeap.Platform.AspNet.Common.Services.Actors;

/// <summary>
/// Generic default: the signed-in user is the <c>NameIdentifier</c> claim as a user id, and
/// there are no divisions.
/// </summary>
internal class NhClaimsActorContextAccessor : INhActorContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public NhClaimsActorContextAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public ValueTask<NhActorContext> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var userId = httpContext?.GetUserId();
        if (httpContext is null || !userId.HasValue)
        {
            return ValueTask.FromResult(NhActorContext.Anonymous);
        }

        return ValueTask.FromResult(new NhActorContext(NhActorRef.ForUser(userId.Value), ReadActiveDivisionId(httpContext)));
    }

    protected virtual Guid? ReadActiveDivisionId(HttpContext httpContext)
    {
        return null;
    }
}

/// <summary>
/// NewHeap Identity: the signed-in user, and the active division from the
/// <c>X-NH-ActiveDivisionId</c> request header.
/// </summary>
internal sealed class NhIdentityActorContextAccessor : NhClaimsActorContextAccessor
{
    public NhIdentityActorContextAccessor(IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor)
    {
    }

    protected override Guid? ReadActiveDivisionId(HttpContext httpContext)
    {
        return httpContext.GetActiveDivisionId();
    }
}

/// <summary>Generic default: there are no divisions, so every division is denied.</summary>
internal sealed class NhNoDivisionAccess : INhDivisionAccess
{
    public ValueTask<bool> HasAccessAsync(
        NhActorRef actor,
        Guid divisionId,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(false);
    }
}

/// <summary>
/// NewHeap Identity: the division claims of the user. For the signed-in user of the request it is
/// the same check as <see cref="HttpContextExtensions.HasDivisionAccessAsync"/>.
/// </summary>
internal sealed class NhIdentityDivisionAccess : INhDivisionAccess
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceProvider _services;

    public NhIdentityDivisionAccess(IHttpContextAccessor httpContextAccessor, IServiceProvider services)
    {
        _httpContextAccessor = httpContextAccessor;
        _services = services;
    }

    public async ValueTask<bool> HasAccessAsync(
        NhActorRef actor,
        Guid divisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.TryGetUserId(out var userId))
        {
            return false;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is not null && httpContext.GetUserId() == userId)
        {
            return await httpContext.HasDivisionAccessAsync(divisionId, cancellationToken: cancellationToken);
        }

        var userManager = _services.GetService<INhUserManager>();
        if (userManager is null)
        {
            return false;
        }

        var claims = await userManager.GetValidClaimsByUserIdAsync(userId, true, cancellationToken);
        if (claims.Any(claim => claim.Type == NhPlatformClaimTypes.Permission
                && claim.Value == Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
        {
            return true;
        }

        return await userManager.DivisionAccessAsync(divisionId, claims, cancellationToken: cancellationToken);
    }
}

/// <summary>Generic default: an actor's id is its display name.</summary>
internal sealed class NhIdActorDirectory : INhActorDirectory
{
    public Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(
        IReadOnlyCollection<NhActorRef> actors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actors);

        IReadOnlyDictionary<NhActorRef, NhActorInfo> result = actors
            .Distinct()
            .ToDictionary(actor => actor, actor => new NhActorInfo(actor, actor.Id));
        return Task.FromResult(result);
    }
}

/// <summary>
/// NewHeap Identity: users from the application's identity user table, shown by user name and
/// otherwise by e-mail address. Other actor kinds are unknown to this directory.
/// </summary>
internal sealed class NhIdentityActorDirectory<TUser> : INhActorDirectory
    where TUser : IdentityUser<Guid>
{
    private readonly IRepository<TUser> _users;

    public NhIdentityActorDirectory(IRepository<TUser> users)
    {
        _users = users;
    }

    public async Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(
        IReadOnlyCollection<NhActorRef> actors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actors);

        var userIds = new List<Guid>();
        foreach (var actor in actors)
        {
            if (actor.TryGetUserId(out var userId))
            {
                userIds.Add(userId);
            }
        }

        var result = new Dictionary<NhActorRef, NhActorInfo>();
        if (userIds.Count == 0)
        {
            return result;
        }

        var ids = userIds.Distinct().ToList();
        var users = await _users.GetDbSet<TUser>()
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                user.UserName,
                user.Email
            })
            .ToListAsync(cancellationToken);

        foreach (var user in users)
        {
            var displayName = !string.IsNullOrWhiteSpace(user.UserName)
                ? user.UserName
                : user.Email;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var reference = NhActorRef.ForUser(user.Id);
            result[reference] = new NhActorInfo(reference, displayName) { Email = user.Email };
        }

        return result;
    }
}
