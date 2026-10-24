using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Enforces the locks of agent packages on every save path (PK9): a save may keep a locked field's
/// value or take the package's, never another (<c>{field}: locked</c>); a part with locks cannot be
/// removed and an agent that belongs to a package cannot be deleted or reset
/// (<c>409 assistant-package-locked</c>) until an administrator unlinks it. Operations stay free.
/// </summary>
internal sealed class NhAssistantPackageLockCheck(NhAssistantAgentPackageStore store)
{
    /// <summary>
    /// The field errors of a change of a linked part whose locked fields would take another value,
    /// or <see langword="null"/> when the part is not linked or the change keeps its locks.
    /// <paramref name="before"/> is the part as stored, <see langword="null"/> for a new one.
    /// </summary>
    public async Task<TaskResult?> CheckChangeAsync(
        string agentId,
        string partKind,
        string partKey,
        IReadOnlyDictionary<string, string>? before,
        IReadOnlyDictionary<string, string> after,
        CancellationToken cancellationToken)
    {
        var link = await store.FindLinkAsync(agentId, partKind, partKey, cancellationToken);
        return link is null ? null : Check(link, before, after);
    }

    /// <summary>
    /// The field errors of a change of a part with <paramref name="link"/>.
    /// </summary>
    public static TaskResult? Check(
        AssistantAgentPackageLink link,
        IReadOnlyDictionary<string, string>? before,
        IReadOnlyDictionary<string, string> after)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(after);
        var locked = NhAssistantAgentPackageStore.LockedValuesOf(link);
        if (locked.Count == 0)
        {
            return null;
        }

        var errors = new NhAssistantValidationErrors();
        foreach (var (field, packageValue) in locked.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var next = after.GetValueOrDefault(field) ?? "null";
            var current = before?.GetValueOrDefault(field) ?? "null";
            if (!string.Equals(next, current, StringComparison.Ordinal)
                && !string.Equals(next, packageValue, StringComparison.Ordinal))
            {
                errors.Add(field, NhAssistantFieldErrors.Locked);
            }
        }

        return errors.IsEmpty ? null : errors.ToResult();
    }

    /// <summary>
    /// <c>assistant-package-locked</c> when the part has locks, so it cannot be removed; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public async Task<TaskResult?> CheckRemovalAsync(
        string agentId,
        string partKind,
        string partKey,
        CancellationToken cancellationToken)
    {
        var link = await store.FindLinkAsync(agentId, partKind, partKey, cancellationToken);
        return link is not null && NhAssistantAgentPackageStore.LockedFieldsOf(link).Count > 0
            ? Locked()
            : null;
    }

    /// <summary>
    /// <c>assistant-package-locked</c> when the agent belongs to a package or has a linked part, so it
    /// cannot be deleted; otherwise <see langword="null"/>.
    /// </summary>
    public async Task<TaskResult?> CheckDeletionAsync(string agentId, CancellationToken cancellationToken)
    {
        return await store.IsLinkedAsync(agentId, cancellationToken) ? Locked() : null;
    }

    /// <summary>
    /// <c>assistant-package-locked</c> when a reset would give a locked field of the agent's definition
    /// another value than the package's; otherwise <see langword="null"/>.
    /// </summary>
    public async Task<TaskResult?> CheckResetAsync(
        string agentId,
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after,
        CancellationToken cancellationToken)
    {
        var link = await store.FindLinkAsync(agentId, NhAssistantPackagePartKinds.Agent, string.Empty, cancellationToken);
        return link is not null && Check(link, before, after) is not null ? Locked() : null;
    }

    /// <summary>
    /// The agent's linked parts; empty for an agent no package links.
    /// </summary>
    public Task<IReadOnlyList<AssistantAgentPackageLink>> LinksOfAsync(string agentId, CancellationToken cancellationToken)
    {
        return store.ListLinksAsync(agentId, cancellationToken);
    }

    public static TaskResult Locked()
    {
        return TaskResult.Failed(
            NhAssistantErrorCodes.PackageLocked,
            "An agent package locks the part; unlink it first.");
    }
}
