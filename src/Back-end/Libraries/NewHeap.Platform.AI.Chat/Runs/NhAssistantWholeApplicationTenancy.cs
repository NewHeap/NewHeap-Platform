using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Who takes part in the work of the whole application (<see cref="NhAiSubjectRef.SystemType"/>), whose
/// threads, runs and proposals are stored without a tenant. A caller without a tenant always does. With
/// <see cref="NhAssistantBuilder.UseSingleTenant"/> a caller of the configured tenant does as well; a
/// caller of any other tenant never does. The rule only says who may take part: the read and approve
/// policies of the whole application and the agent's run and view policies still decide what such a
/// caller sees and decides.
/// </summary>
internal static class NhAssistantWholeApplicationTenancy
{
    /// <summary>
    /// Whether a caller bound to <paramref name="tenantId"/> takes part in the whole application's
    /// work: without a tenant always, with the single-tenant mode also as a caller of its tenant. The
    /// checks that kept every caller with a tenant out before the mode existed ask this.
    /// </summary>
    public static bool TakesPart(NhAssistantRegistrationState registration, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return tenantId is null
            || (registration.SingleTenantId is { } single && string.Equals(tenantId, single, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether the single-tenant mode keeps a caller out of the whole application's work in the checks
    /// that compared no tenant before the mode existed, such as deciding a proposal by its id, starting
    /// an agent by hand and the agent's thread: with the mode a caller of another tenant, without it
    /// nobody, so those checks keep their behavior in a host without the mode.
    /// </summary>
    public static bool KeepsOut(NhAssistantRegistrationState registration, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return registration.SingleTenantId is not null && !TakesPart(registration, tenantId);
    }

    /// <summary>
    /// Whether a caller bound to <paramref name="tenantId"/> reaches something stored under
    /// <paramref name="storedTenantId"/> about a subject of <paramref name="subjectType"/>: the same
    /// tenant, or the whole application's work, stored without a tenant, for a caller who
    /// <see cref="TakesPart"/>.
    /// </summary>
    public static bool Reaches(
        NhAssistantRegistrationState registration,
        string? storedTenantId,
        string subjectType,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (string.Equals(storedTenantId, tenantId, StringComparison.Ordinal))
        {
            return true;
        }

        return storedTenantId is null
            && string.Equals(subjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal)
            && TakesPart(registration, tenantId);
    }

    /// <summary>
    /// Whether a caller bound to <paramref name="tenantId"/> reaches the agent thread: its own tenant's,
    /// or a thread of the whole application when the caller <see cref="TakesPart"/>.
    /// </summary>
    public static bool Reaches(NhAssistantRegistrationState registration, AssistantThread thread, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return Reaches(registration, thread.TenantId, thread.SubjectType, tenantId);
    }

    /// <summary>
    /// Whether the single-tenant mode keeps the caller from the whole application's thread
    /// (<see cref="KeepsOut"/>); a thread of the application's own subjects is never kept out here.
    /// </summary>
    public static bool KeepsOut(NhAssistantRegistrationState registration, AssistantThread thread, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return IsWholeApplication(thread.SubjectType) && KeepsOut(registration, tenantId);
    }

    /// <summary>
    /// Whether the single-tenant mode keeps the caller from the subject: the whole application, for a
    /// caller of another tenant (<see cref="KeepsOut"/>).
    /// </summary>
    public static bool KeepsOut(NhAssistantRegistrationState registration, NhAiSubjectRef subject, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return IsWholeApplication(subject.Type) && KeepsOut(registration, tenantId);
    }

    private static bool IsWholeApplication(string subjectType)
    {
        return string.Equals(subjectType, NhAiSubjectRef.SystemType, StringComparison.Ordinal);
    }
}
