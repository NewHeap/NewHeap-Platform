using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-392: the deadline watch warns the people of a project due today with an alert, not a proposal.
/// Two morning rounds are one alert, the application's own warning of more people reaches only those
/// who read the project, and the watch resolves the alert once the tasks are done.
/// </summary>
public sealed class AssistantAlertSamplesTests(AssistantAlertSampleHost host) : IClassFixture<AssistantAlertSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_392_the_deadline_watch_warns_a_project_due_today_once_and_resolves_it_when_its_tasks_are_done()
    {
        // The rounds and the warning all fall on one day in the sample's time zone.
        host.MoveToNextMorning();
        var today = SampleDeadlineAlertsComposition.DayOf(host.Clock.GetUtcNow());
        var project = await host.CreateProjectAsync(
            "DUE",
            host.OwnerId,
            ProjectStatus.Active,
            SampleDeadlineAlertsComposition.StartOf(today).AddHours(17));
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        await host.AddMemberAsync(project, host.ReaderId, ProjectMemberRole.Reader);
        var tasks = await host.AddTasksAsync(project, "Write the report", "Book the room");
        using var owner = host.CreateClient(host.OwnerId.ToString());

        // The first morning round raises the alert for the owner and the participant.
        host.StartMorningRound();
        var conversationId = await WaitForThreadAsync(owner, project);
        var first = Assert.Single(await WaitForRunsAsync(owner, conversationId, 1));
        Assert.Equal((NhAssistantRunStatuses.Completed, NhAssistantRunKinds.Rule), (first.Status, first.Kind));
        Assert.Equal(0, first.InputTokens + first.OutputTokens);

        // A later round raises the same project and day again: still one alert, unchanged.
        host.StartMorningRound();
        Assert.All(await WaitForRunsAsync(owner, conversationId, 2), run => Assert.Equal(NhAssistantRunStatuses.Completed, run.Status));
        var warned = await WarnAsync(project);
        Assert.True(warned.Success);
        Assert.Equal(
            (NhAssistantAlertOutcome.Unchanged, 1, false, 2),
            (warned.Data!.Outcome, warned.Data.Revision, warned.Data.Shadow, warned.Data.RecipientCount));

        // The application warns a reader and an outsider about it as well: only the reader reads the
        // project, so the outsider receives nothing.
        var colleagues = await WarnAsync(project, host.ReaderId, host.OutsiderId);
        Assert.Equal(warned.Data.AlertId, colleagues.Data!.AlertId);
        Assert.Equal(3, colleagues.Data.RecipientCount);

        // The tasks are done: the next round resolves the alert, so the application finds nothing open.
        await host.CompleteTasksAsync(tasks);
        host.StartMorningRound();
        Assert.All(await WaitForRunsAsync(owner, conversationId, 3), run => Assert.Equal(NhAssistantRunStatuses.Completed, run.Status));
        var done = await WarnAsync(project);
        Assert.False(done.Success);
        Assert.Contains(done.GetResultItems(), item => item.Name == SampleDeadlineAlerts.NotDueToday);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<SampleDeadlineAlerts>().ResolveAsync(project)).Data);
    }

    private async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantAlertReceipt>> WarnAsync(Guid projectId, params Guid[] people)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SampleDeadlineAlerts>().WarnAsync(projectId, people);
    }

    private static async Task<Guid> WaitForThreadAsync(HttpClient client, Guid projectId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var lookup = await client.GetFromJsonAsync<NhAssistantThreadLookupDto>(
                $"/api/assistant/threads?agentId={SampleDeadlineAlertsComposition.AgentId}&subjectType={ProjectSubjects.Type}&subjectId={projectId:D}",
                Json);
            if (lookup?.Thread is { } thread)
            {
                return thread.ConversationId;
            }

            await Task.Delay(AssistantAgentRunSampleHost.PollInterval);
        }

        throw new TimeoutException($"The deadline watch has no thread about project {projectId} after 90 s.");
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

        throw new TimeoutException(
            $"Waited 90 s for {count} ended runs of the deadline watch; it last had {runs.Length}: {string.Join(", ", runs.Select(run => run.Status))}.");
    }
}

/// <summary>
/// SPM-392: the agent-runs host with the deadline watch of Program.cs.
/// </summary>
public sealed class AssistantAlertSampleHost(SamplePostgreSqlServer postgreSql) : AssistantAgentRunSampleHost(postgreSql)
{
    private int _rounds;

    protected override void ConfigureAssistant(IServiceCollection services)
    {
        base.ConfigureAssistant(services);
        services.AddSampleDeadlineAlerts();
    }

    /// <summary>
    /// Moves the host's clock to the next eight o'clock in the sample's time zone, so a test's rounds fall
    /// on one day.
    /// </summary>
    public void MoveToNextMorning()
    {
        var now = Clock.GetUtcNow();
        var day = SampleDeadlineAlertsComposition.DayOf(now);
        var morning = SampleDeadlineAlertsComposition.StartOf(day).AddHours(8);
        if (morning <= now)
        {
            morning = SampleDeadlineAlertsComposition.StartOf(day.AddDays(1)).AddHours(8);
        }

        Clock.Advance(morning - now);
    }

    /// <summary>
    /// Starts a round of the watch now, as its schedule does on working-day mornings. Each round falls an
    /// hour after the previous one, so it is a tick of its own and not the same tick again.
    /// </summary>
    public void StartMorningRound()
    {
        if (_rounds++ > 0)
        {
            Clock.Advance(TimeSpan.FromHours(1));
        }

        Services.GetRequiredService<IRecurringJobManager>()
            .Trigger("nh-assistant-trigger-" + SampleDeadlineAlertsComposition.TriggerId);
    }

    public async Task<Guid[]> AddTasksAsync(Guid projectId, params string[] titles)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        var tasks = titles.Select(title => new ProjectTask { Id = Guid.NewGuid(), ProjectId = projectId, Title = title }).ToArray();
        dbContext.Set<ProjectTask>().AddRange(tasks);
        await dbContext.SaveChangesAsync();
        return tasks.Select(task => task.Id).ToArray();
    }

    public async Task CompleteTasksAsync(IReadOnlyCollection<Guid> taskIds)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        foreach (var task in dbContext.Set<ProjectTask>().Where(item => taskIds.Contains(item.Id)))
        {
            task.IsCompleted = true;
            task.CompletedAt = Clock.GetUtcNow();
        }

        await dbContext.SaveChangesAsync();
    }
}
