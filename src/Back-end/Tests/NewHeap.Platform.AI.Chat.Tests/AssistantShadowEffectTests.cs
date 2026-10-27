using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Shadow is part of the effect decision. A run in the agent's shadow thread proposes every effect it
/// calls as a shadow proposal, also one the effect policy would run without approval, and never runs a
/// tool that issues or authorizes its own approval; the same calls in the live thread run as before.
/// The runs answer to a technical account and act with its rights without an initiator, as a replay
/// does. Both relational providers.
/// </summary>
public sealed class AssistantShadowEffectTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ServiceAccount = "approver-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_shadow_run_proposes_an_approval_free_effect_and_runs_no_tool_that_approves_itself_while_a_live_run_executes_them(
        AssistantTestProvider provider)
    {
        var project = Guid.NewGuid();
        var model = new NhAiScriptedChatClient();
        foreach (var round in new[] { "live", "shadow" })
        {
            model
                .RespondWithFunctionCall(TestLabelToolCatalog.SetLabel.ExportName!, new { input = new { projectId = project, label = "urgent" } }, $"call-{round}-label")
                .RespondWithFunctionCall(TestLabelToolCatalog.IssuePass.ExportName!, new { input = new { projectId = project, label = "visitor" } }, $"call-{round}-issue")
                .RespondWithFunctionCall(TestLabelToolCatalog.ConfirmOwn.ExportName!, new { input = new { projectId = project, label = "checked" } }, $"call-{round}-own")
                .RespondWithText("Done.");
        }
        await using var host = await CreateHostAsync(provider, model);
        var recorder = host.Services.GetRequiredService<TestLabelToolRecorder>();

        var live = await StartRunAsync(host, shadow: false);
        var liveTurn = await RunTurnAsync(host, live, "Label the project.");

        // Live, the effect policy decides: all three tools run with the technical account's rights.
        Assert.Equal(NhAssistantTurnStatuses.Completed, liveTurn.Outcome);
        Assert.Equal(["projects.set-label", "projects.issue-pass", "projects.confirm-own"], recorder.Ran.ToArray());
        Assert.Empty(await RunStore(host).GetRunApprovalsAsync(live.Run.Id, CancellationToken.None));

        var shadow = await StartRunAsync(host, shadow: true);
        var shadowTurn = await RunTurnAsync(host, shadow, "Label the project.");

        // In the shadow thread nothing runs: the approval-free label becomes a shadow proposal, and the
        // tools that approve themselves are refused.
        Assert.Equal(NhAssistantTurnStatuses.Completed, shadowTurn.Outcome);
        Assert.Equal(3, recorder.Ran.Count);
        Assert.Equal(1, shadowTurn.ProposalCount);
        var proposal = Assert.Single(await RunStore(host).GetRunApprovalsAsync(shadow.Run.Id, CancellationToken.None));
        Assert.Equal(TestLabelToolCatalog.SetLabel.Id, proposal.ToolId);
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, proposal.Status);
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var calls = await context.ToolInvocations
            .Where(invocation => invocation.ConversationId == shadow.AgentTurn.Thread.ConversationId)
            .ToListAsync();
        Assert.DoesNotContain(calls, invocation => invocation.Status == NhAssistantToolCallStatuses.Succeeded);
        Assert.Equal(
            [(TestLabelToolCatalog.IssuePass.Id, NhAiToolFailureCodes.EffectDenied), (TestLabelToolCatalog.ConfirmOwn.Id, NhAiToolFailureCodes.EffectDenied)],
            calls
                .Where(invocation => invocation.ToolId != TestLabelToolCatalog.SetLabel.Id)
                .OrderBy(invocation => invocation.StartedAt)
                .Select(invocation => (invocation.ToolId, invocation.ResultCode))
                .ToArray());
    }

    private Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<TestLabelToolRecorder>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestLabelToolCatalog>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .ConfigureAgent(AgentId, agent => agent
                    // Runs without an initiator act with the rights of the account the agent answers to.
                    .ServiceAccount(ServiceAccount)
                    .ActWithAccountableRights()));
    }

    private sealed record StartedRun(AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    /// <summary>
    /// A run without an initiator in the agent's live or shadow thread of a new project.
    /// </summary>
    private static async Task<StartedRun> StartRunAsync(AssistantTestHost host, bool shadow)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var subject = new NhAiSubjectRef("project", Guid.NewGuid().ToString());
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadow)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "project-created",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            Shadow = shadow,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        return new StartedRun(run, new NhAssistantAgentTurnContext(
            thread,
            info,
            options,
            run.Id,
            run.TriggerId,
            null,
            thread.AccountableOwnerId,
            IsRequestTurn: false));
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, StartedRun run, string message)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>();
        return await runner.RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                run.AgentTurn,
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.Run.TriggerId, message)]),
            CancellationToken.None);
    }

    private static NhAssistantRunStore RunStore(AssistantTestHost host)
    {
        return host.Services.GetRequiredService<NhAssistantRunStore>();
    }
}

