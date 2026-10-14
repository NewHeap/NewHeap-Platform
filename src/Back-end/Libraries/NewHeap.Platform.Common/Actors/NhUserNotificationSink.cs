using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.Common.Actors;

/// <summary>How prominent an in-app notification is. The values match the stored user-notification severity.</summary>
public enum NhUserNotificationLevel
{
    Information = 0,
    Success = 10,
    Warning = 20,
    Error = 30
}

/// <summary>One message for a user in the application.</summary>
/// <param name="UserId">The user who receives the message.</param>
/// <param name="Title">The title, already localized for the user.</param>
/// <param name="Message">The text, already localized for the user.</param>
public sealed record NhUserNotificationRequest(Guid UserId, string Title, string Message)
{
    public string? Url { get; init; }

    public bool UrlInNewTab { get; init; }

    /// <summary>A category the application can filter on, such as <c>background-operation</c>.</summary>
    public string? Category { get; init; }

    public NhUserNotificationLevel Level { get; init; } = NhUserNotificationLevel.Information;

    /// <summary>
    /// Joins the user's active notification thread with the same key instead of starting a new one.
    /// </summary>
    public string? GroupKey { get; init; }

    /// <summary>
    /// Continues a thread that this sink returned earlier, when that thread is still active.
    /// </summary>
    public Guid? ThreadId { get; init; }
}

/// <summary>Where a delivered message landed.</summary>
/// <param name="ThreadId">The notification thread, to pass as <see cref="NhUserNotificationRequest.ThreadId"/> for the next message.</param>
public sealed record NhUserNotificationReceipt(Guid ThreadId);

/// <summary>
/// Tells a user something inside the application. The NewHeap notifications module stores the
/// message as a user notification; an application can route messages to its own inbox by
/// registering a sink with <see cref="NhActorServiceCollectionExtensions.AddNewHeapUserNotificationSink{TSink}"/>.
/// </summary>
public interface INhUserNotificationSink
{
    /// <summary>
    /// Delivers <paramref name="request"/>. A succeeded result without data means that the sink
    /// delivers nothing, for example because no notification store is configured.
    /// </summary>
    Task<TaskResult<NhUserNotificationReceipt>> SendAsync(
        NhUserNotificationRequest request,
        CancellationToken cancellationToken = default);
}
