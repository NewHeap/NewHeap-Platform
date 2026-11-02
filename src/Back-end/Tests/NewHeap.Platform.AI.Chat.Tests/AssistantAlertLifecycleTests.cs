using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
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
/// Alerts (FB87 phase 2, lane AR): the subject's members by minimum role as recipients, checked live at
/// delivery and at reading; resolving by omission when rules finish a run on a subject; and the
/// application's explicit revalidation with the checks kinds declare, on both relational providers.
/// </summary>
public sealed class AssistantAlertLifecycleTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string ForWorkers = "task-for-workers";
    private const string ForOwnerAndApprovers = "task-for-owner-and-approvers";
    private const string Overdue = "task-overdue";
    private const string Note = "task-note";
    private const string Blocked = "task-blocked";
    private const string Unlisted = "task-unlisted";
    private const string Owner = "owner-1";
    private const string Reader = "reader-1";
    private const string Approver = "approver-1";
    private const string ScopeKey = "division:11111111111111111111111111111111";

    private static readonly DateTimeOffset Start = new(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Subject_members_with_the_minimum_role_receive_an_alert_and_lose_it_with_the_role_or_their_access(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());
        var people = host.Services.GetRequiredService<AlertTestSubjects>();

        // The participant owner and the approver work on the task; the reader is below the role.
        var raised = (await RaiseAsync(host, Alert(ForWorkers, task, "k"))).Data!;
        Assert.Equal(2, raised.RecipientCount);
        var alert = (await FindAsync(host, raised.AlertId))!;
        Assert.Equal(
            new[] { Approver, Owner },
            alert.Deliveries.Select(item => item.RecipientActorId).Order(StringComparer.Ordinal).ToArray());
        // A member below the role is not chosen at all, so nothing about them is audited.
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRecipientDropped);

        // Before the delivery the approver drops to reader and the owner loses access: nobody gets it.
        people.People[Approver] = (AlertTestSubjects.Division, NhAiSubjectRole.Reader);
        people.People[Owner] = (AlertTestSubjects.Division, NhAiSubjectRole.None);
        var dispatched = await DispatchAsync(host);
        Assert.Equal((2, 0, 2), (dispatched.Claimed, dispatched.Sent, dispatched.Dropped));
        Assert.Empty(host.Services.GetRequiredService<DeliveryPushes>().Sent);
        var deliveries = (await FindAsync(host, raised.AlertId))!.Deliveries.ToDictionary(item => item.RecipientActorId);
        Assert.Equal(
            (NhAssistantAlertDeliveryStatuses.Dropped, NhAssistantAlertDropReasons.BelowMinimumRole),
            (deliveries[Approver].Status, deliveries[Approver].ReasonCode));
        Assert.Equal(
            (NhAssistantAlertDeliveryStatuses.Dropped, NhAssistantAlertDropReasons.NoAccess),
            (deliveries[Owner].Status, deliveries[Owner].ReasonCode));
        var dropped = host.Business.Events
            .Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertRecipientDropped)
            .ToDictionary(evt => evt.ActorId, evt => evt.ResultCode);
        Assert.Equal(NhAssistantAlertDropReasons.BelowMinimumRole, dropped[Approver]);
        Assert.Equal(NhAssistantAlertDropReasons.NoAccess, dropped[Owner]);

        // Reading asks the role again: below it the person no longer reads the alert, with it again they do.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var check = scope.ServiceProvider.GetRequiredService<NhAssistantAlertRecipientCheck>();
            Assert.Equal(
                NhAssistantAlertDropReasons.BelowMinimumRole,
                (await check.CheckAsync(Approver, alert, CancellationToken.None, inRequest: true)).DropReason);
            people.People[Approver] = (AlertTestSubjects.Division, NhAiSubjectRole.Approver);
            Assert.True((await check.CheckAsync(Approver, alert, CancellationToken.None, inRequest: true)).Qualifies);

            // Combined with another selector the person may have been chosen otherwise: the check asks
            // reader, as for the other recipients.
            var mixed = (await FindAsync(host, (await RaiseAsync(host, Alert(ForOwnerAndApprovers, task, "m"))).Data!.AlertId))!;
            Assert.Equal(new[] { Approver }, mixed.Deliveries.Select(item => item.RecipientActorId).ToArray());
            people.People[Approver] = (AlertTestSubjects.Division, NhAiSubjectRole.Reader);
            Assert.True((await check.CheckAsync(Approver, mixed, CancellationToken.None, inRequest: true)).Qualifies);
            Assert.Equal(NhAssistantAlertDropReasons.BelowMinimumRole, (await check.CheckAsync(Approver, alert, CancellationToken.None)).DropReason);
            people.People[Reader] = (AlertTestSubjects.Division, NhAiSubjectRole.None);
            Assert.Equal(NhAssistantAlertDropReasons.NoAccess, (await check.CheckAsync(Reader, mixed, CancellationToken.None)).DropReason);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_finished_rule_run_resolves_the_open_alerts_of_its_subject_it_did_not_raise_again_and_nothing_else(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());
        var other = TaskSubject(Guid.NewGuid());
        var script = host.Services.GetRequiredService<AlertRuleScript>();

        // The first round warns about two overdue keys, notes the task and warns about another task.
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(Alert(Overdue, task, "a"), Alert(Overdue, task, "b"), Alert(Note, task, "n"), Alert(Overdue, other, "u")));
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunRulesAsync(host, task)).Outcome);
        // The application raises the same kind about the task itself, without the agent.
        Assert.True((await RaiseAsync(host, Alert(Overdue, task, "application"))).Success);

        // The next round raises "a" again, and "b" with parameters its title cannot be written from: a
        // refusal counts as raised, so it never resolves what still applies.
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked again.")
            .WithAlerts(Alert(Overdue, task, "a"), new NhAssistantAlert(Overdue, task) { Key = "b" }));
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunRulesAsync(host, task)).Outcome);
        Assert.NotNull(await OpenOrNullAsync(host, Overdue, task, "b"));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertResolved);

        // A round that raises only "a": "b" is resolved by omission and its waiting delivery taken back.
        var b = (await OpenOrNullAsync(host, Overdue, task, "b"))!;
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Only a.").WithAlerts(Alert(Overdue, task, "a")));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunRulesAsync(host, task)).Outcome);
        var resolved = (await FindAsync(host, b.Id))!;
        Assert.Equal((NhAssistantAlertStatuses.Resolved, clock.GetUtcNow()), (resolved.Status, resolved.ClosedAt!.Value));
        Assert.Equal(NhAssistantAlertDeliveryStatuses.Resolved, Assert.Single(resolved.Deliveries).Status);
        var audited = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertResolved);
        Assert.Equal(
            (b.Id, Overdue, NhAssistantAlertCodes.NotRaised, "assistant-agent:" + AgentId),
            (audited.AlertId!.Value, audited.AlertKind, audited.ResultCode, audited.ActorId));

        // What the run raised again, a kind that does not resolve by omission, another subject and the
        // application's own alert all stay open.
        Assert.NotNull(await OpenOrNullAsync(host, Overdue, task, "a"));
        Assert.NotNull(await OpenOrNullAsync(host, Note, task, "n"));
        Assert.NotNull(await OpenOrNullAsync(host, Overdue, other, "u"));
        Assert.NotNull(await OpenOrNullAsync(host, Overdue, task, "application"));

        // A round that raises nothing resolves the rest of the agent's alerts of the kind on the task.
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Nothing is overdue."));
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunRulesAsync(host, task)).Outcome);
        Assert.Null(await OpenOrNullAsync(host, Overdue, task, "a"));
        Assert.NotNull(await OpenOrNullAsync(host, Overdue, task, "application"));
        Assert.Equal(2, host.Business.Events.Count(evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertResolved
            && evt.ResultCode == NhAssistantAlertCodes.NotRaised));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_rule_run_that_fails_hands_over_or_is_cancelled_resolves_nothing_by_omission(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock, model => model.RespondWithText("The model looked at it."));
        var task = TaskSubject(Guid.NewGuid());
        var script = host.Services.GetRequiredService<AlertRuleScript>();
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Overdue.").WithAlerts(Alert(Overdue, task, "a")));
        await RunRulesAsync(host, task);

        // The rules throw.
        script.Run = (_, _) => throw new InvalidOperationException("The rules broke.");
        Assert.Equal(NhAssistantTurnStatuses.Failed, (await RunRulesAsync(host, task)).Outcome);

        // The rules raise a kind the agent does not list, which fails the run.
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Checked.").WithAlerts(new NhAssistantAlert(Unlisted, task)));
        var unlisted = await RunRulesAsync(host, task);
        Assert.Equal((NhAssistantTurnStatuses.Failed, NhAssistantAlertCodes.KindNotListed), (unlisted.Outcome, unlisted.ErrorCode));

        // The rules hand the run over to the model.
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.HandOver("This needs judgement."));
        await RunRulesAsync(host, task);

        // The run is cancelled while the rules work.
        using var cancellation = new CancellationTokenSource();
        script.Run = (_, cancellationToken) =>
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(NhAssistantRuleResult.Done("Never."));
        };
        Assert.Equal(NhAssistantTurnStatuses.Cancelled, (await RunRulesAsync(host, task, cancellationToken: cancellation.Token)).Outcome);

        Assert.Equal(5, script.Runs);
        Assert.NotNull(await OpenOrNullAsync(host, Overdue, task, "a"));
        Assert.DoesNotContain(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertResolved);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_on_the_whole_application_never_resolves_by_omission_and_a_shadow_run_touches_only_shadow_alerts(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var script = host.Services.GetRequiredService<AlertRuleScript>();
        var application = NhAiSystemSubjects.Global;

        // A run on the whole application raises an alert about it, the next raises nothing: it stays open.
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Weekly.").WithAlerts(Alert(Overdue, application, "week")));
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunRulesAsync(host, application)).Outcome);
        var store = host.Services.GetRequiredService<NhAssistantAlertStore>();
        var weekly = await store.FindOpenAsync(NhAiSubjectInfo.CreateScopeKey(null, null), Overdue, application, "week", shadow: false, CancellationToken.None);
        Assert.NotNull(weekly);
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Nothing this week."));
        Assert.Equal(NhAssistantTurnStatuses.Completed, (await RunRulesAsync(host, application)).Outcome);
        Assert.Equal(NhAssistantAlertStatuses.Open, (await FindAsync(host, weekly.Id))!.Status);

        // On a task, a live run raises one alert and a shadow run another, recorded as shadow.
        var task = TaskSubject(Guid.NewGuid());
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Overdue.").WithAlerts(Alert(Overdue, task, "live")));
        await RunRulesAsync(host, task);
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Overdue.").WithAlerts(Alert(Overdue, task, "shadow")));
        await RunRulesAsync(host, task, shadow: true);
        var shadow = await store.FindOpenAsync(ScopeKey, Overdue, task, "shadow", shadow: true, CancellationToken.None);
        Assert.NotNull(shadow);

        // A shadow run that raises nothing resolves only the shadow alert; a live one then the live alert.
        script.Run = (_, _) => Task.FromResult(NhAssistantRuleResult.Done("Nothing is overdue."));
        await RunRulesAsync(host, task, shadow: true);
        Assert.Equal(NhAssistantAlertStatuses.Resolved, (await FindAsync(host, shadow.Id))!.Status);
        Assert.NotNull(await OpenOrNullAsync(host, Overdue, task, "live"));
        await RunRulesAsync(host, task);
        Assert.Null(await OpenOrNullAsync(host, Overdue, task, "live"));
        var resolved = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertResolved).ToArray();
        Assert.Equal(new[] { true, false }, resolved.Select(evt => evt.Shadow!.Value).ToArray());
        Assert.DoesNotContain(resolved, evt => evt.AlertId == weekly.Id);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Revalidation_resolves_what_its_kind_check_no_longer_holds_and_one_failing_check_stops_no_other(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock);
        var task = TaskSubject(Guid.NewGuid());
        var other = TaskSubject(Guid.NewGuid());
        var checks = host.Services.GetRequiredService<BlockedCheckScript>();

        // The oldest alert's check throws; it runs first.
        var failing = (await RaiseAsync(host, Alert(Blocked, task, "boom", "secret detail"))).Data!;
        clock.Advance(TimeSpan.FromMinutes(1));
        var stale = (await RaiseAsync(host, Alert(Blocked, task, "unblocked"))).Data!;
        var valid = (await RaiseAsync(host, Alert(Blocked, task, "still-blocked"))).Data!;
        var racing = (await RaiseAsync(host, Alert(Blocked, task, "raised-again"))).Data!;
        var withoutCheck = (await RaiseAsync(host, Alert(Note, task, "note"))).Data!;
        var elsewhere = (await RaiseAsync(host, Alert(Blocked, other, "unblocked"))).Data!;
        checks.Resolve.Add("unblocked");
        checks.Resolve.Add("raised-again");
        checks.Throw.Add("boom");
        checks.RaiseAgain.Add("raised-again");

        var first = await RevalidateAsync(host, task);

        Assert.True(first.Success);
        var result = first.Data!;
        Assert.Equal((4, 1, false), (result.Checked, result.Failed, result.HasMore));
        var resolved = Assert.Single(result.Resolved);
        Assert.Equal((stale.AlertId, Blocked, "unblocked", false), (resolved.AlertId, resolved.KindId, resolved.Key, resolved.Shadow));
        Assert.DoesNotContain("unblocked", resolved.ToString(), StringComparison.Ordinal);
        var closed = (await FindAsync(host, stale.AlertId))!;
        Assert.Equal((NhAssistantAlertStatuses.Resolved, NhAssistantAlertDeliveryStatuses.Resolved), (closed.Status, Assert.Single(closed.Deliveries).Status));
        var audited = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertResolved);
        Assert.Equal((stale.AlertId, NhAssistantAlertCodes.Revalidated, NhAssistantSpeakers.ApplicationActorId), (audited.AlertId!.Value, audited.ResultCode, audited.ActorId));

        // The check that threw, one that still holds, an alert raised again while it was checked, a kind
        // without a check and another subject all stay open.
        foreach (var open in new[] { failing, valid, racing, withoutCheck, elsewhere })
        {
            Assert.Equal(NhAssistantAlertStatuses.Open, (await FindAsync(host, open.AlertId))!.Status);
        }
        Assert.Equal(2, (await FindAsync(host, racing.AlertId))!.Revision);

        // The check saw the parameters and the key; the log names the exception's type, never content.
        var seen = checks.Seen.Single(context => context.AlertId == valid.AlertId);
        Assert.Equal((Blocked, task, "still-blocked", "Task still-blocked"), (seen.KindId, seen.Subject, seen.Key, seen.Parameters["task"]));
        Assert.DoesNotContain(checks.Seen, context => context.Subject == other || context.KindId == Note);
        var warning = Assert.Single(host.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(failing.AlertId.ToString(), StringComparison.Ordinal));
        Assert.Contains(nameof(InvalidOperationException), warning.Message, StringComparison.Ordinal);
        Assert.All(host.Logs.Entries, entry => Assert.DoesNotContain("secret", entry.Message, StringComparison.Ordinal));

        // A second call checks what is still open and resolves nothing more.
        checks.RaiseAgain.Clear();
        checks.Resolve.Remove("raised-again");
        var second = (await RevalidateAsync(host, task)).Data!;
        Assert.Equal((0, 3, 1), (second.Resolved.Count, second.Checked, second.Failed));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Revalidation_of_kinds_without_a_check_reads_nothing_and_leaves_their_alerts_open(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(Start);
        await using var host = await CreateHostAsync(provider, clock, withCheck: false);
        var task = TaskSubject(Guid.NewGuid());
        var note = (await RaiseAsync(host, Alert(Note, task, "note"))).Data!;

        var result = await RevalidateAsync(host, task);

        Assert.True(result.Success);
        Assert.Same(NhAssistantAlertRevalidationResult.Empty, result.Data);
        Assert.Equal(NhAssistantAlertStatuses.Open, (await FindAsync(host, note.AlertId))!.Status);
        Assert.Empty(host.Services.GetRequiredService<BlockedCheckScript>().Seen);
    }

    private static NhAiSubjectRef TaskSubject(Guid taskId)
    {
        return new NhAiSubjectRef("task", taskId.ToString("D"));
    }

    private static NhAssistantAlert Alert(string kindId, NhAiSubjectRef subject, string key, string? name = null)
    {
        return new NhAssistantAlert(kindId, subject)
        {
            Key = key,
            Parameters = new Dictionary<string, string> { ["task"] = name ?? "Task " + key }
        };
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        AssistantTestClock clock,
        Action<NhAiScriptedChatClient>? model = null,
        bool withCheck = true)
    {
        var chat = new NhAiScriptedChatClient();
        model?.Invoke(chat);
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            chat,
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<AlertTestSubjects>();
                services.AddSingleton<AlertRuleScript>();
                services.AddSingleton<BlockedCheckScript>();
                services.AddSingleton<DeliveryPushes>();
                services.AddSingleton(Substitute.For<INhBackgroundOperationService>());
                services.Replace(ServiceDescriptor.Scoped<INhAssistantNotifier, DeliveryNotifier>());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, AlertPrincipalFactory>());
            },
            assistantBuilder: assistant =>
            {
                assistant
                    .AddSubjectType<AlertTestSubjectResolver>()
                    .AddAlertKind(ForWorkers, kind => Titled(kind, "tests.alerts.task-for-workers")
                        .Recipients(NhAssistantAlertRecipients.SubjectMembers(NhAiSubjectRole.Participant)))
                    .AddAlertKind(ForOwnerAndApprovers, kind => Titled(kind, "tests.alerts.task-for-owner-and-approvers")
                        .Recipients(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.SubjectMembers(NhAiSubjectRole.Approver))))
                    .AddAlertKind(Overdue, kind => Titled(kind, "tests.alerts.task-overdue").ResolveWhenNotRaised())
                    .AddAlertKind(Note, kind => Titled(kind, "tests.alerts.task-note"))
                    .AddAlertKind(Unlisted, kind => Titled(kind, "tests.alerts.task-unlisted"))
                    .AddAlertKind(Blocked, kind =>
                    {
                        Titled(kind, "tests.alerts.task-blocked");
                        if (withCheck)
                        {
                            kind.Revalidate<BlockedCheck>();
                        }
                    })
                    .ConfigureAgent(AgentId, agent => agent
                        .WorksOnAllObjects()
                        .WorksOnApplication()
                        .UseRuleHandler<AlertTestRules>()
                        .Alerts(Overdue, Note));
            });
    }

    private static NhAssistantAlertKindBuilder Titled(NhAssistantAlertKindBuilder kind, string displayNameKey)
    {
        return kind
            .DisplayName(displayNameKey)
            .Title((_, parameters) => parameters["task"] + " needs attention");
    }

    private static async Task<NhAssistantAgentTurnResult> RunRulesAsync(
        AssistantTestHost host,
        NhAiSubjectRef subject,
        bool shadow = false,
        CancellationToken cancellationToken = default)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, includeDisabled: true, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None, shadow)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "round",
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
            new NhAssistantAgentTurnRequest(agentTurn, [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Round.")]),
            cancellationToken);
    }

    private static async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantAlertReceipt>> RaiseAsync(AssistantTestHost host, NhAssistantAlert alert)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RaiseAsync(alert);
    }

    private static async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantAlertRevalidationResult>> RevalidateAsync(AssistantTestHost host, NhAiSubjectRef subject)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RevalidateAsync(subject);
    }

    private static async Task<NhAssistantAlertDispatchResult> DispatchAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantAlertDispatcher>().DispatchAsync(CancellationToken.None);
    }

    private static Task<AssistantAlert?> OpenOrNullAsync(AssistantTestHost host, string kindId, NhAiSubjectRef task, string key)
    {
        return host.Services.GetRequiredService<NhAssistantAlertStore>()
            .FindOpenAsync(ScopeKey, kindId, task, key, shadow: false, CancellationToken.None);
    }

    private static Task<AssistantAlert?> FindAsync(AssistantTestHost host, Guid alertId)
    {
        return host.Services.GetRequiredService<NhAssistantAlertStore>().FindAsync(alertId, CancellationToken.None);
    }
}

