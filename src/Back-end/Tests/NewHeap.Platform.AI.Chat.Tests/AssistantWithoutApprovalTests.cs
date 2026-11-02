using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
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
/// FB82: an agent runs a kind of action without approval only with two keys, the tool's in code and an
/// administrator's switch as data, first in shadow and only then live. In shadow every call still becomes
/// a proposal and the platform records that it would have run without approval; live, the agent's own run
/// executes the call without a proposal and audits it with the switch's version. Switching off asks again
/// at once, a person's chat and a shadow run still ask, a switch made for another contract reads as off,
/// and the agent's stop still stops it. The runs answer to a technical account and act with its rights.
/// Both relational providers.
/// </summary>
public sealed class AssistantWithoutApprovalTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ServiceAccount = "approver-1";
    private const string Administrator = "admin-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_switch_needs_the_code_key_goes_live_only_from_shadow_and_keeps_who_switched_each_version(
        AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient());

        // The first key: a tool that does not allow it in code cannot be switched, nor a tool outside the agent's tools.
        var withoutKey = await SwitchAsync(host, TestTaskToolCatalog.Archive.Id, NhAssistantWithoutApprovalModes.Shadow, 0);
        var unknown = await SwitchAsync(host, "orders.cancel", NhAssistantWithoutApprovalModes.Shadow, 0);
        // A tool whose proposals a person decides one by one never runs without approval, whatever its code says.
        var individual = await SwitchAsync(host, TestTaskToolCatalog.Notify.Id, NhAssistantWithoutApprovalModes.Shadow, 0);
        var noAgent = await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Shadow, 0, agentId: "nobody");
        // Live only from shadow.
        var liveFirst = await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Live, 0);
        var invalid = await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, "always", 0);

        Assert.Equal(NhAssistantErrorCodes.WithoutApprovalNotAllowed, CodeOf(withoutKey));
        Assert.Equal(NhAssistantErrorCodes.WithoutApprovalNotAllowed, CodeOf(unknown));
        Assert.Equal(NhAssistantErrorCodes.WithoutApprovalNotAllowed, CodeOf(individual));
        Assert.Equal(NhAssistantErrorCodes.AgentNotFound, CodeOf(noAgent));
        Assert.Equal(NhAssistantErrorCodes.WithoutApprovalShadowFirst, CodeOf(liveFirst));
        Assert.Contains(invalid.GetResultItems(), item => item.Name == "field:mode");

        var shadow = await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Shadow, 0, reason: "  Measure first  ");
        var stale = await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Live, 0);
        var live = await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Live, 1);
        var again = await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Live, 2);

        Assert.True(shadow.Success);
        Assert.Equal((NhAssistantWithoutApprovalModes.Shadow, 1, "Measure first"), (shadow.Data!.Mode, shadow.Data.Version, shadow.Data.Reason));
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, CodeOf(stale));
        Assert.Equal((NhAssistantWithoutApprovalModes.Live, 2, Administrator), (live.Data!.Mode, live.Data.Version, live.Data.ChangedBy));
        // The same mode again writes no version.
        Assert.Equal(2, again.Data!.Version);

        await using var scope = host.Services.CreateAsyncScope();
        var switches = scope.ServiceProvider.GetRequiredService<INhAssistantWithoutApprovalSwitches>();
        var listed = (await switches.GetAsync(AgentId))!;
        var versions = await switches.GetVersionsAsync(AgentId, TestTaskToolCatalog.Complete.Id);

        Assert.True(listed.Applies);
        var tool = Assert.Single(listed.Tools);
        Assert.Equal((TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Live, false, "idempotent-mutation"), (tool.ToolId, tool.Mode, tool.ContractChanged, tool.Effect));
        Assert.Equal([2, 1], versions.Select(version => version.Version));
        Assert.Equal([NhAssistantWithoutApprovalModes.Live, NhAssistantWithoutApprovalModes.Shadow], versions.Select(version => version.Mode));
        Assert.All(versions, version => Assert.Equal(TestTaskToolCatalog.Complete.ContractHash, version.ContractHash));
        var changes = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentToolWithoutApprovalChanged).ToArray();
        Assert.Equal([(NhAssistantWithoutApprovalModes.Shadow, 1), (NhAssistantWithoutApprovalModes.Live, 2)], changes.Select(evt => (evt.ResultCode, evt.WithoutApprovalVersion!.Value)));
        Assert.All(changes, evt =>
        {
            Assert.Equal(Administrator, evt.ActorId);
            Assert.Equal(AgentId, evt.ObjectId);
            Assert.Equal(TestTaskToolCatalog.Complete.Id, evt.ToolId);
        });
        // The reason stays with the version and out of the audit.
        Assert.DoesNotContain(host.Business.Events, evt => evt.ResultCode?.Contains("Measure", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task In_shadow_the_run_still_proposes_and_records_that_the_call_would_have_run_without_approval(
        AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient();
        ScriptCompletion(model, Guid.NewGuid(), "shadow");
        await using var host = await CreateHostAsync(provider, model);
        var recorder = host.Services.GetRequiredService<TestTaskToolRecorder>();
        Assert.True((await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Shadow, 0)).Success);

        var run = await StartRunAsync(host);
        var turn = await RunTurnAsync(host, run, "Complete the task.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Outcome);
        Assert.Empty(recorder.Ran);
        var proposal = Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, proposal.Status);
        Assert.Equal(1, proposal.WithoutApprovalVersion);
        var recorded = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentProposalWouldRunWithoutApproval);
        Assert.Equal((proposal.Id, TestTaskToolCatalog.Complete.Id, 1, NhAssistantWithoutApprovalModes.Shadow), (recorded.ApprovalId!.Value, recorded.ToolId, recorded.WithoutApprovalVersion!.Value, recorded.ResultCode));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ApprovalRequested && evt.ApprovalId == proposal.Id);
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentToolRanWithoutApproval);
        var audit = Assert.Single(host.Audit.Records, record => record.ToolId == TestTaskToolCatalog.Complete.Id);
        Assert.Equal((NhAiOutcomeKind.ApprovalRequired, NhAiWithoutApprovalMode.Shadow, 1), (audit.Outcome, audit.WithoutApprovalMode, audit.WithoutApprovalSwitchVersion!.Value));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Live_the_run_executes_without_a_proposal_audits_it_and_switching_off_asks_again_at_once(
        AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient();
        ScriptCompletion(model, Guid.NewGuid(), "live");
        ScriptCompletion(model, Guid.NewGuid(), "off");
        await using var host = await CreateHostAsync(provider, model);
        var recorder = host.Services.GetRequiredService<TestTaskToolRecorder>();
        await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Shadow, 0);
        Assert.True((await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Live, 1)).Success);

        var live = await StartRunAsync(host);
        var liveTurn = await RunTurnAsync(host, live, "Complete the task.");

        // The tool ran with every other rule of the call, and nothing waits for a person.
        Assert.Equal(NhAssistantTurnStatuses.Completed, liveTurn.Outcome);
        Assert.Equal([TestTaskToolCatalog.Complete.Id], recorder.Ran.ToArray());
        Assert.Equal(0, liveTurn.ProposalCount);
        Assert.Empty(await RunStore(host).GetRunApprovalsAsync(live.Run.Id, CancellationToken.None));
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            var call = await context.ToolInvocations.SingleAsync(invocation => invocation.ConversationId == live.AgentTurn.Thread.ConversationId);
            Assert.Equal((NhAssistantToolCallStatuses.Succeeded, 2), (call.Status, call.WithoutApprovalVersion!.Value));
            Assert.Null(call.ApprovalId);
        }
        var ran = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentToolRanWithoutApproval);
        Assert.Equal((TestTaskToolCatalog.Complete.Id, 2, "succeeded"), (ran.ToolId, ran.WithoutApprovalVersion!.Value, ran.ResultCode));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked && evt.InvocationId == ran.InvocationId);
        var audit = Assert.Single(host.Audit.Records, record => record.ToolId == TestTaskToolCatalog.Complete.Id);
        Assert.Equal(
            (NhAiOutcomeKind.Succeeded, NhAiWithoutApproval.ApprovalCode, NhAiWithoutApprovalMode.Live, 2, NhAiGrantSource.Accountable),
            (audit.Outcome, audit.ApprovalCode, audit.WithoutApprovalMode, audit.WithoutApprovalSwitchVersion!.Value, audit.GrantSource));

        // Switching off applies to the next call: it is a proposal again, and nothing ran.
        Assert.True((await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Off, 2)).Success);
        var off = await StartRunAsync(host);
        var offTurn = await RunTurnAsync(host, off, "Complete the task.");

        Assert.Equal(1, offTurn.ProposalCount);
        Assert.Single(recorder.Ran);
        var proposal = Assert.Single(await RunStore(host).GetRunApprovalsAsync(off.Run.Id, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Pending, proposal.Status);
        Assert.Null(proposal.WithoutApprovalVersion);
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentToolRanWithoutApproval);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_persons_chat_a_shadow_run_and_a_tool_without_the_key_still_ask_while_the_switch_is_live(
        AssistantTestProvider provider)
    {
        var project = Guid.NewGuid();
        var model = new NhAiScriptedChatClient();
        ScriptCompletion(model, project, "shadow-run");
        model
            .RespondWithFunctionCall(TestTaskToolCatalog.Archive.ExportName, new { input = new { projectId = project, task = "archive" } }, "call-archive")
            .RespondWithText("Proposed.")
            // The chat waits for the person at the approval, so the model is not asked again.
            .RespondWithFunctionCall(TestTaskToolCatalog.Complete.ExportName, new { input = new { projectId = project, task = "chat" } }, "call-chat");
        await using var host = await CreateHostAsync(provider, model);
        var recorder = host.Services.GetRequiredService<TestTaskToolRecorder>();
        await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Shadow, 0);
        await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Live, 1);

        // A run in the agent's shadow thread only ever proposes, as shadow proposals.
        var shadow = await StartRunAsync(host, shadowThread: true);
        var shadowTurn = await RunTurnAsync(host, shadow, "Complete the task.");
        // A tool that does not allow it in code needs approval as always.
        var archive = await StartRunAsync(host);
        await RunTurnAsync(host, archive, "Archive the task.");
        // A person's own chat with the agent is no agent work: the person decides. It runs last, as the
        // person's request stays the ambient request of this flow afterwards.
        var conversation = await host.CreateConversationAsync();
        var chat = await host.SendAsync(conversation.Id, "Complete the task.");

        Assert.Empty(recorder.Ran);
        Assert.NotNull(chat.Single<NhAssistantApprovalRequiredEvent>());
        var shadowProposal = Assert.Single(await RunStore(host).GetRunApprovalsAsync(shadow.Run.Id, CancellationToken.None));
        Assert.Equal(NhAssistantApprovalStatuses.Shadow, shadowProposal.Status);
        Assert.Null(shadowProposal.WithoutApprovalVersion);
        Assert.Equal(1, shadowTurn.ProposalCount);
        var archiveProposal = Assert.Single(await RunStore(host).GetRunApprovalsAsync(archive.Run.Id, CancellationToken.None));
        Assert.Equal((TestTaskToolCatalog.Archive.Id, NhAssistantApprovalStatuses.Pending), (archiveProposal.ToolId, archiveProposal.Status));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind is NhAssistantAuditEventKind.AgentToolRanWithoutApproval
            or NhAssistantAuditEventKind.AgentProposalWouldRunWithoutApproval);
        Assert.All(host.Audit.Records, record => Assert.Equal(NhAiWithoutApprovalMode.Off, record.WithoutApprovalMode));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_switch_made_for_another_contract_reads_as_off_and_goes_through_shadow_again(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient();
        ScriptCompletion(model, Guid.NewGuid(), "changed");
        await using var host = await CreateHostAsync(provider, model);
        var recorder = host.Services.GetRequiredService<TestTaskToolRecorder>();
        // A live switch of an earlier contract of the tool, as a deploy that changed the tool leaves it.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            // The agent's row, which every switch belongs to.
            await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None);
        }
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            context.AgentToolSwitches.Add(new AssistantAgentToolSwitch
            {
                Id = Guid.NewGuid(),
                AgentId = AgentId,
                ToolId = TestTaskToolCatalog.Complete.Id,
                Version = 1,
                Mode = NhAssistantWithoutApprovalModes.Live,
                ContractHash = new string('0', 64),
                ChangedBy = Administrator,
                ChangedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var run = await StartRunAsync(host);
        await RunTurnAsync(host, run, "Complete the task.");
        await using var reading = host.Services.CreateAsyncScope();
        var tool = Assert.Single((await reading.ServiceProvider.GetRequiredService<INhAssistantWithoutApprovalSwitches>().GetAsync(AgentId))!.Tools);
        var live = await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Live, 1);

        Assert.Empty(recorder.Ran);
        Assert.Single(await RunStore(host).GetRunApprovalsAsync(run.Run.Id, CancellationToken.None));
        Assert.Equal((NhAssistantWithoutApprovalModes.Off, true, 1), (tool.Mode, tool.ContractChanged, tool.Version));
        Assert.Equal(NhAssistantErrorCodes.WithoutApprovalShadowFirst, CodeOf(live));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_kill_switch_still_stops_an_agent_whose_tool_runs_without_approval(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient();
        ScriptCompletion(model, Guid.NewGuid(), "stopped");
        await using var host = await CreateHostAsync(provider, model);
        var recorder = host.Services.GetRequiredService<TestTaskToolRecorder>();
        await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Shadow, 0);
        await SwitchAsync(host, TestTaskToolCatalog.Complete.Id, NhAssistantWithoutApprovalModes.Live, 1);
        var queued = await StartRunAsync(host);

        TaskResult<NhAssistantAgentState> stopped;
        NhAssistantTriggerReceipt started;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            stopped = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentControl>().SetKillSwitchAsync(AgentId, true, Administrator);
            started = await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>().StartAsync(
                AgentId,
                queued.AgentTurn.Subject.Subject,
                "member-1",
                "Complete the task.");
        }

        Assert.True(stopped.Success);
        Assert.Equal(NhAssistantAgentRunStates.Stopped, stopped.Data!.RunState);
        Assert.Equal(NhAssistantTriggerOutcomes.Rejected, started.Outcome);
        Assert.Equal(NhAssistantErrorCodes.AgentStopped, started.ReasonCode);
        Assert.Equal(NhAssistantRunStatuses.Cancelled, (await RunStore(host).FindRunAsync(queued.Run.Id, CancellationToken.None))!.Status);
        Assert.Empty(recorder.Ran);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_an_administrator_reads_and_switches_over_http_and_an_agent_without_work_of_its_own_cannot_be_switched(
        AssistantTestProvider provider)
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TestTaskToolRecorder>();
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestTaskToolCatalog>());
            },
            provider: provider);
        using var user = app.CreateClient();
        using var admin = app.CreateClient(admin: true);
        var route = $"/api/assistant/admin/agents/{AgentId}/tools/{TestTaskToolCatalog.Complete.Id}/without-approval";

        // A person without the administration policy can neither read nor switch.
        using var forbiddenRead = await user.GetAsync($"/api/assistant/admin/agents/{AgentId}/without-approval");
        using var forbiddenSwitch = await user.PutAsync(route, ServerSentEventReader.Json(new { mode = "shadow", expectedVersion = 0 }));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenSwitch.StatusCode);
        Assert.Equal("assistant-forbidden", (await ReadJsonAsync(forbiddenSwitch)).GetProperty("code").GetString());

        // The chat agent has no runs of its own: its tool lists with the key, and switching is refused.
        var listed = await ReadJsonAsync(await admin.GetAsync($"/api/assistant/admin/agents/{AgentId}/without-approval"));
        using var refused = await admin.PutAsync(route, ServerSentEventReader.Json(new { mode = "shadow", expectedVersion = 0 }));
        using var invalid = await admin.PutAsync(route, ServerSentEventReader.Json(new { mode = "sometimes", expectedVersion = 0 }));
        using var unknown = await admin.GetAsync("/api/assistant/admin/agents/nobody/without-approval");
        var tools = await ReadJsonAsync(await admin.GetAsync("/api/assistant/admin/tools"));
        var versions = await ReadJsonAsync(await admin.GetAsync(route + "/versions"));

        Assert.False(listed.GetProperty("applies").GetBoolean());
        var tool = Assert.Single(listed.GetProperty("tools").EnumerateArray());
        Assert.Equal((TestTaskToolCatalog.Complete.Id, "off", 0), (tool.GetProperty("toolId").GetString(), tool.GetProperty("mode").GetString(), tool.GetProperty("version").GetInt32()));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(NhAssistantErrorCodes.WithoutApprovalNotAllowed, (await ReadJsonAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(versions.EnumerateArray());
        var entries = tools.EnumerateArray().ToDictionary(entry => entry.GetProperty("id").GetString()!, entry => entry.GetProperty("allowWithoutApproval").GetBoolean());
        Assert.True(entries[TestTaskToolCatalog.Complete.Id]);
        Assert.False(entries[TestTaskToolCatalog.Archive.Id]);
        Assert.False(entries[TestTaskToolCatalog.Notify.Id]);
    }

    private static async Task<System.Text.Json.JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void ScriptCompletion(NhAiScriptedChatClient model, Guid project, string round)
    {
        model
            .RespondWithFunctionCall(TestTaskToolCatalog.Complete.ExportName, new { input = new { projectId = project, task = round } }, $"call-{round}")
            .RespondWithText("Done.");
    }

    private static async Task<TaskResult<NhAssistantWithoutApprovalSwitchDto>> SwitchAsync(
        AssistantTestHost host,
        string toolId,
        string mode,
        int expectedVersion,
        string? reason = null,
        string agentId = AgentId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantWithoutApprovalSwitches>().SetAsync(
            agentId,
            toolId,
            new NhAssistantWithoutApprovalRequest(mode, expectedVersion, reason),
            Administrator);
    }

    private static string? CodeOf(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => name?.StartsWith("assistant-", StringComparison.Ordinal) == true);
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
                services.AddSingleton<TestTaskToolRecorder>();
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestTaskToolCatalog>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("task-review", policy => policy
                    .Mode(NhAiApprovalMode.Deferred)
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver)))
                .ConfigureAgent(AgentId, agent => agent
                    // Runs without an initiator act with the rights of the account the agent answers to.
                    .ServiceAccount(ServiceAccount)
                    .ActWithAccountableRights()
                    .UseApprovalPolicy("task-review")));
    }

    private sealed record StartedRun(AssistantRun Run, NhAssistantAgentTurnContext AgentTurn);

    /// <summary>
    /// A run without an initiator in the agent's live or shadow thread of a new project.
    /// </summary>
    private static async Task<StartedRun> StartRunAsync(AssistantTestHost host, bool shadowThread = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var subject = new NhAiSubjectRef("project", Guid.NewGuid().ToString());
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadowThread)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "project-created",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            Shadow = shadowThread,
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