public sealed record ProjectLabelInput(Guid ProjectId, string Label);

/// <summary>
/// Records which label tools ran.
/// </summary>
public sealed class TestLabelToolRecorder
{
    public ConcurrentQueue<string> Ran { get; } = new();
}

/// <summary>
/// Shared-invoker tools the default effect policy runs without an approval for an agent that answers
/// to a technical account: an idempotent mutation that needs none, a tool that issues an approval and
/// a tool that authorizes its own.
/// </summary>
public sealed class TestLabelToolCatalog : INhAiToolCatalog
{
    public static readonly NhAiToolDescriptor SetLabel = Descriptor(
        "projects.set-label",
        "projects_set_label_v1",
        "Set the label of one project.",
        NhAiApprovalRequirement.NotRequired,
        '6');

    public static readonly NhAiToolDescriptor IssuePass = Descriptor(
        "projects.issue-pass",
        "projects_issue_pass_v1",
        "Issue a visitor pass for one project.",
        NhAiApprovalRequirement.Issuer,
        '7');

    public static readonly NhAiToolDescriptor ConfirmOwn = Descriptor(
        "projects.confirm-own",
        "projects_confirm_own_v1",
        "Confirm a check of one project, which the tool approves itself.",
        NhAiApprovalRequirement.ConsumerAuthoritative,
        '8');

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [SetLabel, IssuePass, ConfirmOwn];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "project-labels",
        1,
        new string('9', 64),
        [
            new NhAiToolManifestEntry(SetLabel.Id, 1, SetLabel.SchemaHash, SetLabel.ContractHash) { ExportName = SetLabel.ExportName },
            new NhAiToolManifestEntry(IssuePass.Id, 1, IssuePass.SchemaHash, IssuePass.ContractHash) { ExportName = IssuePass.ExportName },
            new NhAiToolManifestEntry(ConfirmOwn.Id, 1, ConfirmOwn.SchemaHash, ConfirmOwn.ContractHash) { ExportName = ConfirmOwn.ExportName }
        ]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var recorder = services.GetRequiredService<TestLabelToolRecorder>();
        return Descriptors
            .Select(descriptor =>
            {
                Func<ProjectLabelInput, CancellationToken, Task<TaskResult<string>>> invoke =
                    (input, cancellationToken) => invoker.InvokeAsync(
                        descriptor,
                        input,
                        (_, _) =>
                        {
                            recorder.Ran.Enqueue(descriptor.Id);
                            return Task.FromResult(TaskResult<string>.Succeeded(input.Label));
                        },
                        cancellationToken);
                return NhAiGovernedAIFunction.Create(
                    descriptor,
                    AIFunctionFactory.Create(invoke, new AIFunctionFactoryOptions { Name = descriptor.ExportName, Description = descriptor.Description }),
                    services);
            })
            .ToArray();
    }

    private static NhAiToolDescriptor Descriptor(string id, string exportName, string description, NhAiApprovalRequirement approval, char hash)
    {
        return new NhAiToolDescriptor(
            id,
            1,
            description,
            typeof(ProjectLabelInput),
            typeof(string),
            NhAiToolEffect.IdempotentMutation,
            NhAiToolExposure.Local | NhAiToolExposure.Agent,
            true,
            [TestProjectToolCatalog.ManagePolicy])
        {
            ExportName = exportName,
            CatalogId = "project-labels",
            ContractHash = new string(hash, 64),
            SchemaHash = new string(hash, 64),
            Approval = approval,
            Idempotency = NhAiIdempotencySupport.Required,
            AllowAgentGrant = true
        };
    }
}
