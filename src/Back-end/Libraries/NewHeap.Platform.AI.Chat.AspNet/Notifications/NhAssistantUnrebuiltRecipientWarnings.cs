using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.AspNet.Notifications;

/// <summary>
/// Warns when a delivery skips a person because the application's principal factory rebuilt no
/// principal for them (FB89): at most once per person per <see cref="Window"/>, content-free and without
/// the person's actor id, which the assistant keeps out of its logs. Skipping the person is intended for
/// one who may no longer sign in; an application whose actor ids are its own, which the built-in
/// factory does not know, skips everyone silently otherwise. The warning changes nobody's deliveries.
/// The people it warned about are kept in memory only, at most <see cref="MaxPeople"/> of them; while
/// that many were warned about within the window, others are not.
/// </summary>
internal sealed partial class NhAssistantUnrebuiltRecipientWarnings(
    IServiceProvider services,
    ILogger<NhAssistantUnrebuiltRecipientWarnings> logger)
{
    /// <summary>How long a warning about one person stands before the next one.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>The number of people the warnings remember.</summary>
    public const int MaxPeople = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _warnedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// The application's clock (a registered <see cref="TimeProvider"/>, otherwise the system clock), so
    /// that a test moves the window forward instead of waiting.
    /// </summary>
    private readonly TimeProvider _applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// Records that a delivery skipped <paramref name="actorId"/> because the factory rebuilt nobody,
    /// and warns unless it warned about the person within the window.
    /// </summary>
    public void Skipped(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var now = _applicationClock.GetUtcNow();
        lock (_gate)
        {
            if (_warnedAt.TryGetValue(actorId, out var warnedAt) && now - warnedAt < Window)
            {
                return;
            }

            if (!_warnedAt.ContainsKey(actorId) && _warnedAt.Count >= MaxPeople)
            {
                RemoveExpired(now);
                if (_warnedAt.Count >= MaxPeople)
                {
                    return;
                }
            }

            _warnedAt[actorId] = now;
        }

        LogSkipped(logger);
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (actorId, warnedAt) in _warnedAt.ToArray())
        {
            if (now - warnedAt >= Window)
            {
                _warnedAt.Remove(actorId);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The application's principal factory rebuilt no principal for a recipient, so the assistant delivered nothing to that person. "
            + "That is intended for a person who may no longer sign in; when the application's actor ids are its own, register an INhAiPrincipalFactory "
            + "of its own that rebuilds them. This warning comes at most once per person per hour.")]
    private static partial void LogSkipped(ILogger logger);
}
