using System.ComponentModel.DataAnnotations;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Models;

/// <summary>
/// SPM-364: a person's request on the project page for another project status, which a second person approves.
/// </summary>
public sealed class ProjectStatusRequestMutateModel
{
    /// <summary>
    /// The status the person asks for.
    /// </summary>
    [Required]
    [EnumDataType(typeof(ProjectStatus))]
    public ProjectStatus Status { get; set; }

    /// <summary>
    /// Why, in the person's own words; the approvers read it.
    /// </summary>
    [MaxLength(SampleProjectStatusRequests.MaxReasonLength)]
    public string? Reason { get; set; }

    /// <summary>
    /// The page's id of this request: a request the page sends twice records one proposal.
    /// </summary>
    [Required]
    [MaxLength(64)]
    [RegularExpression("^[A-Za-z0-9_.:-]+$")]
    public string RequestId { get; set; } = "";
}

/// <summary>
/// SPM-364: the proposal a status request recorded, or the one that made it unnecessary.
/// </summary>
/// <param name="Outcome">created, already-open or recently-rejected.</param>
/// <param name="ApprovalId">The proposal, which waits in the intake reviewer's thread at the project.</param>
/// <param name="ConversationId">The intake reviewer's thread at the project.</param>
/// <param name="Status">The proposal's status, such as pending.</param>
/// <param name="PolicyId">The approval policy the proposal waits under.</param>
/// <param name="FourEyes">Whether the person who asked is kept from approving it.</param>
/// <param name="ExpiresAt">When the proposal expires.</param>
/// <param name="Repeated">Whether the request id recorded the proposal before.</param>
public sealed record ProjectStatusRequestViewModel(
    string Outcome,
    Guid? ApprovalId,
    Guid? ConversationId,
    string? Status,
    string? PolicyId,
    bool FourEyes,
    DateTimeOffset? ExpiresAt,
    bool Repeated);

/// <summary>
/// SPM-364: why a status request recorded nothing, as a stable code such as
/// <c>assistant-subject-access-denied</c> or <c>ai-tool-agent-not-granted</c>.
/// </summary>
public sealed record ProjectStatusRequestErrorViewModel(string Code);
