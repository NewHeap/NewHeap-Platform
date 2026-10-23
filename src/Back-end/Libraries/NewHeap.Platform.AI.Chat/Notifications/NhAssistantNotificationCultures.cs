using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Actors;

namespace NewHeap.Platform.AI.Chat.Notifications;

/// <summary>
/// The languages of the recipients of in-app notifications, as the application's
/// <see cref="INhActorDirectory"/> knows them.
/// </summary>
internal static class NhAssistantNotificationCultures
{
    /// <summary>
    /// The culture of each recipient the directory knows; empty without a directory. A recipient
    /// without one reads the caller's fallback.
    /// </summary>
    public static async Task<IReadOnlyDictionary<NhActorRef, string?>> GetAsync(
        IServiceProvider services,
        IReadOnlyCollection<NhActorRef> recipients,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(recipients);

        var directory = services.GetService<INhActorDirectory>();
        if (directory is null)
        {
            return new Dictionary<NhActorRef, string?>();
        }

        var known = await directory.GetAsync(recipients, cancellationToken);
        return known.ToDictionary(pair => pair.Key, pair => pair.Value.Culture);
    }
}
