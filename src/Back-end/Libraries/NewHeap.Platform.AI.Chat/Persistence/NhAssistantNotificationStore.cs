using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Push subscriptions and notification choices. Every call uses its own short-lived context.
/// </summary>
internal sealed class NhAssistantNotificationStore(NhAssistantDbContextFactory contextFactory)
{
    public async Task<bool> IsPushEnabledAsync(string actorId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var stored = await context.NotificationSettings
            .AsNoTracking()
            .Where(setting => setting.ActorId == actorId)
            .Select(setting => (bool?)setting.PushEnabled)
            .SingleOrDefaultAsync(cancellationToken);
        return stored ?? true;
    }

    public async Task SetPushEnabledAsync(string actorId, bool enabled, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var affected = await context.NotificationSettings
            .Where(setting => setting.ActorId == actorId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(setting => setting.PushEnabled, enabled)
                    .SetProperty(setting => setting.UpdatedAt, now),
                cancellationToken);
        if (affected == 1)
        {
            return;
        }

        context.NotificationSettings.Add(new AssistantNotificationSetting
        {
            ActorId = actorId,
            PushEnabled = enabled,
            UpdatedAt = now
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request created the row; apply this choice to it.
            await using var retry = contextFactory.CreateDbContext();
            await retry.NotificationSettings
                .Where(setting => setting.ActorId == actorId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(setting => setting.PushEnabled, enabled)
                        .SetProperty(setting => setting.UpdatedAt, now),
                    cancellationToken);
        }
    }

    /// <summary>
    /// Stores a subscription for the actor. An endpoint that belonged to another actor moves to this
    /// actor, because the browser now signs in as this actor. The oldest subscriptions above
    /// <paramref name="maximumPerActor"/> are removed.
    /// </summary>
    public async Task UpsertSubscriptionAsync(
        string actorId,
        string endpoint,
        string p256dh,
        string auth,
        string language,
        int maximumPerActor,
        CancellationToken cancellationToken)
    {
        var endpointHash = HashEndpoint(endpoint);
        var now = DateTimeOffset.UtcNow;
        await using (var context = contextFactory.CreateDbContext())
        {
            var affected = await UpdateAsync(context, actorId, endpointHash, p256dh, auth, language, now, cancellationToken);
            if (affected == 0)
            {
                context.PushSubscriptions.Add(new AssistantPushSubscription
                {
                    Id = Guid.NewGuid(),
                    ActorId = actorId,
                    EndpointHash = endpointHash,
                    Endpoint = endpoint,
                    P256dh = p256dh,
                    Auth = auth,
                    Language = language,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // The same browser subscribed concurrently; update the row it created.
                    await using var retry = contextFactory.CreateDbContext();
                    await UpdateAsync(retry, actorId, endpointHash, p256dh, auth, language, now, cancellationToken);
                }
            }
        }

        await using var cleanup = contextFactory.CreateDbContext();
        var surplus = await cleanup.PushSubscriptions
            .Where(subscription => subscription.ActorId == actorId)
            .OrderByDescending(subscription => subscription.UpdatedAt)
            .ThenBy(subscription => subscription.Id)
            .Skip(maximumPerActor)
            .Select(subscription => subscription.Id)
            .ToListAsync(cancellationToken);
        if (surplus.Count > 0)
        {
            await cleanup.PushSubscriptions
                .Where(subscription => surplus.Contains(subscription.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    public async Task<bool> DeleteSubscriptionAsync(
        string actorId,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var endpointHash = HashEndpoint(endpoint);
        await using var context = contextFactory.CreateDbContext();
        var affected = await context.PushSubscriptions
            .Where(subscription => subscription.EndpointHash == endpointHash && subscription.ActorId == actorId)
            .ExecuteDeleteAsync(cancellationToken);
        return affected == 1;
    }

    /// <summary>
    /// Removes subscriptions the push service reported as gone.
    /// </summary>
    public async Task DeleteSubscriptionsAsync(
        IReadOnlyCollection<Guid> subscriptionIds,
        CancellationToken cancellationToken)
    {
        if (subscriptionIds.Count == 0)
        {
            return;
        }
        var ids = subscriptionIds.ToArray();
        await using var context = contextFactory.CreateDbContext();
        await context.PushSubscriptions
            .Where(subscription => ids.Contains(subscription.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// The subscriptions of the actors that did not turn push notifications off.
    /// </summary>
    public async Task<IReadOnlyList<AssistantPushSubscription>> GetDeliverableSubscriptionsAsync(
        IReadOnlyCollection<string> actorIds,
        CancellationToken cancellationToken)
    {
        if (actorIds.Count == 0)
        {
            return [];
        }
        var ids = actorIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.PushSubscriptions
            .AsNoTracking()
            .Where(subscription => ids.Contains(subscription.ActorId)
                && !context.NotificationSettings.Any(setting =>
                    setting.ActorId == subscription.ActorId && !setting.PushEnabled))
            .ToListAsync(cancellationToken);
    }

    public static string HashEndpoint(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
    }

    private static Task<int> UpdateAsync(
        NhAssistantDbContext context,
        string actorId,
        string endpointHash,
        string p256dh,
        string auth,
        string language,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return context.PushSubscriptions
            .Where(subscription => subscription.EndpointHash == endpointHash)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(subscription => subscription.ActorId, actorId)
                    .SetProperty(subscription => subscription.P256dh, p256dh)
                    .SetProperty(subscription => subscription.Auth, auth)
                    .SetProperty(subscription => subscription.Language, language)
                    .SetProperty(subscription => subscription.UpdatedAt, now),
                cancellationToken);
    }
}
