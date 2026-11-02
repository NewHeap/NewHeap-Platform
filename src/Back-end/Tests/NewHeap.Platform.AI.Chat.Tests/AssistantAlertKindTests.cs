using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Alert kinds and alerts as code declares them (FB87): what a kind needs, what it may combine, the
/// bounds of an alert and the startup check of the kinds an agent lists. No database is involved.
/// </summary>
public sealed class AssistantAlertKindTests
{
    private const string AgentId = "project-assistant";

    [Fact]
    public void A_kind_declares_its_name_urgency_texts_recipients_repeat_delivery_choices_and_push()
    {
        var state = State(assistant => assistant.AddAlertKind("task-due-today", kind => kind
            .DisplayName("app.alerts.task-due-today")
            .Urgency(NhAssistantAlertUrgency.Warning)
            .Title((culture, parameters) => culture.TwoLetterISOLanguageName == "nl" ? $"{parameters["task"]} moet vandaag af" : $"{parameters["task"]} is due today")
            .Text((_, parameters) => parameters["project"])
            .Recipients(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit).Or(NhAssistantAlertRecipients.Explicit))
            .RepeatAfter(TimeSpan.FromDays(1))
            .Delivery(delivery => delivery.DailyDigest(new TimeOnly(7, 30)).WorkdaysOnly().ImmediatelyFrom(NhAssistantAlertUrgency.Urgent))
            .PersonMayChange(NhAssistantAlertChoices.Mute | NhAssistantAlertChoices.DigestTime)
            .PushShows(NhAssistantAlertPushContent.TitleAndText)));

        var kind = state.AlertKinds["task-due-today"];