public sealed record ProjectTaskInput(Guid ProjectId, string Task);

/// <summary>
/// Records which task tools ran.
/// </summary>
public sealed class TestTaskToolRecorder
{
    public ConcurrentQueue<string> Ran { get; } = new();
}

/// <summary>
/// Two reversible task tools that need a person's approval: completing a task, which allows an
/// administrator to let an agent run it without approval, and archiving one, which does not.
/// </summary>
public sealed class TestTaskToolCatalog : INhAiToolCatalog
{
    public static readonly NhAiToolDescriptor Complete = Descriptor(
        "projects.complete-task",
        "projects_complete_task_v1",
        "Complete one task of a project.",
        allowWithoutApproval: true,
        'c');

    public static readonly NhAiToolDescriptor Archive = Descriptor(
        "projects.archive-task",
        "projects_archive_task_v1",
        "Archive one task of a project.",
        allowWithoutApproval: false,
        'd');

    /// <summary>
    /// Claims the code key but requires an individual decision, which keeps it out of every switch.
    /// </summary>
    public static readonly NhAiToolDescriptor Notify = Descriptor(
        "projects.notify-task",
        "projects_notify_task_v1",
        "Tell the task's owner by mail.",
        allowWithoutApproval: true,
        'f') with { RequireIndividualDecision = true };

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [Complete, Archive, Notify];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "project-task-tools",
        1,
        new string('e', 64),
        [
            new NhAiToolManifestEntry(Complete.Id, 1, Complete.SchemaHash, Complete.ContractHash) { ExportName = Complete.ExportName },
            new NhAiToolManifestEntry(Archive.Id, 1, Archive.SchemaHash, Archive.ContractHash) { ExportName = Archive.ExportName },
            new NhAiToolManifestEntry(Notify.Id, 1, Notify.SchemaHash, Notify.ContractHash) { ExportName = Notify.ExportName }
        ]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var recorder = services.GetRequiredService<TestTaskToolRecorder>();
        return Descriptors
            .Select(descriptor =>
            {
                Func<ProjectTaskInput, CancellationToken, Task<TaskResult<string>>> invoke =
                    (input, cancellationToken) => invoker.InvokeAsync(
                        descriptor,
                        input,
                        (_, _) =>
                        {
                            recorder.Ran.Enqueue(descriptor.Id);
                            return Task.FromResult(TaskResult<string>.Succeeded(input.Task));
                        },
                        cancellationToken);
                return NhAiGovernedAIFunction.Create(
                    descriptor,
                    AIFunctionFactory.Create(invoke, new AIFunctionFactoryOptions { Name = descriptor.ExportName, Description = descriptor.Description }),
                    services);
            })
            .ToArray();
    }

    private static NhAiToolDescriptor Descriptor(string id, string exportName, string description, bool allowWithoutApproval, char hash)
    {
        return new NhAiToolDescriptor(
            id,
            1,
            description,
            typeof(ProjectTaskInput),
            typeof(string),
            NhAiToolEffect.IdempotentMutation,
            NhAiToolExposure.Local | NhAiToolExposure.Agent,
            true,
            [TestProjectToolCatalog.ManagePolicy])
        {
            ExportName = exportName,
            CatalogId = "project-task-tools",
            ContractHash = new string(hash, 64),
            SchemaHash = new string(hash, 64),
            Approval = NhAiApprovalRequirement.Required,
            Idempotency = NhAiIdempotencySupport.Required,
            AllowWithoutApproval = allowWithoutApproval
        };
    }
}
