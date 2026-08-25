using System.ComponentModel.DataAnnotations;

namespace SampleProjectManagement.Api.Models;

public sealed class SendSampleMailMutateModel
{
    public string To { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ProjectUrl { get; set; } = "";
}

public sealed class ProjectAssignmentMailViewModel
{
    public string Language { get; set; } = "nl";
    public string Title { get; set; } = "";
    public string Introduction { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ProjectUrl { get; set; } = "";
    public string ActionLabel { get; set; } = "";
}

public sealed class CreateSampleNotificationMutateModel
{
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
    public string ProjectKey { get; set; } = "";
    public string Email { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed record JobSampleResult(string JobId, string Queue, DateTimeOffset? Cutoff = null);

public sealed class ProjectPortfolioAnalysisMutateModel
{
    [Required, StringLength(100)]
    public string IdempotencyKey { get; set; } = "";

    [Range(1, 50)]
    public int Passes { get; set; } = 10;

    [Range(0, 1000)]
    public int DelayPerItemMilliseconds { get; set; } = 50;

    public bool FailFirstAttempt { get; set; }
}

public sealed class ProjectAiPortfolioReportMutateModel
{
    [Required, StringLength(100), RegularExpression("^[A-Za-z0-9._:-]+$")]
    public string IdempotencyKey { get; set; } = "";

    public DateTimeOffset ApprovalExpiresAt { get; set; }
}

public sealed class ProjectAiPortfolioReportApprovalMutateModel
{
    public Guid ApprovalId { get; set; }

    public Guid ProposalId { get; set; }

    [Required, RegularExpression("^[a-fA-F0-9]{64}$")]
    public string ProposalHash { get; set; } = "";

    public bool Approved { get; set; }

    [Required, StringLength(100), RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    public string DecisionCode { get; set; } = "";
}
