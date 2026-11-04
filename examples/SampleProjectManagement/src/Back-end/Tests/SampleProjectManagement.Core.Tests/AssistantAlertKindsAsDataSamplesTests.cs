using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Scripting;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-395: alert kinds of agents created in the administration (FB93). An administrator creates the
/// project reminder in the administration, finds the kinds the application registers in the run catalog,
/// lists the reminder kind in its run settings (a kind the code does not register is refused at its field)
/// and writes its rules as a rule script, which another administrator sets live under four eyes. Started on
/// a project, its run raises the kind for the project's owner without a model; a version that raises a kind
/// the run settings do not list stops and raises nothing. The library tests prove the same on SQL Server
/// and PostgreSQL.
/// </summary>
public sealed class AssistantAlertKindsAsDataSamplesTests(AssistantAlertKindsAsDataSampleHost host) : IClassFixture<AssistantAlertKindsAsDataSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string AgentId = SampleAlertKindsAsDataComposition.AgentId;
    private const string KindId = SampleAlertKindsAsDataComposition.KindId;

    [Fact]
    public async Task SPM_395_an_agent_created_in_the_administration_raises_the_kind_its_run_settings_list_and_no_other()
    {
        var author = new NhAssistantRuleScriptAuthor("spm-395-author");
        var reviewer = new NhAssistantRuleScriptAuthor("spm-395-reviewer");
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        await using var scope = host.Services.CreateAsyncScope();
        var configuration = scope.ServiceProvider.GetRequiredService<INhAssistantAgentConfiguration>();
        var created = await configuration.CreateAgentAsync(
            new NhAssistantAgentRequest(
                AgentId,
                "Project reminder",
                "Reminds the owner of a project to review it.",
                "Remind the owner of the project to review it.",
                [ProjectDocumentAiTools.ReadToolId],
                NhAiAutonomyLevel.Propose,
                OfferedInChat: false,
                WorksOnSubjectTypes: [ProjectSubjects.Type]),
            "spm-395-administrator",
            TestContext.Current.CancellationToken);
        Assert.True(created.Success, string.Join(",", created.GetResultItems().Select(item => item.Name)));

        // The run catalog lists every kind the application registers, with its name and urgency.
        var catalog = (await administrator.GetFromJsonAsync<NhAssistantRunCatalogDto>("/api/assistant/admin/run-catalog", Json))!;
        Assert.Contains(new NhAssistantRunAlertKindDto(KindId, SampleAlertKindsAsDataComposition.KindNameKey, "info", false), catalog.AlertKinds);
        Assert.Contains(catalog.AlertKinds, kind => kind.Id == SampleRuleScriptsComposition.KindId);

        // A kind the application does not register is refused at its field; the reminder kind is listed.
        const string runSettings = $"/api/assistant/admin/agents/{AgentId}/run-settings";
        using (var unknown = await administrator.PutAsJsonAsync(runSettings, new { accountability = "agent", alertKinds = new[] { KindId, "no-such-kind" } }, Json))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            var errors = (await unknown.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("errors");
            Assert.Equal("not-found", errors.GetProperty("alertKinds")[0].GetString());
        }
        using (var listed = await administrator.PutAsJsonAsync(runSettings, new { accountability = "agent", retryCount = 0, alertKinds = new[] { KindId } }, Json))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            var settings = (await listed.Content.ReadFromJsonAsync<NhAssistantAgentRunSettingsDto>(Json))!;
            Assert.Equal([KindId], settings.AlertKinds);
            Assert.True(settings.InEffect);
        }

        // Its rule script sees the kind its run settings list, and goes live by another administrator.
        var ruleScripts = scope.ServiceProvider.GetRequiredService<INhAssistantRuleScripts>();
        Assert.Equal([KindId], (await ruleScripts.GetCatalogAsync(AgentId, null, cancellationToken: TestContext.Current.CancellationToken)).Data!.AlertKinds);
        await SetLiveAsync(ruleScripts, SampleAlertKindsAsDataComposition.ReminderScript, 0, author, reviewer);

        // Started on a project, the run raises the kind for the project's owner without a model.
        var project = await host.CreateProjectAsync("REM", host.OwnerId, ProjectStatus.Active, host.Clock.GetUtcNow().AddDays(20));
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var run = await StartAndWaitAsync(owner, project);
        Assert.Equal((NhAssistantRunStatuses.Completed, NhAssistantRunKinds.Rule), (run.Status, run.Kind));
        Assert.Equal(0, run.InputTokens + run.OutputTokens);
        var alert = Assert.Single((await ListAlertsAsync(owner, project)).Items);
        Assert.Equal(KindId, alert.KindId);

        // A version that raises a kind the run settings do not list, the document check's, stops at it.
        await SetLiveAsync(ruleScripts, SampleAlertKindsAsDataComposition.UnlistedKindScript, 2, author, reviewer);
        var other = await host.CreateProjectAsync("UNL", host.OwnerId, ProjectStatus.Active, host.Clock.GetUtcNow().AddDays(20));
        var refused = await StartAndWaitAsync(owner, other);
        Assert.Equal((NhAssistantRunStatuses.Failed, NhAiScriptCodes.RuntimeError), (refused.Status, refused.FailureCode));
        Assert.Empty((await ListAlertsAsync(owner, other)).Items);
    }

    /// <summary>
    /// Saves a version as the author and sets it live as the reviewer, as four eyes of the sample's ceiling ask.
    /// </summary>
    private static async Task SetLiveAsync(
        INhAssistantRuleScripts ruleScripts,
        string script,
        int revision,
        NhAssistantRuleScriptAuthor author,
        NhAssistantRuleScriptAuthor reviewer)
    {
        var saved = await ruleScripts.SaveAsync(AgentId, null, new NhAssistantRuleScriptSaveRequest(script, [], revision, "Remind the owner."), author);
        Assert.True(saved.Success, string.Join(",", saved.GetResultItems().Select(item => item.Name)));
        var live = await ruleScripts.SetLiveAsync(AgentId, null, saved.Data!.Version, new NhAssistantRuleScriptChangeRequest(revision + 1, "Looks right."), reviewer);
        Assert.True(live.Success, string.Join(",", live.GetResultItems().Select(item => item.Name)));
    }

    private static async Task<NhAssistantRunDto> StartAndWaitAsync(HttpClient client, Guid projectId)
    {
        using var started = await client.PostAsJsonAsync(
            "/api/assistant/threads/start",
            new NhAssistantStartAgentRequest(AgentId, ProjectSubjects.Type, projectId.ToString("D"), "Remind the owner."),
            Json);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var conversationId = (await started.Content.ReadFromJsonAsync<NhAssistantTriggerReceiptDto>(Json))!.ConversationId!.Value;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        NhAssistantRunDto[] runs = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            runs = (await client.GetFromJsonAsync<NhAssistantRunListDto>($"/api/assistant/threads/{conversationId}/runs", Json))?.Items.ToArray() ?? [];
            if (runs.Length == 1 && NhAssistantRunStatuses.IsTerminal(runs[0].Status))
            {
                return runs[0];
            }

            await Task.Delay(AssistantAgentRunSampleHost.PollInterval);
        }

        throw new TimeoutException($"The project reminder's run on project {projectId} did not end within 90 s; it has {runs.Length} runs.");
    }

    private static async Task<NhAssistantAlertListDto> ListAlertsAsync(HttpClient client, Guid projectId)
    {
        return (await client.GetFromJsonAsync<NhAssistantAlertListDto>(
            $"/api/assistant/alerts?subjectType={ProjectSubjects.Type}&subjectId={ProjectSubjects.IdOf(projectId)}",
            Json))!;
    }
}

/// <summary>
/// SPM-395: the document check's host of SPM-393 with the alert kinds of agents created in the
/// administration of Program.cs, so the reminder kind and the document check's kind are both registered.
/// </summary>
public sealed class AssistantAlertKindsAsDataSampleHost(SamplePostgreSqlServer postgreSql) : AssistantRuleScriptSampleHost(postgreSql)
{
    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSampleAlertKindsAsData();
    }
}
