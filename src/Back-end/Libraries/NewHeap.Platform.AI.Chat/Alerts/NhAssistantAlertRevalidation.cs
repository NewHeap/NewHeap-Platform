namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// What a kind's check says about one open alert when the application revalidates its subject.
/// </summary>
public enum NhAssistantAlertValidity
{
    /// <summary>The alert still applies and stays open.</summary>
    StillValid = 0,

    /// <summary>The alert's reason is gone: it is resolved, and its deliveries that did not go out are taken back.</summary>
    Resolve = 1
}

/// <summary>
/// The open alert a kind's check looks at: its identity and the parameters it was raised with, never a
/// person. The parameters are the application's data; a check keeps them out of its logs.
/// </summary>
/// <param name="AlertId">The open alert.</param>
/// <param name="KindId">Its kind.</param>
/// <param name="Subject">The object it is about.</param>
/// <param name="Key">Its key.</param>
/// <param name="Parameters">The named values it was last raised with.</param>
/// <param name="RaisedAt">When it was first raised, on the application's clock.</param>
/// <param name="Shadow">A shadow alert, which reaches nobody.</param>
public sealed record NhAssistantAlertCheckContext(
    Guid AlertId,
    string KindId,
    NhAiSubjectRef Subject,
    string Key,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset RaisedAt,
    bool Shadow)
{
    /// <summary>
    /// Writes the identity only: the key and parameters are the application's data and stay out of logs.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("AlertId = ").Append(AlertId).Append(", KindId = ").Append(KindId).Append(", Subject = ").Append(Subject);
        return true;
    }
}

/// <summary>
/// The application's check of the open alerts of one kind (FB87-10), declared in code with
/// <see cref="NhAssistantAlertKindBuilder.Revalidate{TCheck}"/> and resolved from the application's
/// services: when the application calls <see cref="INhAssistantAlerts.RevalidateAsync"/> for a subject, it
/// says per open alert whether its reason still holds, as a proposal guard says whether a proposal still
/// fits. It reads the application's state as it is now and changes nothing. An exception leaves the alert
/// open and is logged with its type only; the other alerts are still checked.
/// </summary>
public interface INhAssistantAlertCheck
{
    ValueTask<NhAssistantAlertValidity> CheckAsync(
        NhAssistantAlertCheckContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An open alert that a revalidation resolved.
/// </summary>
/// <param name="AlertId">The alert.</param>
/// <param name="KindId">Its kind.</param>
/// <param name="Key">Its key.</param>
/// <param name="Shadow">A shadow alert, which reached nobody.</param>
public sealed record NhAssistantResolvedAlert(Guid AlertId, string KindId, string Key, bool Shadow)
{
    /// <summary>
    /// Writes the identity only: the key is the application's data and stays out of logs.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("AlertId = ").Append(AlertId).Append(", KindId = ").Append(KindId).Append(", Shadow = ").Append(Shadow);
        return true;
    }
}

/// <summary>
/// What a revalidation of one subject's open alerts did.
/// </summary>
/// <param name="Resolved">The alerts it resolved, because their kind's check said so.</param>
/// <param name="Checked">The open alerts whose kind's check ran.</param>
/// <param name="Failed">The alerts whose check failed; they stay open.</param>
/// <param name="HasMore">
/// More open alerts with a check were about the subject than one call checks
/// (<see cref="NhAssistantAlertLimits.MaxRevalidatedAlerts"/>); the newest of them were not checked.
/// </param>
public sealed record NhAssistantAlertRevalidationResult(
    IReadOnlyList<NhAssistantResolvedAlert> Resolved,
    int Checked,
    int Failed,
    bool HasMore)
{
    public static NhAssistantAlertRevalidationResult Empty { get; } = new([], 0, 0, false);
}
