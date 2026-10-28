using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// Records what the risk tools ran and with which arguments and contexts.
/// </summary>
public sealed class TestRiskToolRecorder
{
    public ConcurrentQueue<(string ToolId, JsonElement Input)> Ran { get; } = new();

    public ConcurrentQueue<NhAiInvocationContext> Contexts { get; } = new();

    public IReadOnlyList<string> RanTools => Ran.Select(item => item.ToolId).ToArray();

    public JsonElement InputOf(string toolId)
    {
        return Ran.Last(item => item.ToolId == toolId).Input;
    }
}

/// <summary>
/// Shared-invoker tools a composition chains in the tests: a project, its open risks and their owners
/// (read-only and also exposed over MCP), a change that needs approval, an idempotent change that needs
/// none, and a read a person needs a claim for.
/// </summary>
public sealed class TestRiskToolCatalog : INhAiToolCatalog
{
    public const string RestrictedPolicy = "app.project.manage";

    public static readonly NhAiToolDescriptor GetProject = Descriptor("projects.get", NhAiToolEffect.ReadOnly, NhAiApprovalRequirement.PolicyControlled, 'a', mcp: true);

    public static readonly NhAiToolDescriptor ListRisks = Descriptor("projects.list-risks", NhAiToolEffect.ReadOnly, NhAiApprovalRequirement.PolicyControlled, 'b', mcp: true);

    public static readonly NhAiToolDescriptor GetPeople = Descriptor("projects.get-people", NhAiToolEffect.ReadOnly, NhAiApprovalRequirement.PolicyControlled, 'c', mcp: true);

    public static readonly NhAiToolDescriptor FlagRisk = Descriptor("projects.flag-risk", NhAiToolEffect.IdempotentMutation, NhAiApprovalRequirement.Required, 'd') with
    {
        Idempotency = NhAiIdempotencySupport.Required
    };

    public static readonly NhAiToolDescriptor TagRisk = Descriptor("projects.tag-risk", NhAiToolEffect.IdempotentMutation, NhAiApprovalRequirement.NotRequired, 'e') with
    {
        Idempotency = NhAiIdempotencySupport.Required
    };

    public static readonly NhAiToolDescriptor ReadSecret = Descriptor("projects.read-secret", NhAiToolEffect.ReadOnly, NhAiApprovalRequirement.PolicyControlled, 'f', policy: RestrictedPolicy);

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [GetProject, ListRisks, GetPeople, FlagRisk, TagRisk, ReadSecret];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "project-risks",
        1,
        new string('7', 64),
        [
            .. new[] { GetProject, ListRisks, GetPeople, FlagRisk, TagRisk, ReadSecret }
                .Select(descriptor => new NhAiToolManifestEntry(descriptor.Id, 1, descriptor.SchemaHash, descriptor.ContractHash) { ExportName = descriptor.ExportName })
        ]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var recorder = services.GetRequiredService<TestRiskToolRecorder>();
        return Descriptors
            .Select(descriptor =>
            {
                Func<JsonElement, CancellationToken, Task<TaskResult<JsonElement?>>> invoke =
                    (input, cancellationToken) => invoker.InvokeAsync(
                        descriptor,
                        input,
                        (context, _) =>
                        {
                            recorder.Contexts.Enqueue(context);
                            recorder.Ran.Enqueue((descriptor.Id, input.Clone()));
                            return Task.FromResult(TaskResult<JsonElement?>.Succeeded(Handle(descriptor.Id, input)));
                        },
                        cancellationToken);
                return NhAiGovernedAIFunction.Create(
                    descriptor,
                    AIFunctionFactory.Create(invoke, new AIFunctionFactoryOptions
                    {
                        Name = descriptor.ExportName,
                        Description = descriptor.Description,
                        ExcludeResultSchema = true,
                        MarshalResult = NhAiToolResult.CreateMarshaller<JsonElement?>()
                    }),
                    services);
            })
            .ToArray();
    }

    private static JsonElement Handle(string toolId, JsonElement input)
    {
        object result = toolId switch
        {
            "projects.get" => new { id = input.GetProperty("projectId").GetString(), name = "Roadmap", ownerId = "person-1" },
            "projects.list-risks" => new[]
            {
                new { id = "risk-1", title = "Late supplier", ownerId = "person-2" },
                new { id = "risk-2", title = "Budget", ownerId = "person-3" }
            },
            "projects.get-people" => input.GetProperty("ids").EnumerateArray()
                .Select(id => new { id = id.GetString(), name = "Name of " + id.GetString() })
                .ToArray(),
            "projects.read-secret" => new { secret = "classified" },
            _ => new { riskId = input.GetProperty("riskId").GetString(), done = true }
        };
        return JsonSerializer.SerializeToElement(result);
    }

    private static NhAiToolDescriptor Descriptor(string id, NhAiToolEffect effect, NhAiApprovalRequirement approval, char hash, bool mcp = false, string? policy = null)
    {
        return new NhAiToolDescriptor(
            id,
            1,
            "Test tool " + id + ".",
            typeof(JsonElement),
            typeof(JsonElement),
            effect,
            NhAiToolExposure.Local | NhAiToolExposure.Agent | (mcp ? NhAiToolExposure.Mcp : NhAiToolExposure.None),
            true,
            [policy ?? TestProjectToolCatalog.ReadPolicy])
        {
            ExportName = id.Replace('.', '_').Replace('-', '_') + "_v1",
            CatalogId = "project-risks",
            ContractHash = new string(hash, 64),
            SchemaHash = new string(hash, 64),
            Approval = approval,
            AllowAgentGrant = true
        };
    }
}

public sealed record ProjectRiskInput(Guid ProjectId);
