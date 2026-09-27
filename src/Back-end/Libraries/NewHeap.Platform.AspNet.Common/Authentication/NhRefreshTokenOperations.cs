using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace NewHeap.Platform.AspNet.Common.Authentication;

internal static class NhRefreshTokenOperations
{
    internal static async Task<bool> TryConsumeAsync(
        IQueryable<NhUserAuthRefreshToken> refreshTokens,
        Guid userId,
        string token,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var candidates = await refreshTokens
            .Where(x =>
                x.UserId == userId
                && x.Token == token
                && x.ExpiryDateTime >= now)
            .Select(x => new RefreshTokenMatch(x.Id, x.Token))
            .ToListAsync(cancellationToken);
        var candidate = candidates.FirstOrDefault(x =>
            string.Equals(x.Token, token, StringComparison.Ordinal));

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
        string token,
        CancellationToken cancellationToken = default)
    {
        var candidates = await refreshTokens
            .Where(x => x.Token == token)
            .Select(x => new RefreshTokenMatch(x.Id, x.Token))
            .ToListAsync(cancellationToken);
        var exactIds = candidates
            .Where(x => string.Equals(x.Token, token, StringComparison.Ordinal))
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
