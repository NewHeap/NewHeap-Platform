using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Who takes part in the work of the whole application (<see cref="NhAiSubjectRef.SystemType"/>), whose
/// threads, runs and proposals are stored without a tenant. A caller without a tenant always does. With
/// <see cref="NhAssistantBuilder.UseSingleTenant"/> a caller of the configured tenant does as well; a
/// caller of any other tenant never does, with or without the mode, neither in listings nor by id. The
/// rule only says who may take part: the read and approve
/// policies of the whole application and the agent's run and view policies still decide what such a
/// caller sees and decides.
/// </summary>
internal static class NhAssistantWholeApplicationTenancy
{
    /// <summary>
    /// Whether a caller bound to <paramref name="tenantId"/> takes part in the whole application's
    /// work: without a tenant always, with the single-tenant mode also as a caller of its tenant.
    /// </summary>
    public static bool TakesPart(NhAssistantRegistrationState registration, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return tenantId is null
            || (registration.SingleTenantId is { } single && string.Equals(tenantId, single, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a caller is kept out of the whole application's work where it is reached by id, such as
    /// deciding a proposal, reading its decision facts, starting or opening an agent's thread and the
    /// thread itself: a caller bound to a tenant, unless the single-tenant mode names exactly that
    /// tenant. Without the mode only callers without a tenant, such as the host's operators, take part.
    /// </summary>
    public static bool KeepsOut(NhAssistantRegistrationState registration, string? tenantId)
    {
        return !TakesPart(registration, tenantId);
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
    /// Whether the caller is kept from the whole application's thread: bound to a tenant that the
    /// single-tenant mode does not name. A thread of the application's own subjects is never kept out
    /// here; its tenant and resolver decide.
    /// </summary>
    public static bool KeepsOut(NhAssistantRegistrationState registration, AssistantThread thread, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return IsWholeApplication(thread.SubjectType) && KeepsOut(registration, tenantId);
    }

    /// <summary>
    /// Whether the caller is kept from the subject: the whole application, for a caller bound to a
    /// tenant that the single-tenant mode does not name.
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
