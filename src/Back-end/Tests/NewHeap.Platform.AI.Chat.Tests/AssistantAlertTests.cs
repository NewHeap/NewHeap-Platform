using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Alerts (FB87 phase 1, lane AK): kinds in code, raised by rules and by the application's code,
/// deduplicated per kind, subject and key, resolved and expired, purged after their retention, and
/// delivered to nobody who does not read the subject, on both relational providers.
/// </summary>
public sealed class AssistantAlertTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string DueToday = "task-due-today";
    private const string Unlisted = "task-unlisted";
    private const string Escalated = "task-escalated";
    private const string Responsible = "owner-1";
    private const string Reader = "reader-1";
    private const string Approver = "approver-1";
    private const string Outsider = "outsider-1";
    private const string OtherDivision = "other-division-1";
    private const string Gone = "gone-1";

    private static readonly DateTimeOffset Start = new(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_alerts_the_responsible_person_once_per_key_over_two_rounds_and_updates_it_in_place(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());
        var script = host.Services.GetRequiredService<AlertRuleScript>();
        var urgency = NhAssistantAlertUrgency.Warning;
        var name = "Write the report";
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked the tasks due today.")
            .WithAlerts(new NhAssistantAlert(DueToday, task)
            {
                Key = "2026-11-02",
                Urgency = urgency,
                Parameters = new Dictionary<string, string> { ["task"] = name }
            }));

        // The morning round raises the alert for the task's responsible person.
        var first = await RunRulesAsync(host, task);
        Assert.Equal(NhAssistantTurnStatuses.Completed, first.Outcome);
        var raised = await OpenAsync(host, task, "2026-11-02");
        Assert.Equal(1, raised.Revision);
        Assert.Equal(AgentId, raised.AgentId);
        Assert.NotNull(raised.RunId);
        Assert.Equal(NhAssistantAlertUrgency.Warning, raised.Urgency);
        var delivery = Assert.Single(raised.Deliveries);
        Assert.Equal((Responsible, NhAssistantAlertDeliveryStatuses.Pending, Start), (delivery.RecipientActorId, delivery.Status, delivery.DueAt!.Value));
        var audited = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised);
        Assert.Equal((raised.Id, DueToday, 1, NhAssistantAlertCodes.Raised, 1), (audited.AlertId!.Value, audited.AlertKind, audited.AlertRevision!.Value, audited.ResultCode, audited.AlertRecipientCount!.Value));
        Assert.Equal(("task", task.Id, "assistant-agent:" + AgentId), (audited.SubjectType, audited.SubjectId, audited.ActorId));

        // The delivery goes out, as the assistant's delivery claims and sends it.
        await SendAllAsync(host, clock.GetUtcNow());

        // A second round with the same content changes nothing and warns nobody again.
        clock.Advance(TimeSpan.FromHours(1));
        await RunRulesAsync(host, task);
        var unchanged = await OpenAsync(host, task, "2026-11-02");
        Assert.Equal(raised.Id, unchanged.Id);
        Assert.Equal(1, unchanged.Revision);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, Assert.Single(unchanged.Deliveries).Status);
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised);

        // New content updates the same alert in place as its next revision, without warning again.
        name = "Write the final report";
        await RunRulesAsync(host, task);
        var updated = await OpenAsync(host, task, "2026-11-02");
        Assert.Equal((raised.Id, 2), (updated.Id, updated.Revision));
        Assert.Contains("Write the final report", updated.ParametersJson);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Sent, Assert.Single(updated.Deliveries).Status);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised && evt.ResultCode == NhAssistantAlertCodes.Updated);

        // A higher urgency delivers it again.
        urgency = NhAssistantAlertUrgency.Urgent;
        await RunRulesAsync(host, task);
        var urgent = await OpenAsync(host, task, "2026-11-02");
        Assert.Equal((3, NhAssistantAlertUrgency.Urgent), (urgent.Revision, urgent.Urgency));
        var again = Assert.Single(urgent.Deliveries);
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Pending, 3), (again.Status, again.PlannedRevision));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised && evt.ResultCode == NhAssistantAlertCodes.Redelivered);

        // The task was reported done: the rules resolve the alert, and the waiting delivery is taken back.
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("The task is done.")
            .ResolveAlerts(new NhAssistantAlertKey(DueToday, task, key: null)));
        await RunRulesAsync(host, task);
        var resolved = await FindAsync(host, raised.Id);
        Assert.Equal(NhAssistantAlertStatuses.Resolved, resolved!.Status);
        Assert.Equal(clock.GetUtcNow(), resolved.ClosedAt);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Resolved, Assert.Single(resolved.Deliveries).Status);
        Assert.Null(await OpenOrNullAsync(host, task, "2026-11-02"));
        var closed = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertResolved);
        Assert.Equal((raised.Id, NhAssistantAlertStatuses.Resolved), (closed.AlertId!.Value, closed.ResultCode));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_alert_repeats_after_its_interval_but_never_reaches_a_person_who_dismissed_it_until_the_key_changes(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());
        var alert = new NhAssistantAlert(DueToday, task)
        {
            Key = "2026-11-02",
            Parameters = new Dictionary<string, string> { ["task"] = "Plan the launch" },
            Recipients = [Reader]
        };

        var raised = (await RaiseAsync(host, alert)).Data!;
        Assert.Equal((NhAssistantAlertOutcome.Raised, 1, false, 2), (raised.Outcome, raised.Revision, raised.Shadow, raised.RecipientCount));
        await SendAllAsync(host, clock.GetUtcNow());
        var store = host.Services.GetRequiredService<NhAssistantAlertStore>();
        Assert.True(await store.DismissAsync(raised.AlertId, Reader, clock.GetUtcNow(), CancellationToken.None));

        // Raised again within the kind's interval: nobody is warned again.
        clock.Advance(TimeSpan.FromHours(6));
        var within = (await RaiseAsync(host, alert)).Data!;
        Assert.Equal((raised.AlertId, NhAssistantAlertOutcome.Unchanged), (within.AlertId, within.Outcome));

        // After the interval the owner is warned again; the reader who dismissed it is not.
        clock.Advance(TimeSpan.FromHours(18));
        var repeated = (await RaiseAsync(host, alert)).Data!;
        Assert.Equal((raised.AlertId, NhAssistantAlertOutcome.Redelivered, 1), (repeated.AlertId, repeated.Outcome, repeated.RecipientCount));
        var deliveries = (await FindAsync(host, raised.AlertId))!.Deliveries.ToDictionary(item => item.RecipientActorId);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Pending, deliveries[Responsible].Status);
        Assert.Equal(clock.GetUtcNow(), deliveries[Responsible].DueAt);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Dismissed, deliveries[Reader].Status);

        // Another key is another alert: it reaches the reader again.
        var next = (await RaiseAsync(host, new NhAssistantAlert(DueToday, task)
        {
            Key = "2026-11-03",
            Parameters = alert.Parameters,
            Recipients = [Reader]
        })).Data!;
        Assert.NotEqual(raised.AlertId, next.AlertId);
        Assert.Equal(NhAssistantAlertOutcome.Raised, next.Outcome);
        Assert.Contains((await FindAsync(host, next.AlertId))!.Deliveries, item => item.RecipientActorId == Reader && item.Status == NhAssistantAlertDeliveryStatuses.Pending);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Resolving_and_expiry_close_alerts_and_the_maintenance_purges_them_after_their_retention(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());
        var expiring = (await RaiseAsync(host, Alert(task, "expiring") with { ExpiresAt = Start.AddHours(1) })).Data!;
        var resolving = (await RaiseAsync(host, Alert(task, "resolving"))).Data!;
        var delivered = (await RaiseAsync(host, Alert(task, "delivered"))).Data!;
        var store = host.Services.GetRequiredService<NhAssistantAlertStore>();
        var claimed = await store.ClaimDueAsync(clock.GetUtcNow(), "instance-1", clock.GetUtcNow().AddMinutes(5), 10, CancellationToken.None);
        Assert.Equal(3, claimed.Count);
        var sent = claimed.Single(item => item.AlertId == delivered.AlertId);
        Assert.True(await store.MarkSentAsync(sent.Id, "instance-1", 1, "web-push", null, clock.GetUtcNow(), CancellationToken.None));

        // Resolving by key closes the alert and takes its waiting delivery back; again closes nothing.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var alerts = scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>();
            Assert.Equal(1, (await alerts.ResolveAsync(new NhAssistantAlertKey(DueToday, task, "resolving"))).Data);
            Assert.Equal(0, (await alerts.ResolveAsync(new NhAssistantAlertKey(DueToday, task, "resolving"))).Data);
            // A delivery that went out stays sent; the alert itself shows as resolved.
            Assert.Equal(1, (await alerts.ResolveAsync(new NhAssistantAlertKey(DueToday, task, "delivered"))).Data);
            Assert.Equal(NhAssistantAlertCodes.KindUnknown, Code(await alerts.ResolveAsync(new NhAssistantAlertKey("task-unknown", task))));
        }
        var closed = (await FindAsync(host, resolving.AlertId))!;
        Assert.Equal(NhAssistantAlertStatuses.Resolved, closed.Status);
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Resolved, Assert.Single(closed.Deliveries).Status);
        var shown = (await FindAsync(host, delivered.AlertId))!;
        Assert.Equal((NhAssistantAlertStatuses.Resolved, NhAssistantAlertDeliveryStatuses.Sent), (shown.Status, Assert.Single(shown.Deliveries).Status));
        // A claim that was released by the resolve lets nobody send the taken-back delivery.
        Assert.False(await store.MarkSentAsync(claimed.Single(item => item.AlertId == resolving.AlertId).Id, "instance-1", 1, null, null, clock.GetUtcNow(), CancellationToken.None));

        // Past its expiry the maintenance closes the alert as expired.
        clock.Advance(TimeSpan.FromHours(2));
        var maintained = await MaintainAsync(host);
        Assert.Equal((1, 0), maintained);
        var expired = (await FindAsync(host, expiring.AlertId))!;
        Assert.Equal((NhAssistantAlertStatuses.Expired, NhAssistantAlertDeliveryStatuses.Expired), (expired.Status, Assert.Single(expired.Deliveries).Status));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertResolved
            && evt.AlertId == expiring.AlertId
            && evt.ResultCode == NhAssistantAlertStatuses.Expired);

        // The same key raises a new alert once the old one closed; an expiry in the past is refused.
        var renewed = (await RaiseAsync(host, Alert(task, "expiring"))).Data!;
        Assert.NotEqual(expiring.AlertId, renewed.AlertId);
        Assert.Equal(NhAssistantAlertCodes.Expired, Code(await RaiseAsync(host, Alert(task, "late") with { ExpiresAt = clock.GetUtcNow() })));

        // Closed alerts stay for the retention, then the maintenance purges them with their deliveries.
        clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal((0, 0), await MaintainAsync(host));
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal((0, 3), await MaintainAsync(host));
        Assert.Null(await FindAsync(host, expiring.AlertId));
        Assert.Null(await FindAsync(host, resolving.AlertId));
        Assert.NotNull(await FindAsync(host, renewed.AlertId));
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.False(await context.AlertDeliveries.AnyAsync(item => item.AlertId == expiring.AlertId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task People_without_reader_access_another_division_or_a_principal_are_left_out_with_a_content_free_audit(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());

        var raised = await RaiseAsync(host, new NhAssistantAlert(DueToday, task)
        {
            Key = "secret-key",
            Parameters = new Dictionary<string, string> { ["task"] = "Confidential merger" },
            Recipients = [Reader, Outsider, OtherDivision, Gone, "assistant-agent:" + AgentId]
        });

        Assert.True(raised.Success);
        Assert.Equal(2, raised.Data!.RecipientCount);
        var alert = (await FindAsync(host, raised.Data.AlertId))!;
        Assert.Equal(
            new[] { Reader, Responsible }.Order(StringComparer.Ordinal).ToArray(),
            alert.Deliveries.Select(item => item.RecipientActorId).Order(StringComparer.Ordinal).ToArray());
        var dropped = host.Business.Events
            .Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRecipientDropped)
            .ToDictionary(evt => evt.ActorId, evt => evt.ResultCode);
        Assert.Equal(NhAssistantAlertDropReasons.NoAccess, dropped[Outsider]);
        Assert.Equal(NhAssistantAlertDropReasons.NoAccess, dropped[OtherDivision]);
        Assert.Equal(NhAssistantAlertDropReasons.NotResolved, dropped[Gone]);
        Assert.Equal(NhAssistantAlertDropReasons.NotAPerson, dropped["assistant-agent:" + AgentId]);
        // The audit never carries the alert's parameters or key.
        Assert.All(host.Business.Events, evt => Assert.DoesNotContain("Confidential", evt.ToString(), StringComparison.Ordinal));
        Assert.All(host.Business.Events, evt => Assert.DoesNotContain("secret-key", evt.ToString(), StringComparison.Ordinal));

        // The same check at delivery and at reading: a person who lost access, a principal that is no
        // longer rebuilt, another tenant and a subject that is gone all drop the person.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var check = scope.ServiceProvider.GetRequiredService<NhAssistantAlertRecipientCheck>();
            var subjects = host.Services.GetRequiredService<AlertTestSubjects>();
            Assert.True((await check.CheckAsync(Reader, alert, CancellationToken.None)).Qualifies);
            Assert.Equal(NhAssistantAlertDropReasons.NoAccess, (await check.CheckAsync(Reader, alert, CancellationToken.None, inRequest: true, callerTenantId: "tenant-b")).DropReason);
            subjects.People[Reader] = (subjects.People[Reader].Division, NhAiSubjectRole.None);
            Assert.Equal(NhAssistantAlertDropReasons.NoAccess, (await check.CheckAsync(Reader, alert, CancellationToken.None)).DropReason);
            Assert.Equal(NhAssistantAlertDropReasons.NotResolved, (await check.CheckAsync(Gone, alert, CancellationToken.None)).DropReason);
            subjects.Deleted.TryAdd(task.Id, true);
            Assert.Equal(NhAssistantAlertDropReasons.SubjectGone, (await check.CheckAsync(Responsible, alert, CancellationToken.None)).DropReason);
            subjects.Deleted.TryRemove(task.Id, out _);
        }

        // Raised again, the reader who lost access is dropped from the waiting deliveries, and a person
        // named for the first time is planned once.
        var again = (await RaiseAsync(host, new NhAssistantAlert(DueToday, task)
        {
            Key = "secret-key",
            Parameters = new Dictionary<string, string> { ["task"] = "Confidential merger" },
            Recipients = [Reader, Approver]
        })).Data!;
        Assert.Equal((raised.Data.AlertId, 2), (again.AlertId, again.RecipientCount));
        var stored = (await FindAsync(host, again.AlertId))!.Deliveries.ToDictionary(item => item.RecipientActorId);
        Assert.Equal((NhAssistantAlertDeliveryStatuses.Dropped, NhAssistantAlertDropReasons.NoAccess), (stored[Reader].Status, stored[Reader].ReasonCode));
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Pending, stored[Approver].Status);
        Assert.Equal(3, stored.Count);

        // A kind without the explicit selector names nobody itself.
        Assert.Equal(NhAssistantAlertCodes.RecipientsNotSelected, Code(await RaiseAsync(host, new NhAssistantAlert(Escalated, task) { Recipients = [Reader] })));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_application_policy_chooses_the_recipients_and_every_one_is_checked(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());

        // The escalation kind selects the subject's approvers; the policy adds an outsider.
        var raised = (await RaiseAsync(host, new NhAssistantAlert(Escalated, task))).Data!;

        var alert = (await FindAsync(host, raised.AlertId))!;
        Assert.Equal(new[] { Approver }, alert.Deliveries.Select(item => item.RecipientActorId).ToArray());
        var context = Assert.Single(host.Services.GetRequiredService<AlertPolicyLog>().Contexts);
        Assert.Equal(new[] { Approver }, context.Selected);
        Assert.Equal(new[] { Approver }, context.Approvers.Select(member => member.ActorId).ToArray());
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRecipientDropped && evt.ActorId == Outsider);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_that_raises_a_kind_its_agent_does_not_list_fails_its_run_and_raises_nothing(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());
        host.Services.GetRequiredService<AlertRuleScript>().Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(Alert(task, "listed"), new NhAssistantAlert(Unlisted, task)));

        var result = await RunRulesAsync(host, task);

        Assert.Equal(NhAssistantTurnStatuses.Failed, result.Outcome);
        Assert.Equal(NhAssistantAlertCodes.KindNotListed, result.ErrorCode);
        Assert.Null(await OpenOrNullAsync(host, task, "listed"));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_shadow_run_records_shadow_alerts_for_nobody_and_a_paused_agent_raises_nothing(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());
        host.Services.GetRequiredService<AlertRuleScript>().Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(Alert(task, "2026-11-02")));

        var shadow = await RunRulesAsync(host, task, shadow: true);

        Assert.Equal(NhAssistantTurnStatuses.Completed, shadow.Outcome);
        var store = host.Services.GetRequiredService<NhAssistantAlertStore>();
        var recorded = await store.FindOpenAsync(ScopeKey, DueToday, task, "2026-11-02", shadow: true, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.True(recorded.Shadow);
        Assert.Empty(recorded.Deliveries);
        Assert.Null(await OpenOrNullAsync(host, task, "2026-11-02"));
        var audited = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised);
        Assert.Equal((true, 0), (audited.Shadow!.Value, audited.AlertRecipientCount!.Value));
        Assert.Empty(await store.ClaimDueAsync(clock.GetUtcNow(), "instance-1", clock.GetUtcNow().AddMinutes(5), 10, CancellationToken.None));

        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.Agents
                .Where(agent => agent.Id == AgentId)
                .ExecuteUpdateAsync(update => update.SetProperty(agent => agent.RunState, NhAssistantAgentRunStates.Paused));
        }

        var script = host.Services.GetRequiredService<AlertRuleScript>();
        var runs = script.Runs;
        var paused = await RunRulesAsync(host, task);

        // The rules ran and asked for the alert; the paused agent raised nothing.
        Assert.Equal(NhAssistantTurnStatuses.Completed, paused.Outcome);
        Assert.Equal(runs + 1, script.Runs);
        Assert.Null(await OpenOrNullAsync(host, task, "2026-11-02"));
        Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_limits_bound_alerts_per_run_and_new_alerts_per_subject_per_day(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock, limits =>
        {
            limits.MaxAlertsPerRun = 2;
            limits.MaxAlertsPerSubjectPerDay = 3;
        });
        var task = TaskSubject(Guid.NewGuid());
        host.Services.GetRequiredService<AlertRuleScript>().Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(Alert(task, "a"), Alert(task, "b"), Alert(task, "c")));

        var run = await RunRulesAsync(host, task);

        Assert.Equal(NhAssistantTurnStatuses.Completed, run.Outcome);
        Assert.NotNull(await OpenOrNullAsync(host, task, "a"));
        Assert.NotNull(await OpenOrNullAsync(host, task, "b"));
        Assert.Null(await OpenOrNullAsync(host, task, "c"));

        Assert.True((await RaiseAsync(host, Alert(task, "d"))).Success);
        Assert.Equal(NhAssistantAlertCodes.LimitReached, Code(await RaiseAsync(host, Alert(task, "e"))));
        // Updating an open alert in place is no new alert.
        Assert.Equal(NhAssistantAlertOutcome.Updated, (await RaiseAsync(host, Alert(task, "a") with { Urgency = NhAssistantAlertUrgency.Info })).Data!.Outcome);
        // Another subject has its own limit, and the next day the subject may get new alerts again.
        Assert.True((await RaiseAsync(host, Alert(TaskSubject(Guid.NewGuid()), "e"))).Success);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True((await RaiseAsync(host, Alert(task, "e"))).Success);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_application_code_is_refused_for_an_unknown_kind_or_subject_and_a_title_that_fails(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());
        host.Services.GetRequiredService<AlertTestSubjects>().Deleted.TryAdd("deleted", true);

        Assert.Equal(NhAssistantAlertCodes.KindUnknown, Code(await RaiseAsync(host, new NhAssistantAlert("task-unknown", task))));
        Assert.Equal(NhAssistantAlertCodes.SubjectNotFound, Code(await RaiseAsync(host, new NhAssistantAlert(DueToday, new NhAiSubjectRef("task", "deleted")))));
        Assert.Equal(NhAssistantAlertCodes.SubjectNotFound, Code(await RaiseAsync(host, new NhAssistantAlert(DueToday, new NhAiSubjectRef("ticket", "1")))));
        // The kind's title needs the task's name.
        Assert.Equal(NhAssistantAlertCodes.TextInvalid, Code(await RaiseAsync(host, new NhAssistantAlert(DueToday, task))));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRaised);
    }

    private const string ScopeKey = "division:11111111111111111111111111111111";

    private static NhAiSubjectRef TaskSubject(Guid taskId)
    {
        return new NhAiSubjectRef("task", taskId.ToString("D"));
    }

    private static NhAssistantAlert Alert(NhAiSubjectRef task, string key)
    {
        return new NhAssistantAlert(DueToday, task)
        {
            Key = key,
            Parameters = new Dictionary<string, string> { ["task"] = "Task " + key }
        };
    }

    private static string? Code<T>(NewHeap.Platform.Common.Models.TaskResult<T> result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        AssistantTestClock clock,
        Action<NhAssistantLimits>? limits = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            new NhAiScriptedChatClient(),
            limits,
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<AlertTestSubjects>();
                services.AddSingleton<AlertRuleScript>();
                services.AddSingleton<AlertPolicyLog>();
                services.AddSingleton(Substitute.For<INhBackgroundOperationService>());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, AlertPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<AlertTestSubjectResolver>()
                .AddAlertKind(DueToday, kind => kind
                    .DisplayName("tests.alerts.task-due-today")
                    .Urgency(NhAssistantAlertUrgency.Warning)
                    .Title((culture, parameters) => culture.TwoLetterISOLanguageName == "nl"
                        ? $"{parameters["task"]} moet vandaag af"
                        : $"{parameters["task"]} is due today")
                    .Recipients(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit))
                    .RepeatAfter(TimeSpan.FromDays(1)))
                .AddAlertKind(Unlisted, kind => kind
                    .DisplayName("tests.alerts.task-unlisted")
                    .Title((_, _) => "Unlisted"))
                .AddAlertKind(Escalated, kind => kind
                    .DisplayName("tests.alerts.task-escalated")
                    .Urgency(NhAssistantAlertUrgency.Urgent)
                    .Title((_, _) => "A task was escalated")
                    .Recipients(NhAssistantAlertRecipients.SubjectApprovers)
                    .Required())
                .UseAlertPolicy<AlertTestPolicy>()
                .ConfigureAgent(AgentId, agent => agent
                    .UseRuleHandler<AlertTestRules>()
                    .Alerts(DueToday)));
    }

    private static async Task<NhAssistantAgentTurnResult> RunRulesAsync(AssistantTestHost host, NhAiSubjectRef task, bool shadow = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(task, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadow)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "morning-round",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            Shadow = shadow,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var agentTurn = new NhAssistantAgentTurnContext(
            thread,
            info,
            options,
            run.Id,
            run.TriggerId,
            null,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        return await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Morning round.")]),
            CancellationToken.None);
    }

    private static async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantAlertReceipt>> RaiseAsync(AssistantTestHost host, NhAssistantAlert alert)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RaiseAsync(alert);
    }

    private static async Task<(int Expired, int Purged)> MaintainAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAlertService>().MaintainAsync(CancellationToken.None);
    }

    /// <summary>
    /// Sends every due delivery as the assistant's delivery does: it claims them and records them sent.
    /// </summary>
    private static async Task SendAllAsync(AssistantTestHost host, DateTimeOffset now)
    {
        var store = host.Services.GetRequiredService<NhAssistantAlertStore>();
        foreach (var delivery in await store.ClaimDueAsync(now, "instance-1", now.AddMinutes(5), 100, CancellationToken.None))
        {
            Assert.True(await store.MarkSentAsync(delivery.Id, "instance-1", delivery.Alert!.Revision, "in-app", null, now, CancellationToken.None));
        }
    }

    private static async Task<AssistantAlert> OpenAsync(AssistantTestHost host, NhAiSubjectRef task, string key)
    {
        return (await OpenOrNullAsync(host, task, key))!;
    }

    private static Task<AssistantAlert?> OpenOrNullAsync(AssistantTestHost host, NhAiSubjectRef task, string key)
    {
        return host.Services.GetRequiredService<NhAssistantAlertStore>()
            .FindOpenAsync(ScopeKey, DueToday, task, key, shadow: false, CancellationToken.None);
    }

    private static Task<AssistantAlert?> FindAsync(AssistantTestHost host, Guid alertId)
    {
        return host.Services.GetRequiredService<NhAssistantAlertStore>().FindAsync(alertId, CancellationToken.None);
    }
}

