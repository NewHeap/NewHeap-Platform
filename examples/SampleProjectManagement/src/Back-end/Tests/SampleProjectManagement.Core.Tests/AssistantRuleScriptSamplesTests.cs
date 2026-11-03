using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-393: the document check's rules are a rule script. An administrator reads what the script may call,
/// saves it as a candidate with the review request opted in as its one write, dry-runs it on a project due
/// within three days without effects, and another administrator sets it live under four eyes, seeing that
/// dry run. The morning round then runs the script without a model: the project without a document raises
/// the warning for its owner and records a proposal to ask the review board, which waits for approval.
/// </summary>
public sealed class AssistantRuleScriptSamplesTests(AssistantRuleScriptSampleHost host) : IClassFixture<AssistantRuleScriptSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string AgentId = SampleRuleScriptsComposition.AgentId;
    private const string ReviewTool = ProjectAiReviewTools.RequestReviewToolId;

    [Fact]
    public async Task SPM_393_a_rule_script_is_dry_run_set_live_by_four_eyes_and_warns_and_proposes_a_review_in_its_round()
    {
        var project = await host.CreateProjectAsync("DOC", host.OwnerId, ProjectStatus.Active, host.Clock.GetUtcNow().AddDays(2));
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        var author = new NhAssistantRuleScriptAuthor("spm-393-author");
        var reviewer = new NhAssistantRuleScriptAuthor("spm-393-reviewer");
        await using var scope = host.Services.CreateAsyncScope();
        var ruleScripts = scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>();

        // What the script sees: the documents read, and the review request as the one write of the ceiling.
        var catalog = (await ruleScripts.GetCatalogAsync(AgentId, null)).Data!;
        Assert.Contains(catalog.Tools, tool => tool.ToolId == ProjectDocumentAiTools.ReadToolId && !tool.Write && tool.Callable);
        Assert.Contains(catalog.Tools, tool => tool.ToolId == ReviewTool && tool.Write && tool.InCeiling);
        Assert.Equal([SampleRuleScriptsComposition.KindId], catalog.AlertKinds);
        Assert.Equal(1, catalog.MaxWritesPerRun);

        // A candidate with the write opted in, then a dry run on the project: what it would do, and nothing happens.
        var saved = await ruleScripts.SaveAsync(
            AgentId,
            null,
            new NhAssistantRuleScriptSaveRequest(SampleRuleScriptsComposition.DocumentCheckScript, [ReviewTool], 0, "Warn about projects without a document."),
            author);
        Assert.True(saved.Success, string.Join(",", saved.GetResultItems().Select(item => item.Name)));
        var dryRun = await ruleScripts.DryRunAsync(
            AgentId,
            null,
            1,
            new NhAssistantRuleScriptDryRunRequest([new NhAssistantRuleScriptSubjectDto(ProjectSubjects.Type, ProjectSubjects.IdOf(project))]),
            author);
        Assert.True(dryRun.Success, string.Join(",", dryRun.GetResultItems().Select(item => item.Name)));
        var onProject = Assert.Single(dryRun.Data!.Subjects);
        Assert.Equal("completed", onProject.Status);
        Assert.Equal(SampleRuleScriptsComposition.KindId, Assert.Single(onProject.Alerts).Kind);
        var write = Assert.Single(onProject.Writes);
        Assert.Equal((ReviewTool, NhAssistantRuleScriptWouldOutcomes.Propose), (write.ToolId, write.Outcome));
        Assert.Equal(0, host.Reviews.Attempts(project));
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var about = $"subjectType={ProjectSubjects.Type}&subjectId={ProjectSubjects.IdOf(project)}";
        Assert.Empty((await ListAlertsAsync(owner, about)).Items);

        // Four eyes: the author does not set the version live; another administrator does and sees the dry run.
        var own = await ruleScripts.SetLiveAsync(AgentId, null, 1, new NhAssistantRuleScriptChangeRequest(1), author);
        Assert.Equal(NhAssistantRuleScriptCodes.FourEyes, own.GetResultItems().First().Name);
        var live = await ruleScripts.SetLiveAsync(AgentId, null, 1, new NhAssistantRuleScriptChangeRequest(1, "Dry run looked right."), reviewer);
        Assert.True(live.Success, string.Join(",", live.GetResultItems().Select(item => item.Name)));
        Assert.Equal(dryRun.Data.Id, live.Data!.LatestDryRun!.Id);

        // The morning round runs the live script without a model: a warning and a proposal that waits.
        host.Services.GetRequiredService<IRecurringJobManager>().Trigger("nh-assistant-trigger-" + SampleRuleScriptsComposition.TriggerId);
        var conversationId = await WaitForThreadAsync(owner, project);
        var run = Assert.Single(await WaitForRunsAsync(owner, conversationId, 1));
        Assert.Equal((NhAssistantRunStatuses.Completed, NhAssistantRunKinds.Rule, 1), (run.Status, run.Kind, run.ProposalCount));
        Assert.Equal(0, run.InputTokens + run.OutputTokens);
        var alert = Assert.Single((await ListAlertsAsync(owner, about)).Items);
        Assert.Equal(SampleRuleScriptsComposition.KindId, alert.KindId);
        // The review request needs approval, so the board has not been asked yet.
        Assert.Equal(0, host.Reviews.Attempts(project));
    }

    private static async Task<NhAssistantAlertListDto> ListAlertsAsync(HttpClient client, string about)
    {
        return (await client.GetFromJsonAsync<NhAssistantAlertListDto>("/api/assistant/alerts?" + about, Json))!;
    }

    private static async Task<Guid> WaitForThreadAsync(HttpClient client, Guid projectId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var lookup = await client.GetFromJsonAsync<NhAssistantThreadLookupDto>(
                $"/api/assistant/threads?agentId={AgentId}&subjectType={ProjectSubjects.Type}&subjectId={projectId:D}",
                Json);
            if (lookup?.Thread is { } thread)
            {
                return thread.ConversationId;
            }

            await Task.Delay(AssistantAgentRunSampleHost.PollInterval);
        }

        throw new TimeoutException($"The document check has no thread about project {projectId} after 90 s.");
    }

    private static async Task<NhAssistantRunDto[]> WaitForRunsAsync(HttpClient client, Guid conversationId, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        NhAssistantRunDto[] runs = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            runs = (await client.GetFromJsonAsync<NhAssistantRunListDto>($"/api/assistant/threads/{conversationId}/runs", Json))?.Items.ToArray() ?? [];
            if (runs.Length >= count && runs.All(run => NhAssistantRunStatuses.IsTerminal(run.Status)))
            {
                return runs;
            }

            await Task.Delay(AssistantAgentRunSampleHost.PollInterval);
        }

        throw new TimeoutException($"Waited 90 s for {count} ended runs of the document check; it last had {runs.Length}.");
    }
}

/// <summary>
/// SPM-393: the agent-runs host with the document check of Program.cs. The documents tool comes with the
/// sample's files in Program.cs; this host registers it over a project store that keeps no document.
/// </summary>
public sealed class AssistantRuleScriptSampleHost(SamplePostgreSqlServer postgreSql) : AssistantAgentRunSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSingleton<IProjectAiDocumentService, NoProjectDocuments>();
        services.AddScoped<ProjectDocumentAiTools>();
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ProjectDocumentAiToolsNhAiCatalog>());
        services.AddSampleRuleScripts();
    }
}

/// <summary>
/// SPM-393: projects without documents, as a new project is.
/// </summary>
public sealed class NoProjectDocuments : IProjectAiDocumentService
{
    public Task<IReadOnlyList<NewHeap.Platform.AI.NhAiFileReference>> ListForAiAsync(
        Guid divisionId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<NewHeap.Platform.AI.NhAiFileReference>>([]);
    }
}
