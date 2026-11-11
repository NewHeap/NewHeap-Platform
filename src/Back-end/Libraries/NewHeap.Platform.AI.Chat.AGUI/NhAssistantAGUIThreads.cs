using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Platform.AI.Chat.AGUI;

/// <summary>
/// Maps an AG-UI <c>threadId</c> to the conversation it names. A thread id that is a GUID is the
/// conversation's id, as a client that keeps the assistant's conversation ids, or chooses a random UUID per
/// thread, sends it. Any other thread id, such as the <c>thread_…</c> ids an AG-UI client generates, names the
/// conversation whose id is derived from the caller, their tenant, the endpoint's agent and that thread id:
/// the same string from another person or for another agent names another conversation, so a client-chosen
/// string never reaches someone else's conversation. Access is still checked on every request.
/// </summary>
internal static class NhAssistantAGUIThreads
{
    /// <summary>The longest thread id the endpoint accepts.</summary>
    public const int MaxThreadIdLength = 256;

    /// <summary>The longest run id the endpoint accepts and echoes.</summary>
    public const int MaxRunIdLength = 128;

    /// <summary>
    /// The conversation <paramref name="threadId"/> names for <paramref name="caller"/> at the endpoint of
    /// <paramref name="agentId"/>, or <see langword="null"/> for a missing or too long thread id.
    /// </summary>
    public static Guid? ConversationIdOf(string? threadId, NhAiInvocationContext caller, string agentId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (string.IsNullOrWhiteSpace(threadId) || threadId.Length > MaxThreadIdLength)
        {
            return null;
        }
        if (Guid.TryParse(threadId, out var conversationId))
        {
            return conversationId;
        }

        return Derive(threadId, caller.ActorId, caller.TenantId, agentId);
    }

    /// <summary>
    /// The run id the events echo: the client's, or a new one when the request has none. <see langword="null"/>
    /// for a run id that is too long.
    /// </summary>
    public static string? RunIdOf(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return $"run_{Guid.NewGuid():N}";
        }

        return runId.Length > MaxRunIdLength ? null : runId;
    }

    /// <summary>
    /// A name-based GUID (RFC 9562 version 8) from SHA-256 over the parts, each length-prefixed so that no
    /// two different sets of parts produce the same input.
    /// </summary>
    private static Guid Derive(string threadId, string actorId, string? tenantId, string agentId)
    {
        var input = new StringBuilder("newheap-agui-thread");
        foreach (var part in new[] { tenantId ?? string.Empty, actorId, agentId, threadId })
        {
            input.Append('\n').Append(part.Length).Append(':').Append(part);
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString()), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
