using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The accounts an administrator chooses from for the account an agent answers to: a search by name
/// or e-mail address through the application's <see cref="INhActorSearch"/>, and the names the
/// application's <see cref="INhActorDirectory"/> knows for given ids. Whether an account may answer
/// for an agent is checked when the run settings are saved.
/// </summary>
internal sealed class NhAssistantAccounts(IServiceProvider services)
{
    /// <summary>The most ids one lookup names.</summary>
    internal const int MaxIds = 50;

    /// <summary>The longest actor id a lookup accepts.</summary>
    internal const int MaxIdLength = 256;

    private const int DefaultLimit = 20;

    /// <summary>
    /// The accounts the application knows among the comma-separated <paramref name="ids"/>, in their
    /// order, unknown ids left out. More than <see cref="MaxIds"/> distinct ids, or an id longer than
    /// <see cref="MaxIdLength"/>, fail with the field error <c>ids</c> <c>invalid</c>.
    /// </summary>
    public async Task<TaskResult<IReadOnlyList<NhAssistantAccountDto>>> FindAsync(
        string ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var actorIds = ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (actorIds.Length > MaxIds || actorIds.Any(actorId => actorId.Length > MaxIdLength))
        {
            return TaskResult<IReadOnlyList<NhAssistantAccountDto>>.Failed(
                NhAssistantValidationErrors.Failed("ids", NhAssistantFieldErrors.Invalid));
        }

        var directory = services.GetService<INhActorDirectory>();
        if (directory is null)
        {
            // Without a directory the application names no accounts.
            IReadOnlyList<NhAssistantAccountDto> none = [];
            return TaskResult<IReadOnlyList<NhAssistantAccountDto>>.Succeeded(none);
        }

        var references = actorIds.ToDictionary(
            actorId => Guid.TryParse(actorId, out var userId) ? NhActorRef.ForUser(userId) : new NhActorRef(NhActorKinds.User, actorId),
            actorId => actorId);
        var known = await directory.GetAsync(references.Keys.ToArray(), cancellationToken);
        IReadOnlyList<NhAssistantAccountDto> accounts = references
            .Where(reference => known.ContainsKey(reference.Key))
            .Select(reference => ToAccount(reference.Value, known[reference.Key]))
            .ToArray();
        return TaskResult<IReadOnlyList<NhAssistantAccountDto>>.Succeeded(accounts);
    }

    /// <summary>
    /// The accounts whose name or e-mail address matches <paramref name="query"/> (2 to 100
    /// characters, otherwise the field error <c>query</c> <c>invalid</c>), at most
    /// <paramref name="limit"/> (default 20, clamped to 1 to 50). Without a registered search it fails
    /// with <see cref="NhAssistantErrorCodes.AccountSearchUnavailable"/>.
    /// </summary>
    public async Task<TaskResult<IReadOnlyList<NhAssistantAccountDto>>> SearchAsync(
        string? query,
        int? limit,
        CancellationToken cancellationToken)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length is < NhActorSearchRequest.MinQueryLength or > NhActorSearchRequest.MaxQueryLength)
        {
            return TaskResult<IReadOnlyList<NhAssistantAccountDto>>.Failed(
                NhAssistantValidationErrors.Failed("query", NhAssistantFieldErrors.Invalid));
        }

        var search = services.GetService<INhActorSearch>();
        if (search is null)
        {
            return TaskResult<IReadOnlyList<NhAssistantAccountDto>>.Failed(
                NhAssistantErrorCodes.AccountSearchUnavailable,
                "The application offers no account search.");
        }

        var found = await search.SearchAsync(
            new NhActorSearchRequest(text, Math.Clamp(limit ?? DefaultLimit, 1, NhActorSearchRequest.MaxLimit)),
            cancellationToken);
        IReadOnlyList<NhAssistantAccountDto> accounts = found.Select(info => ToAccount(info.Actor.Id, info)).ToArray();
        return TaskResult<IReadOnlyList<NhAssistantAccountDto>>.Succeeded(accounts);
    }

    private static NhAssistantAccountDto ToAccount(string actorId, NhActorInfo info)
    {
        return new NhAssistantAccountDto(actorId, info.DisplayName, info.Email);
    }
}
