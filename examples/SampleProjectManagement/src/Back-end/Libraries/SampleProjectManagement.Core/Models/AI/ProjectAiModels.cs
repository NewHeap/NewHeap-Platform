namespace SampleProjectManagement.Core.Models.AI;

using SampleProjectManagement.DAL.Entities;

public sealed record ProjectAiSearchInput(string? Query, int Limit = 10);

public sealed record ProjectAiSearchItem(Guid Id, string Key, string Name);

public sealed record ProjectAiStatusChangeInput(
    Guid ProjectId,
    ProjectStatus Status);

public sealed record ProjectAiStatusChangeReport(
    Guid ProjectId,
    ProjectStatus PreviousStatus,
    ProjectStatus CurrentStatus,
    bool Accepted);

public sealed record ProjectAiContextDocument(
    Guid ProjectId,
    string Key,
    string Name,
    string? Description,
    DateTimeOffset LastModifiedAt);
