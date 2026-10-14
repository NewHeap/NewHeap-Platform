using NewHeap.Platform.Common.Events;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Events;

public sealed class ProjectCreatedEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid ProjectId { get; init; }
    public string ProjectKey { get; init; } = "";

    /// <summary>
    /// The person who created the project, or <see langword="null"/> for an import or a job.
    /// </summary>
    public Guid? CreatedByUserId { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public static string Topic => "sample-project-management.project-created";
}

public sealed class ProjectUpdatedEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid ProjectId { get; init; }
    public string ChangeSet { get; init; } = "project";
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public static string Topic => "sample-project-management.project-updated";
}

public sealed class ProjectStatusChangedEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid ProjectId { get; init; }
    public ProjectStatus PreviousStatus { get; init; }
    public ProjectStatus Status { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public static string Topic => "sample-project-management.project-status-changed";
}

public sealed class ProjectDeletedEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid ProjectId { get; init; }
    public string ProjectKey { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public static string Topic => "sample-project-management.project-deleted";
}

public sealed class ProjectBulkChangedEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Failed { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public static string Topic => "sample-project-management.project-bulk-changed";
}

public sealed class ProjectTaskCreatedEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid ProjectTaskId { get; init; }
    public Guid ProjectId { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public static string Topic => "sample-project-management.project-task-created";
}

public sealed class ProjectTaskUpdatedEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid ProjectTaskId { get; init; }
    public Guid ProjectId { get; init; }

    /// <summary>
    /// The division of the task's project, so consumers can scope what they do, such as revalidating
    /// agent proposals for the task (SPM-282).
    /// </summary>
    public Guid DivisionId { get; init; }

    public bool IsCompleted { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public static string Topic => "sample-project-management.project-task-updated";
}

public sealed class ProjectTaskDeletedEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid ProjectTaskId { get; init; }
    public Guid ProjectId { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public static string Topic => "sample-project-management.project-task-deleted";
}

/// <summary>
/// A request to the review board to review a project.
/// </summary>
public sealed class ProjectReviewRequestedEvent : INhEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public Guid DivisionId { get; init; }
    public Guid ProjectId { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public static string Topic => "sample-project-management.project-review-requested";
}