/// <summary>
/// The tasks of the alert tests: every task belongs to one division, and a person reads it only with a
/// role in that division. The owner is responsible; a deleted task no longer exists.
/// </summary>
public sealed class AlertTestSubjects
{
    public static readonly Guid Division = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid OtherDivision = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public ConcurrentDictionary<string, (Guid Division, NhAiSubjectRole Role)> People { get; } = new(new Dictionary<string, (Guid, NhAiSubjectRole)>
    {
        ["owner-1"] = (Division, NhAiSubjectRole.Participant),
        ["reader-1"] = (Division, NhAiSubjectRole.Reader),
        ["approver-1"] = (Division, NhAiSubjectRole.Approver),
        ["gone-1"] = (Division, NhAiSubjectRole.Reader),
        ["other-division-1"] = (OtherDivision, NhAiSubjectRole.Approver)
    });

    public ConcurrentDictionary<string, bool> Deleted { get; } = new();
}

public sealed class AlertTestSubjectResolver(AlertTestSubjects subjects) : INhAiSubjectAccessResolver
{
    public string SubjectType => "task";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        if (subjects.Deleted.ContainsKey(subject.Id))
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(null);
        }

        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, AlertTestSubjects.Division))
        {
            DivisionId = AlertTestSubjects.Division,
            DisplayName = "Task " + subject.Id[..8],
            ResponsibleActorId = "owner-1",
            Parent = new NhAiSubjectParent(new NhAiSubjectRef("project", "launch"), "Launch")
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        // A role counts only in the task's own division.
        return ValueTask.FromResult(subjects.People.TryGetValue(actorId, out var person) && person.Division == AlertTestSubjects.Division
            ? new NhAiSubjectAccess(person.Role)
            : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (actorId, person) in subjects.People.Where(item => item.Value.Division == AlertTestSubjects.Division))
        {
            await Task.Yield();
            yield return new NhAiSubjectMember(actorId, person.Role);
        }
    }
}