/// <summary>
/// What the blocked-task check of the lifecycle tests says per key: resolve, throw, or raise the alert
/// again with other content while it checks, as another round may. A test sets it before it revalidates,
/// and one revalidation checks its alerts one after the other.
/// </summary>
public sealed class BlockedCheckScript
{
    public HashSet<string> Resolve { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Throw { get; } = new(StringComparer.Ordinal);

    public HashSet<string> RaiseAgain { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<NhAssistantAlertCheckContext> Seen { get; } = new();
}

public sealed class BlockedCheck(BlockedCheckScript script, INhAssistantAlerts alerts) : INhAssistantAlertCheck
{
    public async ValueTask<NhAssistantAlertValidity> CheckAsync(NhAssistantAlertCheckContext context, CancellationToken cancellationToken = default)
    {
        script.Seen.Enqueue(context);
        if (script.Throw.Contains(context.Key))
        {
            throw new InvalidOperationException("The check broke on " + context.Parameters["task"]);
        }
        if (script.RaiseAgain.Contains(context.Key))
        {
            var raised = await alerts.RaiseAsync(
                new NhAssistantAlert(context.KindId, context.Subject)
                {
                    Key = context.Key,
                    Parameters = new Dictionary<string, string> { ["task"] = "Changed meanwhile" }
                },
                cancellationToken);
            Assert.Equal(NhAssistantAlertOutcome.Updated, raised.Data!.Outcome);
        }

        return script.Resolve.Contains(context.Key) ? NhAssistantAlertValidity.Resolve : NhAssistantAlertValidity.StillValid;
    }
}

/// <summary>
/// The lifecycle settings of an alert kind as code declares them; no database is involved.
/// </summary>
public sealed class AssistantAlertLifecycleKindTests
{
    [Fact]
    public void Member_selectors_need_at_least_the_reader_role_and_combine_to_the_lowest()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NhAssistantAlertRecipients.SubjectMembers(NhAiSubjectRole.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => NhAssistantAlertRecipients.SubjectMembers((NhAiSubjectRole)15));

        var approvers = NhAssistantAlertRecipients.SubjectMembers(NhAiSubjectRole.Approver);
        var workers = NhAssistantAlertRecipients.SubjectMembers(NhAiSubjectRole.Participant);
        var combined = approvers.Or(NhAssistantAlertRecipients.SubjectResponsible).Or(workers);

        Assert.Equal(NhAiSubjectRole.Approver, approvers.MinimumMemberRole);
        Assert.Equal(NhAiSubjectRole.Participant, combined.MinimumMemberRole);
        Assert.Null(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit).MinimumMemberRole);
        Assert.Equal(
            new[] { NhAssistantAlertRecipientSelector.SubjectMembers, NhAssistantAlertRecipientSelector.SubjectResponsible },
            combined.Selectors);
        Assert.Equal("SubjectMembers(Participant) or SubjectResponsible", combined.ToString());
    }

    [Fact]
    public void A_kind_declares_resolving_by_omission_and_its_check_which_the_application_services_provide()
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(AssistantTestData.Agent("project-assistant"))
            .AddAlertKind("task-overdue", kind => kind.DisplayName("app.alerts.task-overdue").Title((_, _) => "Overdue").ResolveWhenNotRaised())
            .AddAlertKind("task-blocked", kind => kind.DisplayName("app.alerts.task-blocked").Title((_, _) => "Blocked").Revalidate<BlockedCheck>())
            .ConfigureAgent("project-assistant", agent => agent.Alerts("task-overdue")));
        var state = services.BuildServiceProvider().GetRequiredService<NhAssistantRegistrationState>();

        var overdue = state.AlertKinds["task-overdue"];
        var blocked = state.AlertKinds["task-blocked"];

        Assert.True(overdue.ResolveWhenNotRaised);
        Assert.Null(overdue.CheckType);
        Assert.False(blocked.ResolveWhenNotRaised);
        Assert.Equal(typeof(BlockedCheck), blocked.CheckType);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(BlockedCheck) && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.True(NhAssistantAlertService.ResolvesWhenNotRaised(state.AgentRuns["project-assistant"], state));
    }
}
