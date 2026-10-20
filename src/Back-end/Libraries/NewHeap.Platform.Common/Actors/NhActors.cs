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

/// <summary>One division of the application, as people see it.</summary>
/// <param name="Id">The division id.</param>
/// <param name="Name">The name people see.</param>
public sealed record NhDivisionInfo(Guid Id, string Name)
{
    /// <summary>The most divisions one <see cref="INhDivisionDirectory.ListAsync"/> call returns.</summary>
    public const int MaxListLimit = 1_000;
}

/// <summary>
/// Lists the divisions of the application with their names, so modules can offer work per division,
/// such as an agent that runs once for every division. Divisions are optional: NewHeap Identity lists
/// its divisions; an application with its own tenancy model registers its own directory with
/// <see cref="NhActorServiceCollectionExtensions.AddNewHeapDivisionDirectory{TDirectory}"/>. Without
/// one the application has no listed divisions, and modules offer nothing per division. Who may work
/// in a division stays the question of <see cref="INhDivisionAccess"/>.
/// </summary>
public interface INhDivisionDirectory
{
    /// <summary>
    /// Returns at most <paramref name="limit"/> divisions (1 to <see cref="NhDivisionInfo.MaxListLimit"/>),
    /// ordered by name and then by id, so repeated calls list them in the same order.
    /// </summary>
    Task<IReadOnlyList<NhDivisionInfo>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the divisions with these ids that exist. Unknown ids are missing from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, NhDivisionInfo>> GetAsync(
        IReadOnlyCollection<Guid> divisionIds,
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

/// <summary>A search for actors by name or e-mail address.</summary>
/// <param name="Query">Trimmed search text of <see cref="MinQueryLength"/> to <see cref="MaxQueryLength"/> characters.</param>
/// <param name="Limit">The most results to return, 1 to <see cref="MaxLimit"/>.</param>
public sealed record NhActorSearchRequest(string Query, int Limit = 20)
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 100;
    public const int MaxLimit = 50;

    /// <summary>The actor kinds to search; users by default.</summary>
    public IReadOnlyCollection<string> Kinds { get; init; } = [NhActorKinds.User];
}

/// <summary>
/// Finds actors by name, for example to choose the account an agent answers to. NewHeap Identity
/// searches its users; an application with its own identity model registers its own search with
/// <see cref="NhActorServiceCollectionExtensions.AddNewHeapActorSearch{TSearch}"/>. Without one, modules
/// ask for an actor's id instead.
/// </summary>
public interface INhActorSearch
{
    /// <summary>
    /// Returns at most <see cref="NhActorSearchRequest.Limit"/> actors whose name or e-mail address
    /// contains the query, ignoring case. Leave out actors that may not sign in.
    /// </summary>
    Task<IReadOnlyList<NhActorInfo>> SearchAsync(
        NhActorSearchRequest request,
        CancellationToken cancellationToken = default);
}
