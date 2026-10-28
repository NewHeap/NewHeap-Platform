using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

public sealed record ProjectOverviewInput(string Query);

public sealed record ProjectOverviewResult(string Summary);

public sealed record ProjectCloseAllInput(string Reason);

public sealed record ProjectCloseAllResult(int Closed);

public sealed record ScriptRunInput(string Code);

public sealed record ScriptRunResult(string Value);

/// <summary>
/// Counts what the marked test tools ran behind the shared invoker.
/// </summary>
public sealed class TestCompositionToolRecorder
{
    public ConcurrentQueue<string> Ran { get; } = new();
}

/// <summary>
/// Small stand-ins for a composition and the script tool: governed tools that only carry the marker
/// (<see cref="NhAiToolDescriptor.CompositionFeature"/>). The real composition builder and scripting
/// package live in their own lanes; the switch, the ceiling and the enforcement only read the marker.
/// </summary>
public sealed class TestCompositionToolCatalog : INhAiGeneratedToolCatalog
{
    public const string OverviewFunction = "projects_overview_v1";
    public const string ScriptFunction = "scripting_run_v1";

    /// <summary>A read-only composition that the agent's <c>projects.*</c> selector selects.</summary>
    public static readonly NhAiToolDescriptor Overview = new(
        "projects.overview",
        1,
        "Summarize the projects with their risks in one call.",
        typeof(ProjectOverviewInput),
        typeof(ProjectOverviewResult),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp,
        true,
        [TestProjectToolCatalog.ReadPolicy])
    {
        ExportName = OverviewFunction,
        CatalogId = "test-compositions",
        ContractHash = new string('a', 64),
        SchemaHash = new string('a', 64),
        AllowAgentGrant = true,
        CompositionFeature = NhAiCompositionFeature.Compositions
    };

    /// <summary>The script tool, which no tool selector of the test agents names.</summary>
    public static readonly NhAiToolDescriptor Script = new(
        "scripting.run",
        1,
        "Run a short script over the tools of this turn.",
        typeof(ScriptRunInput),
        typeof(ScriptRunResult),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp,
        true,
        [TestProjectToolCatalog.ReadPolicy])
    {
        ExportName = ScriptFunction,
        CatalogId = "test-compositions",
        ContractHash = new string('d', 64),
        SchemaHash = new string('d', 64),
        AllowAgentGrant = true,
        CompositionFeature = NhAiCompositionFeature.Scripts
    };

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [Overview, Script];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "test-compositions",
        1,
        new string('e', 64),
        [
            new NhAiToolManifestEntry(Overview.Id, 1, Overview.SchemaHash, Overview.ContractHash) { ExportName = Overview.ExportName },
            new NhAiToolManifestEntry(Script.Id, 1, Script.SchemaHash, Script.ContractHash) { ExportName = Script.ExportName }
        ]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var recorder = services.GetRequiredService<TestCompositionToolRecorder>();

        Func<ProjectOverviewInput, CancellationToken, Task<TaskResult<ProjectOverviewResult>>> overview =
            (input, cancellationToken) => invoker.InvokeAsync(
                Overview,
                input,
                (_, _) =>
                {
                    recorder.Ran.Enqueue(Overview.Id);
                    return Task.FromResult(TaskResult<ProjectOverviewResult>.Succeeded(new ProjectOverviewResult("Two projects, one risk.")));
                },
                cancellationToken);
        Func<ScriptRunInput, CancellationToken, Task<TaskResult<ScriptRunResult>>> script =
            (input, cancellationToken) => invoker.InvokeAsync(
                Script,
                input,
                (_, _) =>
                {
                    recorder.Ran.Enqueue(Script.Id);
                    return Task.FromResult(TaskResult<ScriptRunResult>.Succeeded(new ScriptRunResult("ran")));
                },
                cancellationToken);

        return
        [
            NhAiGovernedAIFunction.Create(
                Overview,
                AIFunctionFactory.Create(overview, new AIFunctionFactoryOptions { Name = Overview.ExportName, Description = Overview.Description }),
                services),
            NhAiGovernedAIFunction.Create(
                Script,
                AIFunctionFactory.Create(script, new AIFunctionFactoryOptions { Name = Script.ExportName, Description = Script.Description }),
                services)
        ];
    }
}

/// <summary>
/// A composition that changes state and needs approval, in a catalog of its own so that hosts without
/// an idempotency manager can register the read-only stand-ins alone.
/// </summary>
public sealed class TestCompositionMutationCatalog : INhAiGeneratedToolCatalog
{
    public const string CloseAllFunction = "projects_close_all_v1";

    /// <summary>A composition that changes state and needs approval.</summary>
    public static readonly NhAiToolDescriptor CloseAll = new(
        "projects.close-all",
        1,
        "Close every finished project after approval.",
        typeof(ProjectCloseAllInput),
        typeof(ProjectCloseAllResult),
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        true,
        [TestProjectToolCatalog.ManagePolicy])
    {
        ExportName = CloseAllFunction,
        CatalogId = "test-composition-mutations",
        ContractHash = new string('b', 64),
        SchemaHash = new string('b', 64),
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required,
        AllowAgentGrant = true,
        CompositionFeature = NhAiCompositionFeature.Compositions
    };

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [CloseAll];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "test-composition-mutations",
        1,
        new string('f', 64),
        [new NhAiToolManifestEntry(CloseAll.Id, 1, CloseAll.SchemaHash, CloseAll.ContractHash) { ExportName = CloseAll.ExportName }]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var recorder = services.GetRequiredService<TestCompositionToolRecorder>();
        Func<ProjectCloseAllInput, CancellationToken, Task<TaskResult<ProjectCloseAllResult>>> closeAll =
            (input, cancellationToken) => invoker.InvokeAsync(
                CloseAll,
                input,
                (_, _) =>
                {
                    recorder.Ran.Enqueue(CloseAll.Id);
                    return Task.FromResult(TaskResult<ProjectCloseAllResult>.Succeeded(new ProjectCloseAllResult(2)));
                },
                cancellationToken);

        return
        [
            NhAiGovernedAIFunction.Create(
                CloseAll,
                AIFunctionFactory.Create(closeAll, new AIFunctionFactoryOptions { Name = CloseAll.ExportName, Description = CloseAll.Description }),
                services)
        ];
    }
}

/// <summary>
/// What the test composition switch answers and was asked; one instance per host.
/// </summary>
public sealed class TestCompositionSwitchState
{
    public ConcurrentQueue<NhAssistantCompositionSwitchContext> Asked { get; } = new();

    public Func<NhAssistantCompositionSwitchContext, bool> Answer { get; set; } = _ => true;

    public Exception? Failure { get; set; }
}

/// <summary>
/// The application's own switch, as an application registers it with <c>UseCompositionSwitch</c>.
/// </summary>
public sealed class TestCompositionSwitch(TestCompositionSwitchState state) : INhAssistantCompositionSwitch
{
    public ValueTask<bool> IsOnAsync(NhAssistantCompositionSwitchContext context, CancellationToken cancellationToken)
    {
        state.Asked.Enqueue(context);
        if (state.Failure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(state.Answer(context));
    }
}
