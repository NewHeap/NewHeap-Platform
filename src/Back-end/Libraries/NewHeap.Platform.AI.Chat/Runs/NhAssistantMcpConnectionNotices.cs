using System.Text.Json;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// A personal MCP server a run left out, as the run stores it: the server, its display name when the
/// run left it out, and the skip code.
/// </summary>
internal sealed record NhAssistantRunMcpSkip(string ServerId, string DisplayName, string Code);

/// <summary>
/// The personal MCP servers a run left out (<see cref="AssistantRun.McpConnectionSkips"/>), stored as a
/// JSON array with one entry per server and its latest skip code.
/// </summary>
internal static class NhAssistantRunMcpSkips
{
    /// <summary>The most servers a run records: an agent has at most this many.</summary>
    public const int MaxEntries = 32;

    /// <summary>The longest stored value: 32 entries of a 40-character id, a 200-character name and a code.</summary>
    public const int MaxLength = 16_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Whether a skip code concerns a person's connection: the person's consent to background use or
    /// connection is missing or ended, or the run acts for someone other than the person who asked.
    /// </summary>
    public static bool IsPersonal(string? code)
    {
        return code is NhAssistantAdminErrorCodes.McpConsentRequired
            or NhAssistantAdminErrorCodes.McpConnectionRequired
            or NhAssistantAdminErrorCodes.McpReauthorizationRequired
            or NhAssistantAdminErrorCodes.McpInitiatorOnly;
    }

    /// <summary>
    /// Whether the person whose consent or connection is missing is told. A run that acts for someone
    /// other than the person who asked (<see cref="NhAssistantAdminErrorCodes.McpInitiatorOnly"/>) is
    /// recorded only: no consent or connection would let it use the server.
    /// </summary>
    public static bool Notifies(string? code)
    {
        return code is NhAssistantAdminErrorCodes.McpConsentRequired
            or NhAssistantAdminErrorCodes.McpConnectionRequired
            or NhAssistantAdminErrorCodes.McpReauthorizationRequired;
    }

    /// <summary>The entries of a stored value; empty for <see langword="null"/>.</summary>
    /// <exception cref="JsonException">The stored value is not what this class writes.</exception>
    public static IReadOnlyList<NhAssistantRunMcpSkip> Read(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return [];
        }

        return JsonSerializer.Deserialize<NhAssistantRunMcpSkip[]>(stored, Json)
            ?? throw new JsonException("The MCP skips of a run are not a JSON array.");
    }

    /// <summary>
    /// The stored value with <paramref name="skips"/> added: one entry per server, a later code
    /// replacing an earlier one, at most <see cref="MaxEntries"/>.
    /// </summary>
    public static string Merge(string? stored, IEnumerable<NhAssistantRunMcpSkip> skips)
    {
        var entries = Read(stored).ToList();
        foreach (var skip in skips)
        {
            var index = entries.FindIndex(entry => string.Equals(entry.ServerId, skip.ServerId, StringComparison.Ordinal));
            if (index >= 0)
            {
                entries[index] = skip;
            }
            else if (entries.Count < MaxEntries)
            {
                entries.Add(skip);
            }
        }

        return JsonSerializer.Serialize(entries, Json);
    }
}

/// <summary>
/// The person a notice about a missing MCP connection is for, and the server it names.
/// </summary>
internal sealed record NhAssistantMcpConnectionNotice(string ServerId, string ServerDisplayName, string PersonActorId);

/// <summary>
/// Records on a run the personal MCP servers its turn left out, and tells the person whose consent to
/// background use or connection is missing through the agent notifications
/// (<see cref="NhAssistantAgentNotificationKind.McpConnectionMissing"/>): at most once per person and
/// server within <see cref="NoticeInterval"/>, also across instances, and never for shadow work. A run
/// that acts for someone other than the person who asked is recorded only.
/// </summary>
internal sealed class NhAssistantMcpConnectionNotices(
    NhAssistantRunStore runs,
    NhAssistantNotificationStore notices,
    NhAssistantAgentNotifier notifier,
    NhAssistantRegistrationState registration)
{
    /// <summary>How long a person is told only once that a server needs their consent or connection.</summary>
    public static readonly TimeSpan NoticeInterval = TimeSpan.FromDays(1);

    /// <summary>How long a notice is kept: longer than <see cref="NoticeInterval"/>, so maintenance can remove older ones.</summary>
    public static readonly TimeSpan NoticeRetention = TimeSpan.FromDays(7);

    public async Task RecordAsync(
        Guid runId,
        AssistantThread thread,
        IReadOnlyList<NhAssistantMcpMissingConnection> missing,
        CancellationToken cancellationToken)
    {
        if (missing.Count == 0)
        {
            return;
        }

        await runs.AddMcpConnectionSkipsAsync(
            runId,
            [.. missing.Select(item => new NhAssistantRunMcpSkip(item.ServerId, item.DisplayName, item.Code))],
            cancellationToken);
        var run = await runs.FindRunAsync(runId, cancellationToken);
        if (run is null || run.Shadow || thread.Shadow
            || (registration.AgentRuns.TryGetValue(thread.AgentId, out var options) && options.ShadowOfAgentId is not null))
        {
            // Shadow work notifies nobody.
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var item in missing)
        {
            if (item.PersonActorId is not { } person || !NhAssistantRunMcpSkips.Notifies(item.Code))
            {
                continue;
            }
            if (!await notices.TryClaimMcpConnectionNoticeAsync(item.ServerId, person, item.Code, now, now - NoticeInterval, cancellationToken))
            {
                continue;
            }

            await notifier.NotifyAsync(
                NhAssistantAgentNotificationKind.McpConnectionMissing,
                thread,
                runId,
                null,
                run.InitiatorActorId,
                run.Shadow,
                CancellationToken.None,
                failureCode: item.Code,
                mcpConnection: new NhAssistantMcpConnectionNotice(item.ServerId, item.DisplayName, person));
        }
    }
}
