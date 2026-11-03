using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Scripting;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Rule scripts (RB-1 to RB-6, lane RS): a live version runs instead of the agent's code rule without a
/// model, reads with the run's grants, writes only what its version opted in to within the ceiling, each
/// write with its own decision, and ends as a rule result; a dry run and a shadow run change nothing; going
/// live shows the latest dry run, refuses the author under four eyes and needs the administration policy;
/// a run on the whole application never resolves by omission. On both relational providers.
/// </summary>
public sealed class AssistantRuleScriptTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Kind = "task-needs-plan";
    private const string Administrator = "admin-1";
    private const string OtherAdministrator = "admin-2";
    private static readonly string TagRisk = TestRiskToolCatalog.TagRisk.Id;
    private static readonly string FlagRisk = TestRiskToolCatalog.FlagRisk.Id;
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 7, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Reads the task's project, tags one risk (no approval needed, so it runs), flags another (it needs
    /// one, so it becomes a proposal), raises an alert and ends with a summary.
    /// </summary>
    private const string WritingScript = """
        const project = await tools.projects_get_v1({ projectId: subject.id });
        const tagged = await tools.projects_tag_risk_v1({ riskId: "risk-1" });
        const flagged = await tools.projects_flag_risk_v1({ riskId: "risk-2" });
        alerts.raise({ kind: "task-needs-plan", key: "plan", parameters: { task: project.data.name } });
        done("Tagged " + tagged.success + ", flagged " + flagged.code + ".");
        """;

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_live_script_runs_instead_of_the_code_rule_and_each_write_gets_the_decision_it_gets_on_its_own(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var task = TaskSubject(Guid.NewGuid());
        var saved = await SaveAsync(host, WritingScript, [TagRisk, FlagRisk], live: true);
        Assert.True(saved.Success, string.Join(",", saved.GetResultItems().Select(item => item.Name)));

        var result = await RunAsync(host, task);

        // No model and no code rule: the script ended the run as a rule result.
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        Assert.Equal(NhAssistantRunKinds.Rule, result.Kind);
        Assert.Equal(0, host.Services.GetRequiredService<AlertRuleScript>().Runs);
        Assert.Equal(0, result.InputTokens + result.OutputTokens);
        // The write without approval ran; the one that needs approval became a deferred proposal.
        var risks = host.Services.GetRequiredService<TestRiskToolRecorder>();
        Assert.Equal(["projects.get", TagRisk], risks.RanTools);
        var approval = Assert.Single(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(result.RunId!.Value, CancellationToken.None));
        Assert.Equal((FlagRisk, NhAssistantApprovalStatuses.Pending, NhAssistantApprovalModes.Deferred), (approval.ToolId, approval.Status, approval.Mode));
        // The alert of the script, as a rule result raises it.
        var alert = Assert.Single(await AlertsAboutAsync(host, task));
        Assert.Equal((Kind, "plan", NhAssistantAlertStatuses.Open), (alert.KindId, alert.Key, alert.Status));

        // The script's call is a call of the run, with every tool call as its step naming the version.
        var rows = await AssistantToolStepTests.RowsAsync(host, result.ConversationId);
        var parent = Assert.Single(rows, row => row.ToolId == NhAssistantRuleScriptTool.ToolId);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, parent.Status);
        Assert.Contains("\"version\":1", parent.ArgumentsJson, StringComparison.Ordinal);
        var steps = rows.Where(row => row.CallId.StartsWith(NhAssistantToolStepCallIds.PrefixOf(NhAiToolCallOriginKind.Script, parent.Id), StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, steps.Length);
        var origins = risks.Contexts.Select(context => context.Origin).ToArray();
        Assert.All(origins, origin =>
        {
            Assert.Equal(NhAssistantRuleScriptTool.ToolId, origin?.ToolId);
            Assert.Equal(saved.Data!.ScriptHash, origin?.Hash);
            Assert.Equal(1, origin?.RuleScriptVersion);
            Assert.Equal(parent.Id, origin?.ParentInvocationId);
        });
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_tool_the_run_may_not_use_a_write_not_opted_in_and_more_writes_than_the_ceiling_fail_the_run_with_the_script_code(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, maxWrites: 1);
        var risks = host.Services.GetRequiredService<TestRiskToolRecorder>();

        // A tool the run does not offer.
        await SaveAsync(host, """await tools.users_delete_v1({ id: "u-1" }); done("Gone.");""", [], live: true);
        var unknown = await RunAsync(host, TaskSubject(Guid.NewGuid()));
        // A write the version did not opt in to.
        await SaveAsync(host, """await tools.projects_tag_risk_v1({ riskId: "risk-1" }); done("Tagged.");""", [FlagRisk], live: true, revision: 1);
        var notOptedIn = await RunAsync(host, TaskSubject(Guid.NewGuid()));
        // Two writes where the ceiling allows one per run.
        await SaveAsync(host, """
            await tools.projects_tag_risk_v1({ riskId: "risk-1" });
            await tools.projects_tag_risk_v1({ riskId: "risk-2" });
            done("Tagged twice.");
            """, [TagRisk], live: true, revision: 2);
        var twice = await RunAsync(host, TaskSubject(Guid.NewGuid()));

        Assert.Equal((NhAssistantTurnStatuses.Failed, NhAiScriptCodes.UnknownTool), (unknown.Outcome, unknown.ErrorCode));
        Assert.Equal((NhAssistantTurnStatuses.Failed, NhAiScriptCodes.ToolRefused), (notOptedIn.Outcome, notOptedIn.ErrorCode));
        Assert.Equal((NhAssistantTurnStatuses.Failed, NhAiScriptCodes.MutationLimit), (twice.Outcome, twice.ErrorCode));
        // Only the first write of the last script reached its tool.
        Assert.Equal([TagRisk], risks.RanTools);
        Assert.Equal(0, host.Services.GetRequiredService<AlertRuleScript>().Runs);

        // A write outside the ceiling is not even saved.
        var outside = await SaveAsync(host, "done('x');", ["projects.change-status"], live: false, revision: 3);
        Assert.Equal(NhAssistantRuleScriptCodes.WriteNotAllowed, CodeOf(outside));
        var broken = await SaveAsync(host, "const = ;", [], live: false, revision: 3);
        Assert.Contains(broken.GetResultItems(), item => item.Name == "field:script");
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_dry_run_records_what_a_version_would_do_and_changes_nothing(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var task = TaskSubject(Guid.NewGuid());
        var script = WritingScript + "\nconst proposed = await propose({ tool: \"projects_flag_risk_v1\", input: { riskId: \"risk-3\" }, rationale: \"Check it.\" });";
        var saved = await SaveAsync(host, script, [TagRisk, FlagRisk], live: false);
        Assert.True(saved.Success);
        var risks = host.Services.GetRequiredService<TestRiskToolRecorder>();

        await using var scope = host.Services.CreateAsyncScope();
        var ruleScripts = scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>();
        var dryRun = await ruleScripts.DryRunAsync(
            AgentId,
            null,
            1,
            new NhAssistantRuleScriptDryRunRequest([new NhAssistantRuleScriptSubjectDto(task.Type, task.Id), new NhAssistantRuleScriptSubjectDto("unknown-type", "x")]),
            new NhAssistantRuleScriptAuthor(Administrator));

        Assert.True(dryRun.Success, string.Join(",", dryRun.GetResultItems().Select(item => item.Name)));
        Assert.Equal((2, 1), (dryRun.Data!.SubjectCount, dryRun.Data.CompletedCount));
        var onTask = dryRun.Data.Subjects[0];
        Assert.Equal("completed", onTask.Status);
        Assert.Equal(
            [(TagRisk, NhAssistantRuleScriptWouldOutcomes.Run), (FlagRisk, NhAssistantRuleScriptWouldOutcomes.Propose)],
            onTask.Writes.Select(write => (write.ToolId, write.Outcome)).ToArray());
        Assert.Contains("risk-1", onTask.Writes[0].Input, StringComparison.Ordinal);
        Assert.Equal(("projects_flag_risk_v1", FlagRisk, "Check it."), (onTask.Proposals.Single().Tool, onTask.Proposals.Single().ToolId, onTask.Proposals.Single().Rationale));
        Assert.Equal((Kind, "plan"), (onTask.Alerts.Single().Kind, onTask.Alerts.Single().Key));
        Assert.Equal("failed", dryRun.Data.Subjects[1].Status);

        // No effects: only the read reached a tool; no proposal, no alert, no delivery and no thread.
        Assert.Equal(["projects.get"], risks.RanTools);
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            Assert.Empty(await context.Approvals.ToListAsync());
            Assert.Empty(await context.Alerts.ToListAsync());
            Assert.Empty(await context.AlertDeliveries.ToListAsync());
            Assert.Empty(await context.ToolInvocations.ToListAsync());
            Assert.Equal(1, await context.RuleScriptRunRecords.CountAsync());
        }

        // The latest dry run is kept, the one before goes.
        var again = await ruleScripts.DryRunAsync(AgentId, null, 1, new NhAssistantRuleScriptDryRunRequest([new NhAssistantRuleScriptSubjectDto(task.Type, task.Id)]), new NhAssistantRuleScriptAuthor(Administrator));
        var latest = await ruleScripts.GetDryRunAsync(AgentId, null, 1);
        Assert.Equal(again.Data!.Id, latest!.Id);
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            Assert.Equal(1, await context.RuleScriptRunRecords.CountAsync());
        }

        var audited = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminRuleScriptDryRun).ToArray();
        Assert.Equal(["stopped", "completed"], audited.Select(evt => evt.ResultCode));
        Assert.All(audited, evt => Assert.Equal((AgentId, 1, saved.Data!.ScriptHash), (evt.ObjectId, evt.RuleScriptVersion!.Value, evt.RuleScriptHash)));

        // Too many or no subjects are refused before anything runs.
        var none = await ruleScripts.DryRunAsync(AgentId, null, 1, new NhAssistantRuleScriptDryRunRequest([]), new NhAssistantRuleScriptAuthor(Administrator));
        Assert.Equal(NhAssistantRuleScriptCodes.SubjectsInvalid, CodeOf(none));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_candidate_in_shadow_runs_next_to_the_code_rule_without_effects_and_goes_live_by_another_administrator(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, fourEyes: true);
        var task = TaskSubject(Guid.NewGuid());
        var rules = host.Services.GetRequiredService<AlertRuleScript>();

        // Four eyes: nobody saves straight live.
        Assert.Equal(NhAssistantRuleScriptCodes.FourEyes, CodeOf(await SaveAsync(host, WritingScript, [TagRisk, FlagRisk], live: true)));
        var candidate = await SaveAsync(host, WritingScript, [TagRisk, FlagRisk], live: false, shadow: true);
        Assert.True(candidate.Success);
        Assert.True(candidate.Data!.Shadow);

        // The code rule still runs the run; the candidate runs next to it and records what it would do.
        var result = await RunAsync(host, task);
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        Assert.Equal(1, rules.Runs);
        Assert.Equal(["projects.get"], host.Services.GetRequiredService<TestRiskToolRecorder>().RanTools);
        Assert.Empty(await AlertsAboutAsync(host, task));
        Assert.Empty(await host.Services.GetRequiredService<NhAssistantRunStore>().GetRunApprovalsAsync(result.RunId!.Value, CancellationToken.None));
        await using var scope = host.Services.CreateAsyncScope();
        var ruleScripts = scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>();
        var shadowRun = Assert.Single(await ruleScripts.GetShadowRunsAsync(AgentId, null, 1));
        Assert.Equal((NhAssistantRuleScriptRunKinds.Shadow, result.RunId), (shadowRun.Kind, shadowRun.RunId));
        Assert.Equal(2, shadowRun.Subjects.Single().Writes.Count);

        // Going live: the author is refused under four eyes, a stale revision conflicts, another
        // administrator sets it live and sees that it has no dry run yet.
        var own = await ruleScripts.SetLiveAsync(AgentId, null, 1, new NhAssistantRuleScriptChangeRequest(1), new NhAssistantRuleScriptAuthor(Administrator));
        var stale = await ruleScripts.SetLiveAsync(AgentId, null, 1, new NhAssistantRuleScriptChangeRequest(0), new NhAssistantRuleScriptAuthor(OtherAdministrator));
        var live = await ruleScripts.SetLiveAsync(AgentId, null, 1, new NhAssistantRuleScriptChangeRequest(1, "Tried in shadow."), new NhAssistantRuleScriptAuthor(OtherAdministrator));
        Assert.Equal(NhAssistantRuleScriptCodes.FourEyes, CodeOf(own));
        Assert.Equal(NhAssistantAdminErrorCodes.VersionConflict, CodeOf(stale));
        Assert.True(live.Success);
        Assert.Equal((NhAssistantRuleScriptStatuses.Live, OtherAdministrator, 2), (live.Data!.Version.Status, live.Data.Version.LiveBy, live.Data.Version.Revision));
        Assert.Null(live.Data.LatestDryRun);

        // A second version, dry-run, then live: the first is superseded and going live shows the dry run.
        var second = await SaveAsync(host, "done('Nothing.');", [], live: false, revision: 2);
        await ruleScripts.DryRunAsync(AgentId, null, 2, new NhAssistantRuleScriptDryRunRequest([new NhAssistantRuleScriptSubjectDto(task.Type, task.Id)]), new NhAssistantRuleScriptAuthor(Administrator));
        var secondLive = await ruleScripts.SetLiveAsync(AgentId, null, 2, new NhAssistantRuleScriptChangeRequest(3), new NhAssistantRuleScriptAuthor(OtherAdministrator));
        Assert.True(second.Success);
        Assert.NotNull(secondLive.Data!.LatestDryRun);
        Assert.Equal(NhAssistantRuleScriptCodes.NotCandidate, CodeOf(await ruleScripts.DiscardAsync(AgentId, null, 2, new NhAssistantRuleScriptChangeRequest(4), new NhAssistantRuleScriptAuthor(Administrator))));
        var listed = (await ruleScripts.GetAsync(AgentId, null))!;
        Assert.Equal((2, null, 4), (listed.LiveVersion, listed.CandidateVersion, listed.Revision));
        Assert.Equal([NhAssistantRuleScriptStatuses.Live, NhAssistantRuleScriptStatuses.Superseded], listed.Versions.Select(version => version.Status));
        Assert.Equal(nameof(AlertTestRules), listed.CodeRule);
        var compared = await ruleScripts.CompareAsync(AgentId, null, 2, null);
        Assert.Equal(1, compared.Data!.OtherVersion);
        Assert.Equal([TagRisk, FlagRisk], compared.Data.WritesRemoved.Order(StringComparer.Ordinal).Reverse());

        // Content-free audit of each step.
        Assert.Equal(
            [NhAssistantAuditEventKind.AdminRuleScriptSaved, NhAssistantAuditEventKind.AdminRuleScriptShadowChanged, NhAssistantAuditEventKind.AdminRuleScriptLive],
            host.Business.Events.Where(evt => evt.RuleScriptVersion == 1).Select(evt => evt.Kind));
        Assert.DoesNotContain(host.Business.Events, evt => evt.ResultCode?.Contains("shadow.", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_script_on_the_whole_application_never_resolves_alerts_by_omission(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        var task = TaskSubject(Guid.NewGuid());
        await SaveAsync(host, $$"""
            if (subject.wholeApplication) { done("Nothing on the application."); }
            else { alerts.raise({ kind: "{{Kind}}", key: "plan", parameters: { task: subject.id } }); done("Warned."); }
            """, [], live: true);
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunAsync(host, task)).Outcome);
        Assert.Single(await AlertsAboutAsync(host, task));

        // The application-wide run raises nothing about the task: its alert stays open.
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunAsync(host, NhAiSystemSubjects.Global)).Outcome);
        Assert.Equal(NhAssistantAlertStatuses.Open, Assert.Single(await AlertsAboutAsync(host, task)).Status);

        // A run on the task that raises nothing resolves it, as the kind asks.
        await SaveAsync(host, "done('Planned.');", [], live: true, revision: 1);
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunAsync(host, task)).Outcome);
        Assert.Equal(NhAssistantAlertStatuses.Resolved, Assert.Single(await AlertsAboutAsync(host, task)).Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Only_an_administrator_saves_and_sets_a_version_live_over_http(AssistantTestProvider provider)
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services => services.AddNewHeapPlatformAIScripting(),
            assistantBuilder: assistant => assistant.UseRuleScriptsAsData(scripts => scripts.ForAgent(AgentId)),
            provider: provider);
        using var user = app.CreateClient();
        using var admin = app.CreateClient(admin: true);
        var route = $"/api/assistant/admin/agents/{AgentId}/rule-script";
        var body = new { script = "done('Nothing to do.');", writes = Array.Empty<string>(), expectedRevision = 0 };

        // Without the administration policy nothing is read, saved or set live.
        using var forbiddenRead = await user.GetAsync(route);
        using var forbiddenSave = await user.PostAsync(route + "/versions", ServerSentEventReader.Json(body));
        using var forbiddenLive = await user.PostAsync(route + "/versions/1/live", ServerSentEventReader.Json(new { expectedRevision = 1 }));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenSave.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenLive.StatusCode);

        using var saved = await admin.PostAsync(route + "/versions", ServerSentEventReader.Json(body));
        Assert.Equal(System.Net.HttpStatusCode.OK, saved.StatusCode);
        using var live = await admin.PostAsync(route + "/versions/1/live", ServerSentEventReader.Json(new { expectedRevision = 1 }));
        var liveJson = await ReadJsonAsync(live);
        Assert.Equal(System.Net.HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("live", liveJson.GetProperty("version").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, liveJson.GetProperty("latestDryRun").ValueKind);
        using var again = await admin.PostAsync(route + "/versions/1/live", ServerSentEventReader.Json(new { expectedRevision = 2 }));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(NhAssistantRuleScriptCodes.NotCandidate, (await ReadJsonAsync(again)).GetProperty("code").GetString());
        using var outside = await admin.GetAsync("/api/assistant/admin/agents/no-such-agent/rule-script");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, outside.StatusCode);
        using var dryRunNone = await admin.GetAsync(route + "/versions/1/dry-run");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, dryRunNone.StatusCode);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_live_rule_script_travels_in_an_agent_package_and_its_lock_holds(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider);
        await SaveAsync(host, "done('One.');", [TagRisk], live: true);
        await SaveAsync(host, "done('Candidate.');", [], live: false, revision: 1);
        await using var scope = host.Services.CreateAsyncScope();
        var packages = scope.ServiceProvider.GetRequiredService<INhAssistantAgentPackages>();
        var locks = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal)
        {
            [AgentId] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [NhAssistantPackagePartKinds.RuleScript] = ["script"] }
        };

        // Only the live version travels, with its writes and the hash of its part.
        var exported = await packages.ExportAsync([AgentId], new NhAssistantAgentPackageExportOptions(Locks: locks), Administrator);
        Assert.True(exported.Success, string.Join(",", exported.GetResultItems().Select(item => item.Name)));
        var agent = Assert.Single(exported.Data!.Package.Agents);
        var carried = Assert.Single(agent.RuleScripts!);
        Assert.Equal((null, "done('One.');"), (carried.Skill, carried.Script));
        Assert.Equal([TagRisk], carried.Writes);
        Assert.True(agent.Hashes!.ContainsKey(NhAssistantPackagePartKinds.RuleScript));
        var read = NhAssistantAgentPackageFormat.Read(NhAssistantAgentPackageFormat.Write(exported.Data.Package));
        Assert.True(read.Success);

        // A local change, then the import: the package's version is the next live version, linked and locked.
        await SaveAsync(host, "done('Two.');", [], live: true, revision: 2);
        var imported = await packages.ImportAsync(read.Data!, new NhAssistantAgentPackageImportOptions(), Administrator);
        Assert.True(imported.Success, string.Join(",", imported.GetResultItems().Select(item => item.Name)));
        var part = Assert.Single(imported.Data!.Parts, item => item.Part == NhAssistantPackagePartKinds.RuleScript);
        Assert.Equal(NhAssistantPackagePartOutcomes.Changed, part.Outcome);
        var listed = (await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().GetAsync(AgentId, null))!;
        Assert.Equal(4, listed.LiveVersion);
        Assert.Equal(("done('One.');", NhAssistantRuleScriptStatuses.Live), (listed.Versions[0].Script, listed.Versions[0].Status));

        // The lock: another script is refused, the package's own one with other writes is not.
        var other = await SaveAsync(host, "done('Three.');", [], live: true, revision: listed.Revision);
        var same = await SaveAsync(host, "done('One.');", [], live: true, revision: listed.Revision);
        Assert.Contains(other.GetResultItems(), item => item.Name == "field:script");
        Assert.True(same.Success, string.Join(",", same.GetResultItems().Select(item => item.Name)));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<TaskResult<NhAssistantRuleScriptVersionDto>> SaveAsync(
        AssistantTestHost host,
        string script,
        IReadOnlyList<string> writes,
        bool live,
        int revision = 0,
        bool shadow = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>().SaveAsync(
            AgentId,
            null,
            new NhAssistantRuleScriptSaveRequest(script, writes, revision, null, live, shadow),
            new NhAssistantRuleScriptAuthor(Administrator));
    }

    private static string? CodeOf(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => name?.StartsWith("assistant-", StringComparison.Ordinal) == true);
    }

    private static NhAiSubjectRef TaskSubject(Guid taskId)
    {
        return new NhAiSubjectRef("task", taskId.ToString("D"));
    }

    private static async Task<IReadOnlyList<AssistantAlert>> AlertsAboutAsync(AssistantTestHost host, NhAiSubjectRef subject)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Alerts
            .AsNoTracking()
            .Where(alert => alert.SubjectType == subject.Type && alert.SubjectId == subject.Id)
            .ToListAsync();
    }

    /// <summary>
    /// A headless run of the agent on <paramref name="subject"/> in its live thread, without an initiator.
    /// </summary>
    private static async Task<RunResult> RunAsync(AssistantTestHost host, NhAiSubjectRef subject)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "round",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var agentTurn = new NhAssistantAgentTurnContext(thread, info, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false);
        var result = await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Round.")]),
            CancellationToken.None);
        return new RunResult(result.Outcome, result.ErrorCode, result.Kind, result.InputTokens, result.OutputTokens, run.Id, thread.ConversationId);
    }

    private sealed record RunResult(string Outcome, string? ErrorCode, string Kind, int InputTokens, int OutputTokens, Guid? RunId, Guid ConversationId);

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, bool fourEyes = false, int maxWrites = 2)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(new AssistantTestClock(Start));
                services.AddSingleton<AlertTestSubjects>();
                services.AddSingleton<AlertRuleScript>();
                services.AddSingleton<TestRiskToolRecorder>();
                services.AddSingleton(Substitute.For<INhBackgroundOperationService>());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, AlertPrincipalFactory>());
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestRiskToolCatalog>());
                services.AddNewHeapPlatformAIScripting();
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<AlertTestSubjectResolver>()
                .AddApprovalPolicy("risk-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .AddAlertKind(Kind, kind => kind
                    .DisplayName("tests.alerts.task-needs-plan")
                    .Title((_, parameters) => parameters["task"] + " has no plan")
                    .ResolveWhenNotRaised())
                .ConfigureAgent(AgentId, agent => agent
                    .WorksOnAllObjects()
                    .WorksOnApplication()
                    .Grant("projects.*")
                    // A person answers for the runs, so a write that needs no approval runs.
                    .ServiceAccount("service-1")
                    .UseApprovalPolicy("risk-review")
                    .UseRuleHandler<AlertTestRules>()
                    .Alerts(Kind))
                .UseRuleScriptsAsData(scripts => scripts
                    .ForAgent(AgentId, ceiling => ceiling
                        .AllowWrites(TagRisk, FlagRisk)
                        .MaxWritesPerRun(maxWrites))
                    .RequireFourEyes(fourEyes)));
    }
}
