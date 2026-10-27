using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
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
/// NewHeap Identity: membership of the division, read from the user's current division claims. A
/// user who is no member of the division, or a division that does not exist, is denied. For the
/// signed-in user of the request it is the same check as
/// <see cref="HttpContextExtensions.HasDivisionAccessAsync"/>.
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

/// <summary>
/// NewHeap Identity: the divisions of the application's identity division table, by name. The name
/// is read through the entity model, so any division type with a <c>Name</c> column works on every
/// relational provider.
/// </summary>
internal sealed class NhIdentityDivisionDirectory<TDivision> : INhDivisionDirectory
    where TDivision : class, IdDbEntity
{
    private const string NameProperty = "Name";

    private readonly IRepository<TDivision> _divisions;

    public NhIdentityDivisionDirectory(IRepository<TDivision> divisions)
    {
        _divisions = divisions;
    }

    public async Task<IReadOnlyList<NhDivisionInfo>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, NhDivisionInfo.MaxListLimit);
        var divisions = await _divisions.GetDbSet<TDivision>()
            .AsNoTracking()
            .OrderBy(division => EF.Property<string>(division, NameProperty))
            .ThenBy(division => division.Id)
            .Take(take)
            .Select(division => new
            {
                division.Id,
                Name = EF.Property<string>(division, NameProperty)
            })
            .ToListAsync(cancellationToken);

        return divisions
            .Select(division => new NhDivisionInfo(division.Id, NameOf(division.Id, division.Name)))
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<Guid, NhDivisionInfo>> GetAsync(
        IReadOnlyCollection<Guid> divisionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(divisionIds);

        var ids = divisionIds.Distinct().Take(NhDivisionInfo.MaxListLimit).ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, NhDivisionInfo>();
        }

        var divisions = await _divisions.GetDbSet<TDivision>()
            .AsNoTracking()
            .Where(division => ids.Contains(division.Id))
            .Select(division => new
            {
                division.Id,
                Name = EF.Property<string>(division, NameProperty)
            })
            .ToListAsync(cancellationToken);

        return divisions.ToDictionary(
            division => division.Id,
            division => new NhDivisionInfo(division.Id, NameOf(division.Id, division.Name)));
    }

    /// <summary>A division without a name is shown by its id.</summary>
    private static string NameOf(Guid id, string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? id.ToString("D") : name.Trim();
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

/// <summary>
/// NewHeap Identity: searches the user name and e-mail address of the application's users, ignoring
/// case, and leaves out users who are locked out.
/// </summary>
internal sealed class NhIdentityActorSearch<TUser> : INhActorSearch
    where TUser : IdentityUser<Guid>
{
    private readonly IRepository<TUser> _users;
    private readonly TimeProvider _time;

    public NhIdentityActorSearch(IRepository<TUser> users, TimeProvider? time = null)
    {
        _users = users;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<NhActorInfo>> SearchAsync(
        NhActorSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = request.Query.Trim();
        if (query.Length < NhActorSearchRequest.MinQueryLength
            || query.Length > NhActorSearchRequest.MaxQueryLength
            || !request.Kinds.Contains(NhActorKinds.User))
        {
            return [];
        }

        var limit = Math.Clamp(request.Limit, 1, NhActorSearchRequest.MaxLimit);
        // The normalized columns are upper case in every provider, so Contains needs no collation.
        var normalized = query.ToUpperInvariant();
        var now = _time.GetUtcNow();
        var users = await _users.GetDbSet<TUser>()
            .AsNoTracking()
            .Where(user => (user.NormalizedUserName != null && user.NormalizedUserName.Contains(normalized))
                           || (user.NormalizedEmail != null && user.NormalizedEmail.Contains(normalized)))
            .Where(user => user.LockoutEnd == null || user.LockoutEnd <= now)
            .OrderBy(user => user.UserName)
            .Take(limit)
            .Select(user => new
            {
                user.Id,
                user.UserName,
                user.Email
            })
            .ToListAsync(cancellationToken);

        var result = new List<NhActorInfo>(users.Count);
        foreach (var user in users)
        {
            var displayName = !string.IsNullOrWhiteSpace(user.UserName) ? user.UserName : user.Email;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                result.Add(new NhActorInfo(NhActorRef.ForUser(user.Id), displayName) { Email = user.Email });
            }
        }

        return result;
    }
}
