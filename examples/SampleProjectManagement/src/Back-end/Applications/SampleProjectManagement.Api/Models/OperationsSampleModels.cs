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
