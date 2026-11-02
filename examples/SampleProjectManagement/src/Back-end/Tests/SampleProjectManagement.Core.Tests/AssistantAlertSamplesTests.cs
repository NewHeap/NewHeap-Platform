using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-392: the deadline watch warns the people of a project due today with an alert, not a proposal.
/// Two morning rounds are one alert for the owner and the members who work on the project, the
/// application's own warning of more people reaches only those who read the project, each of them reads
/// it through <c>GET alerts</c> in their language and marks it read or dismisses it, and an outsider reads
/// nothing. Ticking off the last task resolves the alert at once, because the task consumer revalidates
/// the project's alerts, and a round that no longer raises the watch's warning resolves it by omission.
/// The warning arrives in the 07:30 digest where each person works, and not for a person who muted its
/// kind.
/// </summary>
public sealed class AssistantAlertSamplesTests(AssistantAlertSampleHost host) : IClassFixture<AssistantAlertSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The function name of the platform tool through which an agent's model raises alerts (FB87-9).</summary>
    private const string NhAssistantAlertModelToolName = "raise_alert";

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

        // The first morning round raises the alert for the owner and the participant, whom the kind
        // chooses as the members who work on the project; the reader is not one of them.
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

        // Each of them reads the one stored alert in their own list, written in their language, with the
        // project as its subject and its link; the badge counts it as unread.
        // Only this project's alerts: the other tests of the shared host warn the same people about theirs.
        var about = $"subjectType={ProjectSubjects.Type}&subjectId={ProjectSubjects.IdOf(project)}";
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        using var reader = host.CreateClient(host.ReaderId.ToString());
        using var outsider = host.CreateClient(host.OutsiderId.ToString());
        var alert = Assert.Single((await ListAlertsAsync(owner, about)).Items);
        Assert.Equal(
            (warned.Data.AlertId, SampleDeadlineAlertsComposition.KindId, SampleDeadlineAlertsComposition.KindNameKey, NhAssistantAlertUrgencyNames.Warning, NhAssistantAlertListStatuses.Unread),
            (alert.Id, alert.KindId, alert.DisplayNameKey, alert.Urgency, alert.Status));
        Assert.EndsWith(" is due today", alert.Title);
        Assert.Contains("2 tasks are still open", alert.Text);
        Assert.Equal((ProjectSubjects.Type, ProjectSubjects.IdOf(project)), (alert.SubjectType, alert.SubjectId));
        Assert.Equal(ProjectSubjects.LinkTemplate.Replace("{id}", ProjectSubjects.IdOf(project)), alert.SubjectLink);
        Assert.Equal(warned.Data.AlertId, Assert.Single((await ListAlertsAsync(participant, about)).Items).Id);
        Assert.Equal(warned.Data.AlertId, Assert.Single((await ListAlertsAsync(reader, about)).Items).Id);
        Assert.Equal(1, (await SummaryAsync(owner, about)).UnreadByUrgency.Warning);

        // The outsider has no delivery and no role on the project: nothing to list, read or dismiss.
        Assert.Empty((await ListAlertsAsync(outsider, about)).Items);
        Assert.Equal(0, (await SummaryAsync(outsider, about)).Unread);
        using (var refused = await outsider.PostAsync($"/api/assistant/alerts/{alert.Id}/read", null))
        {
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }

        // The owner reads it, which takes it off the badge; the reader dismisses it from their list.
        Assert.Equal(NhAssistantAlertListStatuses.Read, (await PostAlertAsync(owner, alert.Id, "read")).Status);
        Assert.Equal(0, (await SummaryAsync(owner, about)).Unread);
        Assert.Equal(NhAssistantAlertListStatuses.Dismissed, (await PostAlertAsync(reader, alert.Id, "dismiss")).Status);
        Assert.Empty((await ListAlertsAsync(reader, about)).Items);

        // The owner ticks off the tasks on the task page. After each commit the task consumer revalidates
        // the project's alerts: with a task left the warning still applies, after the last it is resolved
        // at once, also though the application raised it last, without waiting for a round.
        var firstDone = await host.CompleteTaskByHandAsync(tasks[0], host.OwnerId);
        await host.DeliverAsync(firstDone);
        Assert.Equal(NhAssistantAlertListStatuses.Read, Assert.Single((await ListAlertsAsync(owner, about)).Items).Status);
        var lastDone = await host.CompleteTaskByHandAsync(tasks[1], host.OwnerId);
        await host.DeliverAsync(lastDone);
        Assert.Empty((await ListAlertsAsync(owner, about, "open")).Items);

        // The next round finds nothing to warn about, so the application finds nothing open either.
        host.StartMorningRound();
        Assert.All(await WaitForRunsAsync(owner, conversationId, 3), run => Assert.Equal(NhAssistantRunStatuses.Completed, run.Status));
        var done = await WarnAsync(project);
        Assert.False(done.Success);
        Assert.Contains(done.GetResultItems(), item => item.Name == SampleDeadlineAlerts.NotDueToday);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<SampleDeadlineAlerts>().ResolveAsync(project)).Data);

        // The owner's list shows the alert as resolved, and the participant's badge no longer counts it.
        var resolved = Assert.Single((await ListAlertsAsync(owner, about, "closed")).Items);
        Assert.Equal((NhAssistantAlertListStatuses.Resolved, true), (resolved.Status, resolved.IsResolved));
        Assert.Equal(0, (await SummaryAsync(participant, about)).Unread);
    }

    [Fact]
    public async Task SPM_392_a_round_that_no_longer_raises_the_warning_resolves_it_by_omission()
    {
        host.MoveToNextMorning();
        var today = SampleDeadlineAlertsComposition.DayOf(host.Clock.GetUtcNow());
        var project = await host.CreateProjectAsync(
            "OMIT",
            host.OwnerId,
            ProjectStatus.Active,
            SampleDeadlineAlertsComposition.StartOf(today).AddHours(17));
        var tasks = await host.AddTasksAsync(project, "Sign the contract");
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var about = $"subjectType={ProjectSubjects.Type}&subjectId={ProjectSubjects.IdOf(project)}";

        // The round warns the owner.
        host.StartMorningRound();
        var conversationId = await WaitForThreadAsync(owner, project);
        Assert.Single(await WaitForRunsAsync(owner, conversationId, 1), run => run.Status == NhAssistantRunStatuses.Completed);
        Assert.Equal(NhAssistantAlertListStatuses.Unread, Assert.Single((await ListAlertsAsync(owner, about)).Items).Status);

        // The task is done without an event the application revalidates on, as an import would do it.
        // The next round raises nothing, so the kind resolves the watch's own warning by omission.
        await host.CompleteTasksAsync(tasks);
        host.StartMorningRound();
        Assert.All(await WaitForRunsAsync(owner, conversationId, 2), run => Assert.Equal(NhAssistantRunStatuses.Completed, run.Status));
        var resolved = Assert.Single((await ListAlertsAsync(owner, about, "closed")).Items);
        Assert.Equal((NhAssistantAlertListStatuses.Resolved, true), (resolved.Status, resolved.IsResolved));
        Assert.Equal(0, (await SummaryAsync(owner, about)).Unread);
    }

    [Fact]
    public async Task SPM_392_an_administrator_moves_the_warnings_digest_and_switches_it_off_within_its_kinds_bounds()
    {
        // The administration of alert kinds (FB87 phase 2): the deadline watch's kind lets an
        // administrator switch it off and change its default delivery; its recipients and texts stay code.
        const string route = $"/api/assistant/admin/alert-kinds/{SampleDeadlineAlertsComposition.KindId}";
        using var administrator = host.CreateClient("spm-392-admin", admin: true);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>();
        try
        {
            // Only the administration changes a kind: the project's owner may not.
            using (var forbidden = await owner.PutAsync(route, AssistantSampleHost.Json(new { enabled = false, expectedVersion = 0 })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }

            var kind = await AlertKindAsync(administrator);
            Assert.Equal(new NhAssistantAdminAlertKindChoicesDto(true, true, true, true, true), kind.AdministratorMayChange);
            Assert.Equal(("daily-digest", "07:30", true, "urgent"), (kind.CodeDelivery.Mode, Assert.Single(kind.CodeDelivery.DigestTimes), kind.CodeDelivery.WorkdaysOnly, kind.CodeDelivery.ImmediatelyFrom));

            // The morning shift reads the warning at 08:15; a person's preferences show the new default.
            NhAssistantAdminAlertKindDto moved;
            var later = new { mode = "daily-digest", digestTimes = new[] { "08:15" }, workdaysOnly = true, immediatelyFrom = "urgent" };
            using (var response = await administrator.PutAsync(route, AssistantSampleHost.Json(new
                   {
                       enabled = true,
                       delivery = later,
                       expectedVersion = kind.Settings.Version,
                       reason = "The project office starts later"
                   })))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                moved = (await response.Content.ReadFromJsonAsync<NhAssistantAdminAlertKindDto>(Json))!;
            }
            Assert.Equal(["08:15"], moved.Delivery.DigestTimes);
            var settings = await owner.GetFromJsonAsync<NhAssistantNotificationSettingsDto>("/api/assistant/notifications", Json);
            Assert.Equal("08:15", Assert.Single(settings!.AlertKinds, item => item.Id == SampleDeadlineAlertsComposition.KindId).DefaultDigestTime);

            // The application's warning about a project due today counts in the kind's numbers.
            host.MoveToNextWorkingMorning();
            var today = SampleDeadlineAlertsComposition.DayOf(host.Clock.GetUtcNow());
            var project = await host.CreateProjectAsync(
                "ADMIN",
                host.OwnerId,
                ProjectStatus.Active,
                SampleDeadlineAlertsComposition.StartOf(today).AddHours(17));
            await host.AddTasksAsync(project, "Approve the budget");
            Assert.True((await WarnAsync(project)).Success);
            var period = $"?to={Uri.EscapeDataString(host.Clock.GetUtcNow().AddMinutes(1).ToString("O"))}";
            Assert.True((await AlertKindAsync(administrator, period)).Numbers!.Raised >= 1);

            // Switched off, keeping its delivery, the kind raises nothing and a person has nothing to choose about it.
            using (var off = await administrator.PutAsync(route, AssistantSampleHost.Json(new { enabled = false, delivery = later, expectedVersion = moved.Settings.Version })))
            {
                Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            }
            var refused = await WarnAsync(project);
            Assert.Equal(NhAssistantAlertCodes.KindOff, Assert.Single(refused.GetResultItems()).Name);
            settings = await owner.GetFromJsonAsync<NhAssistantNotificationSettingsDto>("/api/assistant/notifications", Json);
            Assert.DoesNotContain(settings!.AlertKinds, item => item.Id == SampleDeadlineAlertsComposition.KindId);
            Assert.Contains(audit.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminAlertKindChanged
                && evt.ActorId == "spm-392-admin"
                && evt.AlertKind == SampleDeadlineAlertsComposition.KindId
                && evt.ChangedFields == "enabled");
        }
        finally
        {
            // The other tests of the watch expect the kind as its code declares it.
            var current = await AlertKindAsync(administrator);
            using var reset = await administrator.PutAsync(route, AssistantSampleHost.Json(new { enabled = true, delivery = (object?)null, expectedVersion = current.Settings.Version }));
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }
    }

    [Fact]
    public async Task SPM_392_the_reviewer_s_model_raises_a_deliverable_alert_through_its_template_first_in_shadow_then_for_the_project_s_people()
    {
        // The model tool of alerts (FB87-9): the deadline reviewer's model may raise the kind only once an
        // administrator switched it on for the reviewer, first in shadow; it fills the kind's template and
        // never chooses the project or the people.
        host.MoveToNextMorning();
        var today = SampleDeadlineAlertsComposition.DayOf(host.Clock.GetUtcNow());
        var project = await host.CreateProjectAsync("REVIEW", host.OwnerId, ProjectStatus.Active, SampleDeadlineAlertsComposition.StartOf(today.AddDays(30)));
        await host.AddMemberAsync(project, host.ParticipantId, ProjectMemberRole.Participant);
        var about = $"subjectType={ProjectSubjects.Type}&subjectId={ProjectSubjects.IdOf(project)}";
        const string switches = $"/api/assistant/admin/agents/{SampleDeadlineAlertsComposition.ReviewerAgentId}/alert-model";
        const string route = $"/api/assistant/admin/agents/{SampleDeadlineAlertsComposition.ReviewerAgentId}/alert-kinds/{SampleDeadlineAlertsComposition.ModelKindId}/model";
        using var administrator = host.CreateClient("spm-392-admin", admin: true);
        using var owner = host.CreateClient(host.OwnerId.ToString());
        using var participant = host.CreateClient(host.ParticipantId.ToString());
        var audit = host.Services.GetRequiredService<SampleAssistantAuditLog>();
        var deadline = today.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var call = new
        {
            kind = SampleDeadlineAlertsComposition.ModelKindId,
            parameters = new { deliverable = "Design brief", deadline, daysLeft = 5, work = "in-progress" }
        };
        var start = new NhAssistantStartAgentRequest(
            SampleDeadlineAlertsComposition.ReviewerAgentId,
            ProjectSubjects.Type,
            ProjectSubjects.IdOf(project),
            "Review the deadlines of this project's deliverables.");
        try
        {
            // The kind lists for the reviewer, off; a person outside the administration cannot switch it,
            // and it goes live only from shadow.
            var listed = (await administrator.GetFromJsonAsync<NhAssistantAlertModelListDto>(switches, Json))!;
            Assert.True(listed.Applies);
            var kind = Assert.Single(listed.Kinds);
            Assert.Equal((SampleDeadlineAlertsComposition.ModelKindId, NhAssistantAlertModelModes.Off), (kind.KindId, kind.Mode));
            Assert.Equal(["deliverable", "deadline", "daysLeft", "work"], kind.Parameters.Select(parameter => parameter.Name));
            using (var forbidden = await owner.PutAsync(route, AssistantSampleHost.Json(new { mode = "shadow", expectedVersion = 0 })))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }
            using (var liveFirst = await administrator.PutAsync(route, AssistantSampleHost.Json(new { mode = "live", expectedVersion = 0 })))
            {
                Assert.Equal(HttpStatusCode.Conflict, liveFirst.StatusCode);
            }
            using (var shadow = await administrator.PutAsync(route, AssistantSampleHost.Json(new { mode = "shadow", expectedVersion = 0, reason = "See what it raises first" })))
            {
                Assert.Equal(HttpStatusCode.OK, shadow.StatusCode);
            }

            // In shadow the reviewer's model is offered raise_alert and records a shadow alert that
            // reaches nobody; the kind's numbers count it as what the reviewer would have sent.
            var shadowModel = new NhAiScriptedChatClient()
                .RespondWithFunctionCall(NhAssistantAlertModelToolName, call, "call-shadow")
                .RespondWithText("The design brief is due in five days.");
            host.Model.Use(shadowModel);
            var conversationId = await StartReviewerAsync(participant, start);
            Assert.Equal(NhAssistantRunStatuses.Completed, Assert.Single(await WaitForRunsAsync(participant, conversationId, 1)).Status);
            Assert.Contains(shadowModel.Requests[0].Options!.Tools!, tool => tool.Name == NhAssistantAlertModelToolName);
            Assert.Empty((await ListAlertsAsync(owner, about)).Items);
            var period = $"?to={Uri.EscapeDataString(host.Clock.GetUtcNow().AddMinutes(1).ToString("O"))}";
            var counted = (await administrator.GetFromJsonAsync<NhAssistantAdminAlertKindsDto>("/api/assistant/admin/alert-kinds" + period, Json))!
                .Kinds.Single(item => item.Id == SampleDeadlineAlertsComposition.ModelKindId);
            Assert.True(Assert.Single(counted.Shadow, agent => agent.AgentId == SampleDeadlineAlertsComposition.ReviewerAgentId).WouldHaveSent >= 1);

            // Live, the same call reaches the project's owner and the member who works on it, labelled
            // as raised by AI, with the kind's texts from the values the platform checked.
            using (var live = await administrator.PutAsync(route, AssistantSampleHost.Json(new { mode = "live", expectedVersion = 1 })))
            {
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            }
            host.Model.Use(new NhAiScriptedChatClient()
                .RespondWithFunctionCall(NhAssistantAlertModelToolName, call, "call-live")
                .RespondWithText("The design brief is due in five days."));
            await StartReviewerAsync(participant, start);
            Assert.All(await WaitForRunsAsync(participant, conversationId, 2), run => Assert.Equal(NhAssistantRunStatuses.Completed, run.Status));
            var alert = Assert.Single((await ListAlertsAsync(owner, about)).Items);
            Assert.Equal((SampleDeadlineAlertsComposition.ModelKindId, true), (alert.KindId, alert.RaisedByModel));
            Assert.Equal("Design brief is due in 5 days", alert.Title);
            Assert.Contains("its work is in progress", alert.Text);
            Assert.Equal(alert.Id, Assert.Single((await ListAlertsAsync(participant, about)).Items).Id);

            Assert.Equal(
                [NhAssistantAlertModelModes.Shadow, NhAssistantAlertModelModes.Live],
                audit.Events
                    .Where(evt => evt.Kind == NhAssistantAuditEventKind.AdminAlertModelSwitchChanged
                        && evt.ObjectId == SampleDeadlineAlertsComposition.ReviewerAgentId)
                    .Select(evt => evt.ResultCode));
            Assert.Contains(audit.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised
                && evt.AlertId == alert.Id
                && evt.AlertRaisedByModel == true);
        }
        finally
        {
            // The other tests expect the reviewer's model off and the sample's own model.
            var current = (await administrator.GetFromJsonAsync<NhAssistantAlertModelListDto>(switches, Json))!.Kinds.Single();
            using var off = await administrator.PutAsync(route, AssistantSampleHost.Json(new { mode = "off", expectedVersion = current.Version }));
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            host.Model.Use(new SampleAssistantChatClient());
        }
    }

    private static async Task<Guid> StartReviewerAsync(HttpClient client, NhAssistantStartAgentRequest start)
    {
        using var started = await client.PostAsJsonAsync("/api/assistant/threads/start", start, Json);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        return (await started.Content.ReadFromJsonAsync<NhAssistantTriggerReceiptDto>(Json))!.ConversationId!.Value;
    }

    private static async Task<NhAssistantAdminAlertKindDto> AlertKindAsync(HttpClient administrator, string period = "")
    {
        var view = await administrator.GetFromJsonAsync<NhAssistantAdminAlertKindsDto>("/api/assistant/admin/alert-kinds" + period, Json);
        return Assert.Single(view!.Kinds, item => item.Id == SampleDeadlineAlertsComposition.KindId);
    }

    private static async Task<NhAssistantAlertListDto> ListAlertsAsync(HttpClient client, string about, string? status = null)
    {
        var query = status is null ? about : $"{about}&status={status}";
        return (await client.GetFromJsonAsync<NhAssistantAlertListDto>("/api/assistant/alerts?" + query, Json))!;
    }

    private static async Task<NhAssistantAlertSummaryDto> SummaryAsync(HttpClient client, string about)
    {
        return (await client.GetFromJsonAsync<NhAssistantAlertSummaryDto>("/api/assistant/alerts/summary?" + about, Json))!;
    }

    private static async Task<NhAssistantAlertDto> PostAlertAsync(HttpClient client, Guid alertId, string action)
    {
        using var response = await client.PostAsync($"/api/assistant/alerts/{alertId}/{action}", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NhAssistantAlertDto>(Json))!;
    }

    [Fact]
    public async Task SPM_392_the_warning_arrives_in_the_0730_digest_where_each_person_works_and_not_for_a_person_who_muted_it()
    {
        // 07:00 on a working day in the sample's time zone, when the morning round runs: before the digest.
        host.MoveToNextWorkingMorning();
        var today = SampleDeadlineAlertsComposition.DayOf(host.Clock.GetUtcNow());
        var project = await host.CreateProjectAsync(
            "DIGEST",
            host.OwnerId,
            ProjectStatus.Active,
            SampleDeadlineAlertsComposition.StartOf(today).AddHours(17));
        await host.AddTasksAsync(project, "Send the invoice");
        // A colleague whose division works in New York, and one who turns the kind off.
        var traveller = await host.AddPersonInTimeZoneAsync("America/New_York");
        var muted = await host.CreatePersonAsync();
        await host.AddMemberAsync(project, traveller, ProjectMemberRole.Reader);
        await host.AddMemberAsync(project, muted, ProjectMemberRole.Participant);

        // Each person's preferences: the time zone they read their digest in, and the kind they may mute.
        using (var travellerClient = host.CreateClient(traveller.ToString()))
        {
            var settings = await travellerClient.GetFromJsonAsync<NhAssistantNotificationSettingsDto>("/api/assistant/notifications", Json);
            Assert.Equal("America/New_York", settings!.TimeZone);
            var kind = Assert.Single(settings.AlertKinds, item => item.Id == SampleDeadlineAlertsComposition.KindId);
            Assert.Equal(("warning", "07:30", true, true, false), (kind.Urgency, kind.DefaultDigestTime, kind.WorkdaysOnly, kind.MayMute, kind.Muted));
        }
        using (var mutedClient = host.CreateClient(muted.ToString()))
        {
            using var response = await mutedClient.PutAsJsonAsync(
                "/api/assistant/notifications",
                new { alertKinds = new[] { new { id = SampleDeadlineAlertsComposition.KindId, muted = true } } },
                Json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var settings = await response.Content.ReadFromJsonAsync<NhAssistantNotificationSettingsDto>(Json);
            Assert.True(Assert.Single(settings!.AlertKinds, item => item.Id == SampleDeadlineAlertsComposition.KindId).Muted);
        }

        var warned = await WarnAsync(project, traveller, muted);
        Assert.Equal(3, warned.Data!.RecipientCount);

        // 07:30 in Amsterdam: the owner's digest goes out, as one notification on every channel.
        var amsterdam = SampleDeadlineAlertsComposition.At(today, new TimeOnly(7, 30), SampleDeadlineAlertsComposition.TimeZone);
        host.MoveTo(amsterdam);
        var ownerDigest = await host.WaitForOutboxAsync(host.OwnerId, warned.Data.AlertId);
        Assert.True(ownerDigest.SentAt >= amsterdam);
        Assert.NotNull(ownerDigest.DigestId);
        Assert.Equal([warned.Data.AlertId], ownerDigest.AlertIds);
        Assert.Contains(host.Inbox.Requests, request => request.UserId == host.OwnerId
            && request.GroupKey == $"assistant-alert:digest:{ownerDigest.DigestId:N}");

        // 07:30 in New York, six hours later: the colleague there gets theirs.
        var newYork = SampleDeadlineAlertsComposition.At(today, new TimeOnly(7, 30), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        host.MoveTo(newYork);
        var travellerDigest = await host.WaitForOutboxAsync(traveller, warned.Data.AlertId);
        Assert.True(travellerDigest.SentAt >= newYork);
        Assert.Single(host.Outbox.Entries, entry => entry.RecipientActorId == traveller.ToString("D"));

        // The colleague who muted the kind received nothing, on any channel.
        Assert.DoesNotContain(host.Outbox.Entries, entry => entry.RecipientActorId == muted.ToString("D"));
        Assert.DoesNotContain(host.Inbox.Requests, request => request.UserId == muted && request.Category == "assistant-alert");
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
    /// <summary>
    /// SPM-392: the sample's stand-in for an e-mail channel of alerts.
    /// </summary>
    public SampleAlertOutbox Outbox => Services.GetRequiredService<SampleAlertOutbox>();

    /// <summary>
    /// Moves the host's clock to 07:00 of the next working day in the sample's time zone, when the
    /// watch's morning round runs.
    /// </summary>
    public void MoveToNextWorkingMorning()
    {
        var now = Clock.GetUtcNow();
        var day = SampleDeadlineAlertsComposition.DayOf(now);
        var morning = SampleDeadlineAlertsComposition.At(day, new TimeOnly(7, 0), SampleDeadlineAlertsComposition.TimeZone);
        while (morning <= now || day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            day = day.AddDays(1);
            morning = SampleDeadlineAlertsComposition.At(day, new TimeOnly(7, 0), SampleDeadlineAlertsComposition.TimeZone);
        }

        Clock.Advance(morning - now);
    }

    /// <summary>
    /// Moves the host's clock to <paramref name="moment"/>; the run maintenance, every second in this
    /// host, then delivers what falls due.
    /// </summary>
    public void MoveTo(DateTimeOffset moment)
    {
        var now = Clock.GetUtcNow();
        if (moment > now)
        {
            Clock.Advance(moment - now);
        }
    }

    /// <summary>
    /// A person of the host's division who works in a division of their own in
    /// <paramref name="timeZoneId"/>, as their profile shows.
    /// </summary>
    public async Task<Guid> AddPersonInTimeZoneAsync(string timeZoneId)
    {
        var personId = await CreatePersonAsync();
        var divisionId = await AddDivisionAsync("Office in " + timeZoneId);
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        var division = await dbContext.Set<NhDivision>().SingleAsync(item => item.Id == divisionId);
        division.TimeZoneId = timeZoneId;
        dbContext.Set<NhDivisionUser>().Add(new NhDivisionUser { Id = Guid.NewGuid(), UserId = personId, DivisionId = divisionId });
        var person = await dbContext.Users.SingleAsync(item => item.Id == personId);
        person.ActiveDivisionId = divisionId;
        await dbContext.SaveChangesAsync();
        return personId;
    }

    /// <summary>
    /// The notification of <paramref name="alertId"/> to <paramref name="personId"/> in the outbox, once
    /// the delivery sent it.
    /// </summary>
    public async Task<SampleAlertOutboxEntry> WaitForOutboxAsync(Guid personId, Guid alertId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var entry = Outbox.Entries.FirstOrDefault(item => item.RecipientActorId == personId.ToString("D") && item.AlertIds.Contains(alertId));
            if (entry is not null)
            {
                return entry;
            }

            await Task.Delay(PollInterval);
        }

        throw new TimeoutException($"No notification of alert {alertId} reached person {personId} within 90 s.");
    }

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
