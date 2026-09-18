using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

public sealed record ProjectSearchInput(string Query);

public sealed record ProjectSearchItem(Guid Id, string Name);

public sealed record ProjectStatusInput(Guid ProjectId, string Status);

public sealed record ProjectStatusReport(Guid ProjectId, string Status);

/// <summary>
/// Records what the test tools executed. A governed mutation must never execute before approval.
/// </summary>
public sealed class TestProjectToolRecorder
{
    public ConcurrentQueue<ProjectStatusInput> StatusChanges { get; } = new();

    public ConcurrentQueue<NhAiInvocationContext> Contexts { get; } = new();

    public string SearchResultName { get; set; } = "Roadmap project";
}

/// <summary>
/// A hand-written shared-invoker catalog equivalent to generated NewHeap tool catalogs.
/// </summary>
public sealed class TestProjectToolCatalog : INhAiToolCatalog
{
    public const string ReadPolicy = "projects.read";
    public const string ManagePolicy = "projects.manage";

    public static readonly NhAiToolDescriptor Search = new(
        "projects.search",
        1,
        "Search projects by name.",
        typeof(ProjectSearchInput),
        typeof(IReadOnlyList<ProjectSearchItem>),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        true,
        [ReadPolicy])
    {
        ExportName = "projects_search_v1",
        CatalogId = "projects",
        ContractHash = new string('1', 64),
        SchemaHash = new string('1', 64)
    };

    public static readonly NhAiToolDescriptor ChangeStatus = new(
        "projects.change-status",
        1,
        "Change the status of one project after approval.",
        typeof(ProjectStatusInput),
        typeof(ProjectStatusReport),
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        true,
        [ManagePolicy])
    {
        ExportName = "projects_change_status_v1",
        CatalogId = "projects",
        ContractHash = new string('2', 64),
        SchemaHash = new string('2', 64),
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required
    };

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [Search, ChangeStatus];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "projects",
        1,
        new string('3', 64),
        [
            new NhAiToolManifestEntry(Search.Id, 1, Search.SchemaHash, Search.ContractHash) { ExportName = Search.ExportName },
            new NhAiToolManifestEntry(ChangeStatus.Id, 1, ChangeStatus.SchemaHash, ChangeStatus.ContractHash) { ExportName = ChangeStatus.ExportName }
        ]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var recorder = services.GetRequiredService<TestProjectToolRecorder>();

        Func<ProjectSearchInput, CancellationToken, Task<TaskResult<IReadOnlyList<ProjectSearchItem>>>> search =
            (input, cancellationToken) => invoker.InvokeAsync(
                Search,
                input,
                (context, _) =>
                {
                    recorder.Contexts.Enqueue(context);
                    IReadOnlyList<ProjectSearchItem> items = [new ProjectSearchItem(Guid.Empty, recorder.SearchResultName)];
                    return Task.FromResult(TaskResult<IReadOnlyList<ProjectSearchItem>>.Succeeded(items));
                },
                cancellationToken);
        Func<ProjectStatusInput, CancellationToken, Task<TaskResult<ProjectStatusReport>>> changeStatus =
            (input, cancellationToken) => invoker.InvokeAsync(
                ChangeStatus,
                input,
                (context, _) =>
                {
                    recorder.Contexts.Enqueue(context);
                    recorder.StatusChanges.Enqueue(input);
                    return Task.FromResult(TaskResult<ProjectStatusReport>.Succeeded(
                        new ProjectStatusReport(input.ProjectId, input.Status)));
                },
                cancellationToken);

        return
        [
            NhAiGovernedAIFunction.Create(
                Search,
                AIFunctionFactory.Create(search, new AIFunctionFactoryOptions { Name = Search.ExportName, Description = Search.Description })),
            NhAiGovernedAIFunction.Create(
                ChangeStatus,
                AIFunctionFactory.Create(changeStatus, new AIFunctionFactoryOptions { Name = ChangeStatus.ExportName, Description = ChangeStatus.Description }))
        ];
    }
}
