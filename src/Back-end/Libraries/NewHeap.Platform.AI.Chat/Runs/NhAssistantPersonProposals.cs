using System.Security.Cryptography;
using System.Text;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// The proposals the application records for a person without a run (FB21,
/// <c>INhAssistantProposals.ProposeForAsync</c>): deferred, in the agent's thread at the subject, with the
/// person as the initiator and allowed by that person's own rights. Such a proposal has no run, no
/// consultation and was not placed from another thread; no other path records a deferred proposal like
/// that, so the execution recognizes it by these facts and runs it with the person's rights only, never
/// the agent's grants or the rights of the account the agent answers to.
/// </summary>
internal static class NhAssistantPersonProposals
{
    /// <summary>
    /// Whether <paramref name="approval"/> is a proposal the application recorded for a person.
    /// </summary>
    public static bool IsPersonProposal(AssistantApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return approval.RunId is null
            && approval.ConsultationId is null
            && approval.PlacedFromConversationId is null
            && string.Equals(approval.Mode, NhAssistantApprovalModes.Deferred, StringComparison.Ordinal)
            && approval.GrantSource == NhAiGrantSource.Delegated;
    }

    /// <summary>
    /// The id of the proposal that the application's idempotency key records for the person and the
    /// agent in the tenant: the same key always names the same proposal, so a repeat finds it and two
    /// calls that race record it once. The key never leaves the hash.
    /// </summary>
    public static Guid ApprovalIdOf(string? tenantId, string agentId, string actorId, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var material = string.Join('\n', "nh-assistant:proposal-for:v1", tenantId ?? string.Empty, agentId, actorId, idempotencyKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        var bytes = hash.AsSpan(0, 16).ToArray();
        // A name-based UUID (version 8, RFC 9562 variant), never equal to a random version 4 id.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
