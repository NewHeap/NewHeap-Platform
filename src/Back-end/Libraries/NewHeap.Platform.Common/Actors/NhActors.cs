namespace NewHeap.Platform.Common.Actors;

/// <summary>
/// The kinds of actors the platform knows. Modules may define more kinds; these three are the ones
/// every module understands.
/// </summary>
public static class NhActorKinds
{
    /// <summary>A person. Its id is the user id as a <see cref="Guid"/> in <c>D</c> format.</summary>
    public const string User = "user";

    /// <summary>An agent that acts on its own, such as an assistant agent run.</summary>
    public const string Agent = "agent";

    /// <summary>A technical process, such as a scheduled job.</summary>
    public const string System = "system";
}

/// <summary>
/// A user, agent or system, identified by its kind and its id. Equal references point to the
/// same actor.
/// </summary>
public sealed record NhActorRef
{
    public NhActorRef(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Kind = kind;
        Id = id;
    }

    public string Kind { get; }

    public string Id { get; }

    /// <summary>Returns the reference of the user with <paramref name="userId"/>.</summary>
    public static NhActorRef ForUser(Guid userId)
    {
        return new NhActorRef(NhActorKinds.User, userId.ToString("D"));
    }

    /// <summary>Reads the user id of a user reference.</summary>
    /// <returns><see langword="false"/> for other kinds and for ids that are not a <see cref="Guid"/>.</returns>
    public bool TryGetUserId(out Guid userId)
    {
        userId = Guid.Empty;
        return string.Equals(Kind, NhActorKinds.User, StringComparison.Ordinal)
            && Guid.TryParse(Id, out userId);
    }

    public override string ToString()
    {
        return $"{Kind}:{Id}";
    }
}

/// <summary>
/// The actor of the current request or background work, and the division it works in.
/// </summary>
/// <param name="Actor">The signed-in actor, or <see langword="null"/> when nobody is signed in.</param>
/// <param name="ActiveDivisionId">The division the actor works in, or <see langword="null"/> when the work has no division.</param>
public sealed record NhActorContext(NhActorRef? Actor, Guid? ActiveDivisionId)
{
    /// <summary>No signed-in actor and no division.</summary>
    public static NhActorContext Anonymous { get; } = new(null, null);

    /// <summary>The user id when the actor is a user; otherwise <see langword="null"/>.</summary>
    public Guid? UserId => Actor is not null && Actor.TryGetUserId(out var userId) ? userId : null;
}

/// <summary>
/// Supplies the current actor and active division to platform modules. NewHeap Identity reads them
/// from the request; an application with its own identity model registers its own accessor with
/// <see cref="NhActorServiceCollectionExtensions.AddNewHeapActorContext{TAccessor}"/>.
/// </summary>
public interface INhActorContextAccessor
{
    ValueTask<NhActorContext> GetCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides whether an actor may work in a division. Division is the platform's tenancy term; an
/// application with another tenancy model maps its unit onto divisions here. Without an
/// implementation there are no divisions, and every division is denied.
/// </summary>
public interface INhDivisionAccess
{
    ValueTask<bool> HasAccessAsync(
        NhActorRef actor,
        Guid divisionId,
        CancellationToken cancellationToken = default);
}

/// <summary>What the platform shows about an actor.</summary>
/// <param name="Actor">The actor the information describes.</param>
/// <param name="DisplayName">The name people see.</param>
public sealed record NhActorInfo(NhActorRef Actor, string DisplayName)
{
    public string? Email { get; init; }

    /// <summary>The actor's preferred culture name, such as <c>nl-NL</c>, when known.</summary>
    public string? Culture { get; init; }
}

/// <summary>
/// Looks up names and contact details of actors in one call. Modules use it for owner names,
/// participants and recipients instead of reading a user table.
/// </summary>
public interface INhActorDirectory
{
    /// <summary>
    /// Returns information about the actors the directory knows. Unknown actors are missing from the
    /// result; callers show the actor id instead.
    /// </summary>
    Task<IReadOnlyDictionary<NhActorRef, NhActorInfo>> GetAsync(
        IReadOnlyCollection<NhActorRef> actors,
        CancellationToken cancellationToken = default);
}