        Assert.Equal(("app.alerts.task-due-today", NhAssistantAlertUrgency.Warning), (kind.DisplayNameKey, kind.Urgency));
        Assert.Equal(
            new[] { NhAssistantAlertRecipientSelector.SubjectResponsible, NhAssistantAlertRecipientSelector.Explicit },
            kind.Recipients.Selectors);
        Assert.Equal(TimeSpan.FromDays(1), kind.RepeatAfter);
        Assert.Equal(
            new NhAssistantAlertDelivery(NhAssistantAlertDeliveryMode.DailyDigest, new TimeOnly(7, 30), true, NhAssistantAlertUrgency.Urgent),
            kind.Delivery);
        Assert.True(kind.Delivery.IsImmediateFor(NhAssistantAlertUrgency.Urgent));
        Assert.False(kind.Delivery.IsImmediateFor(NhAssistantAlertUrgency.Warning));
        Assert.Equal((NhAssistantAlertChoices.Mute | NhAssistantAlertChoices.DigestTime, false), (kind.Choices, kind.Required));
        Assert.Equal(NhAssistantAlertPushContent.TitleAndText, kind.PushShows);
        var parameters = new Dictionary<string, string> { ["task"] = "Report", ["project"] = "Launch" };
        Assert.Equal("Report moet vandaag af", kind.Title(CultureInfo.GetCultureInfo("nl-NL"), parameters));
        Assert.Equal("Launch", kind.Text!(CultureInfo.InvariantCulture, parameters));
    }

    [Fact]
    public void A_kind_without_choices_is_delivered_immediately_shows_the_title_only_and_a_required_one_cannot_be_muted()
    {
        var state = State(assistant => assistant
            .AddAlertKind("task-note", kind => kind.DisplayName("app.alerts.task-note").Title((_, _) => "A note"))
            .AddAlertKind("task-escalated", kind => kind.DisplayName("app.alerts.task-escalated").Title((_, _) => "Escalated").Required()));

        var note = state.AlertKinds["task-note"];
        var escalated = state.AlertKinds["task-escalated"];

        Assert.Equal(NhAssistantAlertDelivery.Immediately, note.Delivery);
        Assert.Equal((NhAssistantAlertUrgency.Info, NhAssistantAlertPushContent.TitleOnly, NhAssistantAlertChoices.All), (note.Urgency, note.PushShows, note.Choices));
        Assert.Equal(new[] { NhAssistantAlertRecipientSelector.SubjectResponsible }, note.Recipients.Selectors);
        Assert.Null(note.RepeatAfter);
        Assert.True(escalated.Required);
        Assert.Equal(NhAssistantAlertChoices.DigestTime | NhAssistantAlertChoices.Channels, escalated.Choices);
    }

    [Fact]
    public void A_kind_that_misses_its_name_or_title_or_contradicts_itself_is_refused()
    {
        Assert.Contains("DisplayName", Assert.Throws<InvalidOperationException>(() => State(assistant => assistant
            .AddAlertKind("task-note", kind => kind.Title((_, _) => "A note")))).Message);
        Assert.Contains("Title", Assert.Throws<InvalidOperationException>(() => State(assistant => assistant
            .AddAlertKind("task-note", kind => kind.DisplayName("app.alerts.task-note")))).Message);
        Assert.Contains("required", Assert.Throws<InvalidOperationException>(() => State(assistant => assistant
            .AddAlertKind("task-note", kind => kind.DisplayName("app.alerts.task-note").Title((_, _) => "A note").Required().PersonMayChange(NhAssistantAlertChoices.All)))).Message);
        Assert.Contains("DailyDigest", Assert.Throws<InvalidOperationException>(() => State(assistant => assistant
            .AddAlertKind("task-note", kind => kind.DisplayName("app.alerts.task-note").Title((_, _) => "A note").Delivery(delivery => delivery.WorkdaysOnly())))).Message);
        Assert.Contains("already registered", Assert.Throws<InvalidOperationException>(() => State(assistant => assistant
            .AddAlertKind("task-note", kind => kind.DisplayName("app.alerts.task-note").Title((_, _) => "A note"))
            .AddAlertKind("task-note", kind => kind.DisplayName("app.alerts.task-note").Title((_, _) => "A note")))).Message);
        Assert.Throws<ArgumentException>(() => State(assistant => assistant.AddAlertKind("Task Note", kind => kind.DisplayName("x").Title((_, _) => "x"))));
        Assert.Throws<ArgumentException>(() => State(assistant => assistant.AddAlertKind(new string('a', NhAssistantAlertLimits.MaxKindIdLength + 1), kind => kind.DisplayName("x").Title((_, _) => "x"))));
        Assert.Throws<ArgumentOutOfRangeException>(() => State(assistant => assistant.AddAlertKind("task-note", kind => kind.RepeatAfter(TimeSpan.FromSeconds(30)))));
        Assert.Throws<ArgumentOutOfRangeException>(() => State(assistant => assistant.AddAlertKind("task-note", kind => kind.Delivery(delivery => delivery.DailyDigest(new TimeOnly(7, 30, 15))))));
    }

    [Fact]
    public void An_agent_that_lists_an_unregistered_kind_fails_startup_validation()
    {
        var listed = State(assistant => assistant
            .AddAlertKind("task-due-today", kind => kind.DisplayName("app.alerts.task-due-today").Title((_, _) => "Due"))
            .ConfigureAgent(AgentId, agent => agent.Alerts("task-due-today").Alerts("task-due-today")));
        listed.ValidateAgentRuns();
        Assert.Equal(new[] { "task-due-today" }, listed.AgentRuns[AgentId].AlertKinds);

        var unknown = State(assistant => assistant.ConfigureAgent(AgentId, agent => agent.Alerts("task-due-today")));

        var exception = Assert.Throws<InvalidOperationException>(unknown.ValidateAgentRuns);
        Assert.Contains("task-due-today", exception.Message);
        Assert.Contains("AddAlertKind", exception.Message);
        Assert.Throws<ArgumentException>(() => State(assistant => assistant.ConfigureAgent(AgentId, agent => agent.Alerts())));
        // Alerts apply to runs on subjects, so a chat-only agent refuses them.
        var chatOnly = State(assistant => assistant
            .AddAlertKind("task-due-today", kind => kind.DisplayName("app.alerts.task-due-today").Title((_, _) => "Due"))
            .ConfigureAgent(AgentId, agent => agent.ChatOnly().Alerts("task-due-today")));
        Assert.Contains("Alerts", Assert.Throws<InvalidOperationException>(chatOnly.ValidateAgentRuns).Message);
    }

    [Fact]
    public void An_alert_bounds_its_key_parameters_and_recipients_and_keeps_its_content_out_of_its_text_form()
    {
        var task = new NhAiSubjectRef("task", "t-1");
        var alert = new NhAssistantAlert("task-due-today", task)
        {
            Key = "2026-11-02",
            Parameters = new Dictionary<string, string> { ["task"] = new string('x', NhAssistantAlertLimits.MaxParameterValueLength + 10), ["due-date"] = "2026-11-02" },
            Recipients = [" reader-1 ", "reader-1", "owner-1"]
        };

        Assert.Equal(NhAssistantAlertLimits.MaxParameterValueLength, alert.Parameters["task"].Length);
        Assert.Equal(new[] { "reader-1", "owner-1" }, alert.Recipients);
        Assert.DoesNotContain("2026-11-02", alert.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("reader-1", alert.ToString(), StringComparison.Ordinal);
        Assert.Contains("task-due-today", alert.ToString(), StringComparison.Ordinal);
        // A copy is checked as the original is.
        Assert.Throws<ArgumentException>(() => alert with { Key = "line\nbreak" });
        Assert.Throws<ArgumentException>(() => alert with { Key = new string('k', NhAssistantAlertLimits.MaxKeyLength + 1) });
        Assert.Throws<ArgumentException>(() => alert with { Parameters = new Dictionary<string, string> { ["1st"] = "x" } });
        Assert.Throws<ArgumentException>(() => alert with
        {
            Parameters = Enumerable.Range(0, NhAssistantAlertLimits.MaxParameters + 1).ToDictionary(index => "p" + index, index => "x")
        });
        Assert.Throws<ArgumentException>(() => alert with
        {
            Recipients = Enumerable.Range(0, NhAssistantAlertLimits.MaxExplicitRecipients + 1).Select(index => "person-" + index).ToArray()
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => alert with { Urgency = (NhAssistantAlertUrgency)7 });
        Assert.Throws<ArgumentException>(() => new NhAssistantAlert("Task", task));
        Assert.Throws<ArgumentException>(() => new NhAssistantAlertKey("task-due-today", task, " padded "));
        Assert.Null(new NhAssistantAlertKey("task-due-today", task, null).Key);
        Assert.Equal(string.Empty, new NhAssistantAlertKey("task-due-today", task).Key);
    }

    [Fact]
    public void A_rule_result_carries_its_alerts_and_resolutions_next_to_its_summary()
    {
        var task = new NhAiSubjectRef("task", "t-1");
        var result = NhAssistantRuleResult.Done("Checked.")
            .WithAlerts(new NhAssistantAlert("task-due-today", task))
            .ResolveAlerts(new NhAssistantAlertKey("task-due-today", task, "2026-11-01"))
            .WithAlerts(new NhAssistantAlert("task-due-today", task) { Key = "2026-11-02" });

        Assert.Equal("Checked.", result.Summary);
        Assert.Equal(new[] { string.Empty, "2026-11-02" }, result.Alerts.Select(alert => alert.Key));
        Assert.Equal("2026-11-01", Assert.Single(result.AlertsToResolve).Key);
        Assert.Throws<ArgumentException>(() => result.WithAlerts(Enumerable.Range(0, NhAssistantAlertLimits.MaxAlertsPerResult).Select(_ => new NhAssistantAlert("task-due-today", task)).ToArray()));
        Assert.True(NhAssistantRuleResult.HandOver("Unclear.").WithAlerts(new NhAssistantAlert("task-due-today", task)).HandsOver);
    }

    [Fact]
    public void The_alert_limits_are_bounded()
    {
        Assert.Throws<InvalidOperationException>(() => State(assistant => assistant.WithLimits(limits => limits.MaxAlertsPerRun = 0)));
        Assert.Throws<InvalidOperationException>(() => State(assistant => assistant.WithLimits(limits => limits.MaxAlertsPerSubjectPerDay = 1_001)));
        Assert.Throws<InvalidOperationException>(() => State(assistant => assistant.WithLimits(limits => limits.AlertRetention = TimeSpan.FromHours(1))));
        var limits = new NhAssistantLimits();
        Assert.Equal((50, 20, TimeSpan.FromDays(30)), (limits.MaxAlertsPerRun, limits.MaxAlertsPerSubjectPerDay, limits.AlertRetention));
    }

    private static NhAssistantRegistrationState State(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(assistant =>
        {
            assistant.AddAgent(AssistantTestData.Agent(AgentId));
            configure(assistant);
        });
        return services.BuildServiceProvider().GetRequiredService<NhAssistantRegistrationState>();
    }
}
