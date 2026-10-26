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

    /// <summary>
    /// The person who changed the status, or <see langword="null"/> for an agent or a job.
    /// </summary>
    public Guid? ChangedByUserId { get; init; }

    /// <summary>
    /// SPM-308: the actor of the agent whose tool changed the status, as the tool's invocation
    /// context names it; <see langword="null"/> for a person or a job.
    /// </summary>
    public string? ChangedByAgentActorId { get; init; }

    /// <summary>
    /// SPM-308: the type and id of the subject the agent worked in when its tool changed the status,
    /// which may be another subject than this project.
    /// </summary>
    public string? ChangedInSubjectType { get; init; }

    public string? ChangedInSubjectId { get; init; }

    /// <summary>
    /// SPM-308: the agent run whose tool changed the status, as the tool's invocation context names it
    /// (<c>AgentRunId</c>); <see langword="null"/> for a person, a job or an agent outside a run.
    /// </summary>
    public Guid? ChangedByAgentRunId { get; init; }

    /// <summary>
    /// SPM-342: the assistant conversation whose turn changed the status, as the tool's invocation
    /// context names it (<c>ConversationId</c>); <see langword="null"/> for a person or a job.
    /// </summary>
    public Guid? ChangedInConversationId { get; init; }

    /// <summary>
    /// SPM-342: the application's channel that started that turn, such as <c>messaging-chat</c>
    /// (<c>Channel</c>); <see langword="null"/> for the portal's own turns, runs, a person or a job.
    /// </summary>
    public string? ChangedThroughChannel { get; init; }

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
