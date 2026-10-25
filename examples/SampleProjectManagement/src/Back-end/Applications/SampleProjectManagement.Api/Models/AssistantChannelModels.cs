using System.ComponentModel.DataAnnotations;

namespace SampleProjectManagement.Api.Models;

/// <summary>
/// SPM-332: the chat a signed-in person continues in the messaging app.
/// </summary>
public sealed class AssistantChannelLinkMutateModel
{
    /// <summary>
    /// The person's chat with the project assistant.
    /// </summary>
    [Required]
    public Guid ConversationId { get; set; }
}

/// <summary>
/// SPM-332: a message the messaging provider delivers to <c>POST assistant-channel/messages</c>.
/// </summary>
public sealed class AssistantChannelMessageMutateModel
{
    /// <summary>
    /// The provider's id of the delivery; a delivery repeated with the same id starts no second turn.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string DeliveryId { get; set; } = "";

    /// <summary>
    /// The link token of the chat the person writes in.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";

    /// <summary>
    /// The person's message.
    /// </summary>
    [Required]
    [MaxLength(8_000)]
    public string Text { get; set; } = "";
}

/// <summary>
/// SPM-332: an answer to an approval card the messaging provider delivers to
/// <c>POST assistant-channel/decisions</c>.
/// </summary>
public sealed class AssistantChannelDecisionMutateModel
{
    /// <summary>
    /// The link token of the chat the card belongs to.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";

    /// <summary>
    /// The approval the card shows.
    /// </summary>
    [Required]
    public Guid ApprovalId { get; set; }

    /// <summary>
    /// The proposal hash the card shows; a proposal that changed since is refused.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string ProposalHash { get; set; } = "";

    /// <summary>
    /// The answer: yes or approve, no or reject.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Text { get; set; } = "";
}

/// <summary>
/// SPM-332: the link token the messaging app keeps for the chat, and until when it is valid.
/// </summary>
public sealed record AssistantChannelLinkViewModel(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// SPM-332: what the messaging app shows after a turn.
/// </summary>
/// <param name="Status">completed, waiting-for-approval, cancelled or failed.</param>
/// <param name="Code">The code of a failed turn, or of a turn that reached its tool-call limit.</param>
/// <param name="Text">The assistant's answer.</param>
/// <param name="Approval">The approval card when the turn waits for the person.</param>
public sealed record AssistantChannelAnswerViewModel(
    string Status,
    string? Code,
    string? Text,
    AssistantChannelApprovalViewModel? Approval);

/// <summary>
/// SPM-332: an approval card: the approval, the proposal hash to answer with and what it does.
/// </summary>
public sealed record AssistantChannelApprovalViewModel(Guid ApprovalId, string ProposalHash, string Summary);

/// <summary>
/// SPM-332: why a delivery started or decided nothing, as a stable code such as
/// <c>assistant-message-duplicate</c> or <c>sample-assistant-channel-delivery-invalid</c>.
/// </summary>
public sealed record AssistantChannelErrorViewModel(string Code);
