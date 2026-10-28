using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Applies the tenant rule of agent work (<see cref="NhAssistantWholeApplicationTenancy"/>) for every
/// path that reaches a thread or a subject, and makes one of its refusals visible to operators: a caller
/// without a tenant who addresses work stored under a tenant. That caller reads the work as unknown, as a
/// caller of another tenant does, so the answer stays the same. It is nearly always an application that
/// passed no tenant for the person (<c>NhAssistantDecidingPerson.TenantId</c>) or a context resolver
/// that gives none, so the refusal is logged as a warning, at most once per <see cref="WarningInterval"/>
/// for each path. The warning names the path and the subject type, never an id, tenant or person.
/// </summary>
internal sealed class NhAssistantTenancy(NhAssistantRegistrationState registration, ILogger<NhAssistantTenancy> logger)
{
    /// <summary>
    /// How long one path stays quiet after its warning, so a job that repeats the call for many people
    /// does not fill the log.
    /// </summary>
    public static readonly TimeSpan WarningInterval = TimeSpan.FromHours(1);

    private static readonly Action<ILogger, string, string, Exception?> TenantlessCallerRefused =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, "AssistantTenantlessCallerRefused"),
            "{Path} refused agent work about a subject of type {SubjectType} stored under a tenant, because the caller has no tenant. "
            + "The caller reads the work as unknown. Pass the person's tenant (NhAssistantDecidingPerson.TenantId) or let the context resolver give it.");

    private readonly ConcurrentDictionary<string, long> _warnedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a caller bound to <paramref name="tenantId"/> reaches the agent thread: its own tenant's,
    /// or a thread of the whole application when the caller takes part in it. Every path that reaches a
    /// thread by id, or a run or proposal through its thread, applies it before any read, decision or
    /// participant row; a thread it does not reach answers like an unknown one.
    /// </summary>
    public bool Reaches(
        AssistantThread thread,
        string? tenantId,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "")
    {
        ArgumentNullException.ThrowIfNull(thread);
        return Reaches(thread.TenantId, thread.SubjectType, tenantId, member, file);
    }

    /// <summary>
    /// Whether a caller bound to <paramref name="tenantId"/> reaches the subject as its resolver
    /// described it: a subject of the caller's own tenant, or the whole application when the caller takes
    /// part in it. Every path that addresses a subject applies it before any thread, run, proposal,
    /// count, consultation or participant row; a subject it does not reach reads as unknown.
    /// </summary>
    public bool Reaches(
        NhAiSubjectInfo subject,
        string? tenantId,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "")
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Reaches(subject.TenantId, subject.Subject.Type, tenantId, member, file);
    }

    /// <summary>
    /// Whether a caller bound to <paramref name="tenantId"/> reaches something stored under
    /// <paramref name="storedTenantId"/> about a subject of <paramref name="subjectType"/>.
    /// </summary>
    public bool Reaches(
        string? storedTenantId,
        string subjectType,
        string? tenantId,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "")
    {
        if (NhAssistantWholeApplicationTenancy.Reaches(registration, storedTenantId, subjectType, tenantId))
        {
            return true;
        }

        // A caller of another tenant is the isolation working as intended; only a caller without a
        // tenant is worth an operator's attention.
        if (tenantId is null && storedTenantId is not null)
        {
            Warn(Path.GetFileNameWithoutExtension(file) + "." + member, subjectType);
        }

        return false;
    }

    /// <summary>
    /// Whether a listing for a caller bound to <paramref name="tenantId"/> includes the agent thread, by
    /// the same rule as <see cref="Reaches(AssistantThread, string?, string, string)"/>. A listing leaves
    /// out other tenants' work as a matter of course, also for operators without a tenant, so it warns
    /// about nothing; a path that addresses one thread or subject does.
    /// </summary>
    public bool Includes(AssistantThread thread, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return NhAssistantWholeApplicationTenancy.Reaches(registration, thread.TenantId, thread.SubjectType, tenantId);
    }

    /// <summary>
    /// Whether a listing for a caller bound to <paramref name="tenantId"/> includes the subject as its
    /// resolver described it, without a warning; see <see cref="Includes(AssistantThread, string?)"/>.
    /// </summary>
    public bool Includes(NhAiSubjectInfo subject, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return NhAssistantWholeApplicationTenancy.Reaches(registration, subject.TenantId, subject.Subject.Type, tenantId);
    }

    private void Warn(string path, string subjectType)
    {
        // Operational throttling follows the system's monotonic clock, not the application's.
        var now = Environment.TickCount64;
        if (_warnedAt.TryGetValue(path, out var warnedAt) && now - warnedAt < (long)WarningInterval.TotalMilliseconds)
        {
            return;
        }

        _warnedAt[path] = now;
        TenantlessCallerRefused(logger, path, subjectType, null);
    }
}
