using System.ComponentModel.DataAnnotations;

namespace SampleProjectManagement.Api.Models;

/// <summary>
/// SPM-268: a reply the messaging provider delivers to <c>POST approval-replies</c>.
/// </summary>
public sealed class ApprovalReplyMutateModel
{
    /// <summary>
    /// The provider's id of the delivery; a delivery repeated with the same answer returns the
    /// earlier decision.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string DeliveryId { get; set; } = "";

    /// <summary>
    /// The reply token of the message the person answered.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";

    /// <summary>
    /// The answer: yes or approve, no or reject.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Text { get; set; } = "";
}

/// <summary>
/// SPM-268: the decision a reply made, or the earlier one it repeated.
/// </summary>
/// <param name="ApprovalId">The proposal.</param>
/// <param name="Status">The proposal's status after the decision.</param>
/// <param name="Decision">approve or reject.</param>
/// <param name="Duplicate">Whether the reply repeated the person's earlier decision.</param>
public sealed record ApprovalReplyViewModel(Guid ApprovalId, string Status, string Decision, bool Duplicate);

/// <summary>
/// SPM-268: why a reply decided nothing, as a stable code such as
/// <c>assistant-four-eyes-required</c> or <c>sample-approval-reply-invalid</c>.
/// </summary>
public sealed record ApprovalReplyErrorViewModel(string Code);
