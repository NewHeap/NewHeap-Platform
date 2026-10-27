using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Extensions;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Reads and deletes refresh-token rows. A row stores the token's digest
/// (<see cref="NhRefreshTokenKey.Digest"/>), never the token itself, so callers look rows up with the
/// digest of the token the client presented.
/// </summary>
internal static class NhRefreshTokenOperations
{
    internal static async Task<bool> TryConsumeAsync(
        IQueryable<NhUserAuthRefreshToken> refreshTokens,
        Guid userId,
        string tokenDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var candidates = await refreshTokens
            .Where(x =>
                x.UserId == userId
                && x.Token == tokenDigest
                && x.ExpiryDateTime >= now)
            .Select(x => new RefreshTokenMatch(x.Id, x.Token))
            .ToListAsync(cancellationToken);
        var candidate = candidates.FirstOrDefault(x =>
            string.Equals(x.Token, tokenDigest, StringComparison.Ordinal));

        if (candidate == null)
        {
            return false;
        }

        var deletedCount = await refreshTokens
            .Where(x =>
                x.Id == candidate.Id
                && x.ExpiryDateTime >= now)
            .ExecuteDeleteAsync(cancellationToken);

        return deletedCount == 1;
    }

    internal static async Task<int> RevokeAsync(
        IQueryable<NhUserAuthRefreshToken> refreshTokens,
        string tokenDigest,
        CancellationToken cancellationToken = default)
    {
        var candidates = await refreshTokens
            .Where(x => x.Token == tokenDigest)
            .Select(x => new RefreshTokenMatch(x.Id, x.Token))
            .ToListAsync(cancellationToken);
        var exactIds = candidates
            .Where(x => string.Equals(x.Token, tokenDigest, StringComparison.Ordinal))
            .Select(x => x.Id)
            .ToList();

        if (exactIds.Count == 0)
        {
            return 0;
        }

        return await refreshTokens
            .Where(x => exactIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    internal static Task<int> RevokeAllAsync(
        IQueryable<NhUserAuthRefreshToken> refreshTokens,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return refreshTokens
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    internal static Task<int> DeleteExpiredAsync(
        IQueryable<NhUserAuthRefreshToken> refreshTokens,
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return refreshTokens
            .Where(x => x.UserId == userId && x.ExpiryDateTime < now)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private sealed record RefreshTokenMatch(Guid Id, string Token);
}
