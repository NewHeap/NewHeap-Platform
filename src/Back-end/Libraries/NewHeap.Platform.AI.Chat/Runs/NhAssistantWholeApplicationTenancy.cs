using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// The one tenant rule of agent work. A caller reaches an agent thread, its runs and proposals, and a
/// subject as its resolver describes it, only in the caller's own tenant, by id as in listings, and
/// before anything records the caller as a participant. The work of the whole application
/// (<see cref="NhAiSubjectRef.SystemType"/>) is stored without a tenant: a caller without a tenant
/// always takes part in it. With <see cref="NhAssistantBuilder.UseSingleTenant"/> a caller of the
/// configured tenant does as well; a caller of any other tenant never does, with or without the mode.
/// The rule only says who may take part: the subject access resolver, the read and approve policies of
/// the whole application and the agent's run and view policies still decide what such a caller sees
/// and decides.
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
    /// Whether a caller is kept out of the whole application's work: a caller bound to a tenant, unless
    /// the single-tenant mode names exactly that tenant. Without the mode only callers without a
    /// tenant, such as the host's operators, take part.
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
    /// or a thread of the whole application when the caller <see cref="TakesPart"/>. Every path that
    /// reaches a thread by id, or a run or proposal through its thread, applies it before any read,
    /// decision or participant row; a thread it does not reach answers like an unknown one.
    /// </summary>
    public static bool Reaches(NhAssistantRegistrationState registration, AssistantThread thread, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return Reaches(registration, thread.TenantId, thread.SubjectType, tenantId);
    }

    /// <summary>
    /// Whether a caller bound to <paramref name="tenantId"/> reaches the subject as its resolver
    /// described it: a subject of the caller's own tenant, or the whole application when the caller
    /// <see cref="TakesPart"/>. Every path that addresses a subject applies it before any thread, run,
    /// proposal, count, consultation or participant row; a subject it does not reach reads as unknown.
    /// </summary>
    public static bool Reaches(NhAssistantRegistrationState registration, NhAiSubjectInfo subject, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Reaches(registration, subject.TenantId, subject.Subject.Type, tenantId);
    }
}

/// <summary>
/// The tenant of the person for whom work is done, for a path that binds that work to the person's
/// tenant with <see cref="NhAssistantWholeApplicationTenancy"/>. <see cref="TenantId"/> is
/// <see langword="null"/> for a person without a tenant. A path that gets no binding applies no tenant
/// rule of its own, as the application's events and schedules, which name their subject themselves.
/// </summary>
internal sealed record NhAssistantTenantBinding(string? TenantId)
{
    /// <summary>
    /// Whether the binding reaches the subject as its resolver described it.
    /// </summary>
    public bool Reaches(NhAssistantRegistrationState registration, NhAiSubjectInfo subject)
    {
        return NhAssistantWholeApplicationTenancy.Reaches(registration, subject, TenantId);
    }
}
