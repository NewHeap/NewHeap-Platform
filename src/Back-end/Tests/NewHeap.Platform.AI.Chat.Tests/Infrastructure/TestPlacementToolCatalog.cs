using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

public sealed record ProjectCloseInput(Guid ProjectId);

public sealed record ProjectCloseReport(Guid ProjectId, bool Closed);

/// <summary>
/// Records what the placement tool executed and in which invocation context.
/// </summary>
public sealed class TestPlacementRecorder
{
    public ConcurrentQueue<(ProjectCloseInput Input, NhAiInvocationContext Context)> Closed { get; } = new();
}

/// <summary>
/// A hand-built shared-invoker catalog with one tool that declares which argument names its object,
/// as <c>[NhAiSubject("project")]</c> does for a generated tool.
/// </summary>
public sealed class TestPlacementToolCatalog : INhAiToolCatalog
{
    public const string CloseToolId = "projects.close";

    public static readonly NhAiToolDescriptor Close = new(
        CloseToolId,
        1,
        "Close one project after approval.",
        typeof(ProjectCloseInput),
        typeof(ProjectCloseReport),
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        true,
        [TestProjectToolCatalog.ManagePolicy])
    {
        ExportName = "projects_close_v1",
        CatalogId = "project-closing",
        ContractHash = new string('4', 64),
        SchemaHash = new string('4', 64),
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required,
        AllowAgentGrant = true,
        Subject = new NhAiToolSubject("project", "projectId")
    };

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [Close];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "project-closing",
        1,
        new string('5', 64),
        [new NhAiToolManifestEntry(Close.Id, 1, Close.SchemaHash, Close.ContractHash) { ExportName = Close.ExportName }]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var recorder = services.GetRequiredService<TestPlacementRecorder>();
        Func<ProjectCloseInput, CancellationToken, Task<TaskResult<ProjectCloseReport>>> close =
            (input, cancellationToken) => invoker.InvokeAsync(
                Close,
                input,
                (context, _) =>
                {
                    recorder.Closed.Enqueue((input, context));
                    return Task.FromResult(TaskResult<ProjectCloseReport>.Succeeded(new ProjectCloseReport(input.ProjectId, true)));
                },
                cancellationToken);

        return
        [
            NhAiGovernedAIFunction.Create(
                Close,
                AIFunctionFactory.Create(close, new AIFunctionFactoryOptions { Name = Close.ExportName, Description = Close.Description }),
                services)
        ];
    }
}
