using System.Security.Claims;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Optional application hook that lets an owner invite colleagues directly instead of sharing an
/// invitation link. Return only people the caller may invite: people of the caller's tenant who may
/// use the assistant. The library still checks the tenant and the agent policy of every participant
/// on each request.
/// </summary>
public interface INhAssistantParticipantDirectory
{
    /// <summary>
    /// Searches people the caller may invite. Return at most <see cref="NhAssistantDirectorySearch.Limit"/> entries.
    /// </summary>
    ValueTask<IReadOnlyList<NhAssistantDirectoryEntry>> SearchAsync(
        NhAssistantDirectorySearch search,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves one person the caller may invite, or <see langword="null"/> when the caller may not invite them.
    /// </summary>
    ValueTask<NhAssistantDirectoryEntry?> FindAsync(
        NhAssistantDirectoryLookup lookup,
        CancellationToken cancellationToken);
}

/// <param name="Caller">The authenticated invocation context of the owner who searches.</param>
/// <param name="Query">Trimmed search text of 2 to 100 characters.</param>
/// <param name="Limit">Maximum number of entries to return.</param>
public sealed record NhAssistantDirectorySearch(
    NhAiInvocationContext Caller,
    string Query,
    int Limit);

/// <param name="Caller">The authenticated invocation context of the owner who invites.</param>
/// <param name="ActorId">The actor id of the person to invite, as returned by a search.</param>
public sealed record NhAssistantDirectoryLookup(
    NhAiInvocationContext Caller,
    string ActorId);

/// <summary>
/// A person the caller may invite. <paramref name="ActorId"/> must be the actor id the authenticated
/// invocation context resolver produces for that person; <paramref name="Detail"/> is an optional
/// short distinguishing text such as a department, shown only to the inviting owner.
/// </summary>
public sealed record NhAssistantDirectoryEntry(
    string ActorId,
    string DisplayName,
    string? Detail = null);

/// <summary>
/// Optional application hook for the name other people see in a shared conversation. The default
/// reads the <c>name</c>, <c>given_name</c> and <c>family_name</c>, <see cref="ClaimTypes.Name"/>,
/// <c>preferred_username</c> and e-mail claims in that order.
/// </summary>
public interface INhAssistantDisplayNameResolver
{
    /// <summary>
    /// Returns the display name of the signed-in user, or <see langword="null"/> when unknown. The
    /// library trims it to 120 characters and removes control characters.
    /// </summary>
    ValueTask<string?> GetDisplayNameAsync(
        ClaimsPrincipal user,
        NhAiInvocationContext caller,
        CancellationToken cancellationToken);
}

internal sealed class NhAssistantClaimsDisplayNameResolver : INhAssistantDisplayNameResolver
{
    public ValueTask<string?> GetDisplayNameAsync(
        ClaimsPrincipal user,
        NhAiInvocationContext caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        var name = Claim(user, "name");
        if (name is null)
        {
            var given = Claim(user, "given_name") ?? Claim(user, ClaimTypes.GivenName);
            var family = Claim(user, "family_name") ?? Claim(user, ClaimTypes.Surname);
            if (given is not null || family is not null)
            {
                name = string.Join(' ', new[] { given, family }.Where(part => part is not null));
            }
        }
        name ??= Claim(user, ClaimTypes.Name)
            ?? Claim(user, "preferred_username")
            ?? Claim(user, "email")
            ?? Claim(user, ClaimTypes.Email)
            ?? user.Identity?.Name;
        return ValueTask.FromResult(name);
    }

    private static string? Claim(ClaimsPrincipal user, string type)
    {
        var value = user.FindFirst(type)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>
/// Normalizes display names of owners and participants before they are stored, shown or given to
/// the model: control characters and line breaks become spaces, brackets become parentheses so a
/// name cannot imitate the speaker marker, and the result is at most 120 characters.
/// </summary>
internal static class NhAssistantParticipantNames
{
    public const int MaxLength = 120;

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(Math.Min(value.Length, MaxLength));
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character switch
            {
                '[' => '(',
                ']' => ')',
                _ => character
            });
            if (builder.Length >= MaxLength)
            {
                break;
            }
        }

        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            // Never keep half of a character that was cut off at the limit.
            builder.Length--;
        }
        return builder.ToString();
    }
}
