using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The categories of <see cref="AssistantNotificationPreference.Category"/>.
/// </summary>
internal static class NhAssistantNotificationPreferenceCategories
{
    /// <summary>The kinds of alert registered with <c>AddAlertKind</c> (FB87).</summary>
    public const string Alert = "alert";
}

/// <summary>
/// One change to a person's choices about one kind: each value that is set replaces the stored one;
/// <see cref="DigestTimeChanged"/> says whether <see cref="DigestTime"/> replaces it, so
/// <see langword="null"/> resets it to the kind's own time.
/// </summary>
internal sealed record NhAssistantNotificationPreferenceChange(string KindId)
{
    public bool? Muted { get; init; }

    public bool DigestTimeChanged { get; init; }

    public TimeOnly? DigestTime { get; init; }

    public bool? Push { get; init; }

    public bool? InApp { get; init; }
}

/// <summary>
/// Push subscriptions and notification choices: the global push switch and the choices per kind of
/// notification (FB87-6). Every call uses its own short-lived context.
/// </summary>
internal sealed class NhAssistantNotificationStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>How often saving choices is tried again after a concurrent save created a row first.</summary>
    private const int MaxPreferenceSaveAttempts = 3;

    /// <summary>
    /// Claims the notice that a person's consent to background use of a server, or their connection to
    /// it, is missing: <see langword="true"/> when no notice was claimed since <paramref name="since"/>,
    /// so the person is told at most once per interval. Of concurrent claims, also of other instances,
    /// one succeeds.
    /// </summary>
    public async Task<bool> TryClaimMcpConnectionNoticeAsync(
        string serverId,
        string personActorId,
        string code,
        DateTimeOffset now,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var context = contextFactory.CreateDbContext();
            var renewed = await context.McpConnectionNotices
                .Where(notice => notice.ServerId == serverId && notice.PersonActorId == personActorId && notice.NotifiedAt < since)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(notice => notice.NotifiedAt, now)
                        .SetProperty(notice => notice.Code, code),
                    cancellationToken);
            if (renewed == 1)
            {
                return true;
            }
            if (await context.McpConnectionNotices
                    .AsNoTracking()
                    .AnyAsync(notice => notice.ServerId == serverId && notice.PersonActorId == personActorId, cancellationToken))
            {
                return false;
            }

            context.McpConnectionNotices.Add(new AssistantMcpConnectionNotice
            {
                ServerId = serverId,
                PersonActorId = personActorId,
                Code = code,
                NotifiedAt = now
            });
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Another instance claimed the notice first: the next attempt finds its row.
            }
        }
    }

    /// <summary>Removes the notices of missing MCP connections claimed before <paramref name="before"/>.</summary>
    public async Task<int> DeleteMcpConnectionNoticesBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnectionNotices
            .Where(notice => notice.NotifiedAt < before)
            .ExecuteDeleteAsync(cancellationToken);
    }

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

    /// <summary>
    /// The person's choices about the kinds of <paramref name="category"/>, by kind id; a kind without
    /// a choice is missing.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, AssistantNotificationPreference>> GetPreferencesAsync(
        string actorId,
        string category,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var stored = await context.NotificationPreferences
            .AsNoTracking()
            .Where(preference => preference.ActorId == actorId && preference.Category == category)
            .ToListAsync(cancellationToken);
        return stored.ToDictionary(preference => preference.KindId, StringComparer.Ordinal);
    }

    /// <summary>
    /// The choices of <paramref name="actorIds"/> about the kinds of <paramref name="category"/>, by
    /// person and kind, for a delivery that serves several people at once.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string ActorId, string KindId), AssistantNotificationPreference>> GetPreferencesAsync(
        IReadOnlyCollection<string> actorIds,
        string category,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actorIds);
        if (actorIds.Count == 0)
        {
            return new Dictionary<(string, string), AssistantNotificationPreference>();
        }

        var ids = actorIds.Distinct(StringComparer.Ordinal).ToArray();
        await using var context = contextFactory.CreateDbContext();
        var stored = await context.NotificationPreferences
            .AsNoTracking()
            .Where(preference => ids.Contains(preference.ActorId) && preference.Category == category)
            .ToListAsync(cancellationToken);
        return stored.ToDictionary(preference => (preference.ActorId, preference.KindId));
    }

    /// <summary>
    /// Applies <paramref name="changes"/> to the person's choices about the kinds of
    /// <paramref name="category"/> in one transaction: all of them or none.
    /// </summary>
    public async Task SavePreferencesAsync(
        string actorId,
        string category,
        IReadOnlyList<NhAssistantNotificationPreferenceChange> changes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await TrySavePreferencesAsync(actorId, category, changes, now, cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < MaxPreferenceSaveAttempts)
            {
                // A concurrent request of the same person created a row first; apply the changes to it.
            }
        }
    }

    private async Task TrySavePreferencesAsync(
        string actorId,
        string category,
        IReadOnlyList<NhAssistantNotificationPreferenceChange> changes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var kindIds = changes.Select(change => change.KindId).Distinct(StringComparer.Ordinal).ToArray();
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var stored = await context.NotificationPreferences
            .Where(preference => preference.ActorId == actorId
                && preference.Category == category
                && kindIds.Contains(preference.KindId))
            .ToDictionaryAsync(preference => preference.KindId, StringComparer.Ordinal, cancellationToken);

        foreach (var change in changes)
        {
            if (!stored.TryGetValue(change.KindId, out var preference))
            {
                preference = new AssistantNotificationPreference
                {
                    ActorId = actorId,
                    Category = category,
                    KindId = change.KindId
                };
                context.NotificationPreferences.Add(preference);
                stored[change.KindId] = preference;
            }

            if (change.Muted is { } muted)
            {
                preference.Muted = muted;
            }
            if (change.DigestTimeChanged)
            {
                preference.DigestTime = change.DigestTime;
            }
            if (change.Push is { } push)
            {
                preference.Push = push;
            }
            if (change.InApp is { } inApp)
            {
                preference.InApp = inApp;
            }

            preference.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