/// <summary>
/// The application's principal factory of the alert tests: an account that was removed is no longer rebuilt.
/// </summary>
public sealed class AlertPrincipalFactory : INhAiPrincipalFactory
{
    public ValueTask<ClaimsPrincipal?> CreateAsync(string actorId, NhAiSubjectInfo subject, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<ClaimsPrincipal?>(actorId == "gone-1"
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test")));
    }
}

public sealed class AlertRuleScript
{
    private int _runs;

    public Func<NhAssistantRuleContext, CancellationToken, Task<NhAssistantRuleResult>> Run { get; set; } =
        (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Nothing to do."));

    /// <summary>How often the rules ran.</summary>
    public int Runs => Volatile.Read(ref _runs);

    public void Ran()
    {
        Interlocked.Increment(ref _runs);
    }
}

public sealed class AlertTestRules(AlertRuleScript script) : INhAssistantAgentRuleHandler
{
    public Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        script.Ran();
        return script.Run(context, cancellationToken);
    }
}

public sealed class AlertPolicyLog
{
    public ConcurrentQueue<NhAssistantAlertRecipientContext> Contexts { get; } = new();
}

/// <summary>
/// The application's policy of the alert tests: an escalation also names an outsider, whom the
/// recipient check leaves out; other kinds keep their selectors.
/// </summary>
public sealed class AlertTestPolicy(AlertPolicyLog log) : INhAssistantAlertPolicy
{
    public ValueTask<IReadOnlyList<string>> SelectRecipientsAsync(NhAssistantAlertRecipientContext context, CancellationToken cancellationToken = default)
    {
        if (context.Kind.Id != "task-escalated")
        {
            return ValueTask.FromResult(context.Selected);
        }

        log.Contexts.Enqueue(context);
        return ValueTask.FromResult<IReadOnlyList<string>>([.. context.Selected, "outsider-1"]);
    }
}
