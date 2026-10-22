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

    /// <summary>
    /// The agent invocation scope that was ambient while the search tool ran, as a tool handler of
    /// the application reads it; <see langword="null"/> outside an agent's turn.
    /// </summary>
    public ConcurrentQueue<NhAiAgentInvocationScope?> Scopes { get; } = new();

    public string SearchResultName { get; set; } = "Roadmap project";

    /// <summary>When set, the search tool throws this exception inside the governed invocation.</summary>
    public Exception? SearchException { get; set; }

    /// <summary>When set, the search tool saves its result as the artifact <c>search-notes</c>.</summary>
    public bool SaveSearchArtifact { get; set; }
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
        SchemaHash = new string('1', 64),
        AllowAgentGrant = true
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
        Idempotency = NhAiIdempotencySupport.Required,
        AllowAgentGrant = true
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
                async (context, token) =>
                {
                    recorder.Contexts.Enqueue(context);
                    recorder.Scopes.Enqueue(NhAiAgentInvocationScopes.Current);
                    if (recorder.SearchException is { } exception)
                    {
                        throw exception;
                    }
                    IReadOnlyList<ProjectSearchItem> items = [new ProjectSearchItem(Guid.Empty, recorder.SearchResultName)];
                    if (recorder.SaveSearchArtifact)
                    {
                        var saved = await services.GetRequiredService<INhAiArtifacts>().SaveAsync(
                            "search-notes",
                            "text/markdown",
                            System.Text.Encoding.UTF8.GetBytes("# " + recorder.SearchResultName),
                            token);
                        if (!saved.Success)
                        {
                            return TaskResult<IReadOnlyList<ProjectSearchItem>>.Failed(saved);
                        }
                    }
                    return TaskResult<IReadOnlyList<ProjectSearchItem>>.Succeeded(items);
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
                AIFunctionFactory.Create(search, new AIFunctionFactoryOptions { Name = Search.ExportName, Description = Search.Description }),
                services),
            NhAiGovernedAIFunction.Create(
                ChangeStatus,
                AIFunctionFactory.Create(changeStatus, new AIFunctionFactoryOptions { Name = ChangeStatus.ExportName, Description = ChangeStatus.Description }),
                services)
        ];
    }
}
