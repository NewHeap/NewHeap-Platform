using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A person's side of alerts (FB87 phase 1, lane AU) over HTTP: their own list with its filters, grouped
/// by parent in their language, the badge's counts, reading and dismissing; and that nobody reads or
/// changes another person's alerts, nor an alert about a subject they lost access to, also in an
/// impersonation session; on both relational providers.
/// </summary>
public sealed class AssistantAlertEndpointTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string DueToday = "task-due-today";
    private const string Owner = "owner-1";
    private const string Reader = "reader-1";
    private const string Outsider = "outsider-1";
    private const string Administrator = "admin-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_person_lists_reads_and_dismisses_their_own_alerts_grouped_by_parent_in_their_language(AssistantTestProvider provider)
    {
        var clock = new AssistantTestClock(DateTimeOffset.UtcNow.AddMinutes(-30));
        await using var app = await StartAsync(provider, clock);
        // Task a and b belong to project 1, task c to project 2; b is the most recent, then c, then a.
        var a = await RaiseAsync(app, TaskOf("a-" + Guid.NewGuid().ToString("N")[..8], "p1"), "Write the report", NhAssistantAlertUrgency.Warning);
        clock.Advance(TimeSpan.FromMinutes(1));
        var c = await RaiseAsync(app, TaskOf("c-" + Guid.NewGuid().ToString("N")[..8], "p2"), "Book the room", NhAssistantAlertUrgency.Info);
        clock.Advance(TimeSpan.FromMinutes(1));
        var b = await RaiseAsync(app, TaskOf("b-" + Guid.NewGuid().ToString("N")[..8], "p1"), "Send the invoice", NhAssistantAlertUrgency.Urgent);
        using var owner = CreateClient(app, Owner, "en");
        using var dutch = CreateClient(app, Owner, "nl");

        // The flat list: the most recent first, written in the caller's language, with its subject and parent.
        var list = await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts"));
        Assert.Equal([b.AlertId, c.AlertId, a.AlertId], Ids(list));
        Assert.Equal(3, list.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("groups").ValueKind);
        var urgent = list.GetProperty("items")[0];
        Assert.Equal(
            (DueToday, "tests.alerts.task-due-today", NhAssistantAlertUrgencyNames.Urgent, "Send the invoice is due today", "Open the task before five.", NhAssistantAlertListStatuses.Unread),
            (urgent.GetProperty("kindId").GetString(),
                urgent.GetProperty("displayNameKey").GetString(),
                urgent.GetProperty("urgency").GetString(),
                urgent.GetProperty("title").GetString(),
                urgent.GetProperty("text").GetString(),
                urgent.GetProperty("status").GetString()));
        Assert.Equal(("task", "Task", $"/tasks/{b.Subject.Id}"), (
            urgent.GetProperty("subjectType").GetString(),
            urgent.GetProperty("subjectTypeName").GetString(),
            urgent.GetProperty("subjectLink").GetString()));
        Assert.Equal(("project", "p1", "Project p1"), (
            urgent.GetProperty("parent").GetProperty("subjectType").GetString(),
            urgent.GetProperty("parent").GetProperty("subjectId").GetString(),
            urgent.GetProperty("parent").GetProperty("displayName").GetString()));
        Assert.NotEqual(Guid.Empty, urgent.GetProperty("deliveryId").GetGuid());
        Assert.False(urgent.GetProperty("isRead").GetBoolean());
        var inDutch = await ReadJsonAsync(await dutch.GetAsync("/api/assistant/alerts"));
        Assert.Equal("Send the invoice moet vandaag af", inDutch.GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Equal("Taak", inDutch.GetProperty("items")[0].GetProperty("subjectTypeName").GetString());

        // Grouped by parent: project 1 with its two tasks first, by its most recent alert, then project 2.
        var grouped = await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?grouped=true"));
        Assert.Equal([b.AlertId, a.AlertId, c.AlertId], Ids(grouped));
        Assert.Equal([("p1", 2, 2, false, false), ("p2", 1, 1, false, false)], Shape(grouped));
        Assert.Equal(
            [(b.Subject.Id, 1, 1), (a.Subject.Id, 1, 1)],
            grouped.GetProperty("groups")[0].GetProperty("subjects").EnumerateArray()
                .Select(subject => (subject.GetProperty("subjectId").GetString(), subject.GetProperty("count").GetInt32(), subject.GetProperty("total").GetInt32())));
        var second = await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?grouped=true&itemsPerPage=1&page=2"));
        Assert.Equal([a.AlertId], Ids(second));
        Assert.Equal([("p1", 1, 2, true, false)], Shape(second));

        // The badge counts the unread alerts per urgency.
        Assert.Equal((3, 1, 1, 1), Summary(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts/summary"))));

        // Reading one marks it read once; reading it again changes nothing.
        var read = await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{a.AlertId}/read", null));
        Assert.Equal((NhAssistantAlertListStatuses.Read, true), (read.GetProperty("status").GetString(), read.GetProperty("isRead").GetBoolean()));
        var readAt = read.GetProperty("readAt").GetDateTimeOffset();
        var again = await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{a.AlertId}/read", null));
        Assert.Equal(readAt, again.GetProperty("readAt").GetDateTimeOffset());
        Assert.Equal((2, 1, 0, 1), Summary(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts/summary"))));
        Assert.Equal([a.AlertId], Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?status=read"))));
        Assert.Equal([b.AlertId, c.AlertId], Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?status=unread"))));

        // The filters on urgency, kind and subject.
        Assert.Equal([a.AlertId], Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?urgency=warning"))));
        Assert.Equal([b.AlertId, a.AlertId], Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?urgency=warning&urgency=urgent"))));
        Assert.Empty(Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?kind=task-unknown"))));
        Assert.Equal([c.AlertId], Ids(await ReadJsonAsync(await owner.GetAsync($"/api/assistant/alerts?subjectType=task&subjectId={c.Subject.Id}"))));
        Assert.Equal((1, 0, 0, 1), Summary(await ReadJsonAsync(await owner.GetAsync($"/api/assistant/alerts/summary?subjectType=task&subjectId={b.Subject.Id}"))));

        // Dismissing one takes it out of the list and is audited once, without its content.
        var dismissed = await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{c.AlertId}/dismiss", null));
        Assert.Equal((NhAssistantAlertListStatuses.Dismissed, true), (dismissed.GetProperty("status").GetString(), dismissed.GetProperty("isDismissed").GetBoolean()));
        await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{c.AlertId}/dismiss", null));
        var audit = Assert.Single(Audit(app), evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertDismissed);
        Assert.Equal((Owner, c.AlertId, DueToday, (string?)null), (audit.ActorId, audit.AlertId!.Value, audit.AlertKind, audit.ImpersonationOriginActorId));
        Assert.Equal([b.AlertId, a.AlertId], Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts"))));
        Assert.Equal([c.AlertId], Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?status=dismissed"))));
        // Reading a dismissed alert changes nothing.
        Assert.Equal(
            NhAssistantAlertListStatuses.Dismissed,
            (await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{c.AlertId}/read", null))).GetProperty("status").GetString());

        // A resolved alert shows as resolved and no longer counts; the person can still dismiss it.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var resolved = await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>()
                .ResolveAsync(new NhAssistantAlertKey(DueToday, b.Subject, key: null));
            Assert.Equal(1, resolved.Data);
        }
        var closed = await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?status=closed"));
        Assert.Equal([b.AlertId], Ids(closed));
        Assert.Equal((NhAssistantAlertListStatuses.Resolved, true), (
            closed.GetProperty("items")[0].GetProperty("status").GetString(),
            closed.GetProperty("items")[0].GetProperty("isResolved").GetBoolean()));
        Assert.Equal([a.AlertId], Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts?status=open"))));
        Assert.Equal((0, 0, 0, 0), Summary(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts/summary"))));
        Assert.Equal(
            NhAssistantAlertListStatuses.Dismissed,
            (await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{b.AlertId}/dismiss", null))).GetProperty("status").GetString());

        // A malformed filter is refused.
        foreach (var malformed in new[]
                 {
                     "status=bogus", "urgency=high", $"subjectId={a.Subject.Id}", "subjectType=Not A Type", "page=0", "itemsPerPage=101",
                     string.Join('&', Enumerable.Range(0, 21).Select(index => $"kind=kind-{index}"))
                 })
        {
            Assert.Equal(
                NhAssistantAlertCodes.FilterInvalid,
                (await ReadJsonAsync(await owner.GetAsync($"/api/assistant/alerts?{malformed}"), HttpStatusCode.BadRequest)).GetProperty("code").GetString());
        }
        Assert.Equal(
            NhAssistantAlertCodes.FilterInvalid,
            (await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts/summary?urgency=high&subjectId=x"), HttpStatusCode.BadRequest)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Nobody_reads_or_changes_another_persons_alerts_nor_an_alert_after_losing_access_also_when_impersonating(AssistantTestProvider provider)
    {
        await using var app = await StartAsync(provider, new AssistantTestClock(DateTimeOffset.UtcNow));
        var subjects = app.Services.GetRequiredService<AlertListSubjects>();
        var task = TaskOf("t-" + Guid.NewGuid().ToString("N")[..8], "p1");
        var alert = await RaiseAsync(app, task, "Plan the launch", NhAssistantAlertUrgency.Warning, Reader, Outsider);
        // The outsider has no role on the task, so the alert reached the owner and the reader only.
        Assert.Equal(2, alert.RecipientCount);
        using var owner = CreateClient(app, Owner, "en");
        using var reader = CreateClient(app, Reader, "en");
        using var outsider = CreateClient(app, Outsider, "en");

        // Another person's alert is not listed, not counted and answers like an unknown one.
        Assert.Empty(Ids(await ReadJsonAsync(await outsider.GetAsync("/api/assistant/alerts"))));
        Assert.Equal((0, 0, 0, 0), Summary(await ReadJsonAsync(await outsider.GetAsync("/api/assistant/alerts/summary"))));
        foreach (var path in new[] { $"alerts/{alert.AlertId}/read", $"alerts/{alert.AlertId}/dismiss", $"alerts/{Guid.NewGuid()}/read", $"alerts/{Guid.NewGuid()}/dismiss" })
        {
            Assert.Equal(
                NhAssistantAlertCodes.NotFound,
                (await ReadJsonAsync(await outsider.PostAsync("/api/assistant/" + path, null), HttpStatusCode.NotFound)).GetProperty("code").GetString());
        }

        // The reader sees it until they lose access to the task; then it is gone and cannot be read.
        Assert.Equal([alert.AlertId], Ids(await ReadJsonAsync(await reader.GetAsync("/api/assistant/alerts"))));
        subjects.Roles[Reader] = NhAiSubjectRole.None;
        Assert.Empty(Ids(await ReadJsonAsync(await reader.GetAsync("/api/assistant/alerts?status=unread"))));
        Assert.Equal((0, 0, 0, 0), Summary(await ReadJsonAsync(await reader.GetAsync("/api/assistant/alerts/summary"))));
        await ReadJsonAsync(await reader.PostAsync($"/api/assistant/alerts/{alert.AlertId}/read", null), HttpStatusCode.NotFound);
        await ReadJsonAsync(await reader.PostAsync($"/api/assistant/alerts/{alert.AlertId}/dismiss", null), HttpStatusCode.NotFound);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<NhAssistantAlertStore>()
                .FindDeliveryAsync(alert.AlertId, Reader, CancellationToken.None);
            Assert.Equal(NhAssistantAlertDeliveryStatuses.Pending, stored!.Status);
        }
        Assert.DoesNotContain(Audit(app), evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertDismissed);

        // An impersonation session reads and dismisses the person's own alerts, naming who impersonates;
        // the user who impersonates sees none of them in their own session.
        using var impersonated = CreateClient(app, Owner, "en", impersonatedBy: Administrator);
        Assert.Equal([alert.AlertId], Ids(await ReadJsonAsync(await impersonated.GetAsync("/api/assistant/alerts"))));
        using var administrator = CreateClient(app, Administrator, "en");
        Assert.Empty(Ids(await ReadJsonAsync(await administrator.GetAsync("/api/assistant/alerts"))));
        await ReadJsonAsync(await administrator.PostAsync($"/api/assistant/alerts/{alert.AlertId}/dismiss", null), HttpStatusCode.NotFound);
        await ReadJsonAsync(await impersonated.PostAsync($"/api/assistant/alerts/{alert.AlertId}/dismiss", null));
        var audit = Assert.Single(Audit(app), evt => evt.Kind == NhAssistantAuditEventKind.AgentAlertDismissed);
        Assert.Equal((Owner, Administrator), (audit.ActorId, audit.ImpersonationOriginActorId));

        // A subject that no longer exists takes its alerts out of everyone's list.
        var other = await RaiseAsync(app, TaskOf("u-" + Guid.NewGuid().ToString("N")[..8], "p2"), "Close the books", NhAssistantAlertUrgency.Info);
        Assert.Equal([other.AlertId], Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts"))));
        subjects.Deleted[other.Subject.Id] = true;
        Assert.Empty(Ids(await ReadJsonAsync(await owner.GetAsync("/api/assistant/alerts"))));
        await ReadJsonAsync(await owner.PostAsync($"/api/assistant/alerts/{other.AlertId}/read", null), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_host_without_alert_kinds_lists_nothing_and_knows_no_alert()
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient());
        using var client = CreateClient(app, Owner, "en");

        var list = await ReadJsonAsync(await client.GetAsync("/api/assistant/alerts?grouped=true"));
        Assert.Equal((0, 0), (list.GetProperty("total").GetInt32(), list.GetProperty("groups").GetArrayLength()));
        Assert.Equal((0, 0, 0, 0), Summary(await ReadJsonAsync(await client.GetAsync("/api/assistant/alerts/summary"))));
        await ReadJsonAsync(await client.PostAsync($"/api/assistant/alerts/{Guid.NewGuid()}/read", null), HttpStatusCode.NotFound);
    }

    private Task<AssistantWebApplication> StartAsync(AssistantTestProvider provider, AssistantTestClock clock)
    {
        return AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            configure: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<AlertListSubjects>();
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, AlertPrincipalFactory>());
                services.AddSingleton<CapturedBusinessAuditSink>();
                services.AddScoped<INhAssistantBusinessAuditSink, CapturedBusinessAuditSinkAdapter>();
                // The request's language picks the culture the alerts are written in.
                services.Configure<RequestLocalizationOptions>(options =>
                {
                    CultureInfo[] cultures = [new CultureInfo("en-GB"), new CultureInfo("nl-NL")];
                    options.SupportedCultures = cultures;
                    options.SupportedUICultures = cultures;
                });
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<AlertListTaskResolver>(type => type
                    .Named(new() { ["en"] = "Task", ["nl"] = "Taak" }, fallbackLanguage: "en")
                    .OpenAt("/tasks/{id}"))
                .AddAlertKind(DueToday, kind => kind
                    .DisplayName("tests.alerts.task-due-today")
                    .Urgency(NhAssistantAlertUrgency.Warning)
                    .Title((culture, parameters) => culture.TwoLetterISOLanguageName == "nl"
                        ? $"{parameters["task"]} moet vandaag af"
                        : $"{parameters["task"]} is due today")
                    .Text((culture, _) => culture.TwoLetterISOLanguageName == "nl"
                        ? "Open de taak voor vijf uur."
                        : "Open the task before five.")
                    .Recipients(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit))),
            provider: provider);
    }

    private static NhAiSubjectRef TaskOf(string id, string project)
    {
        return new NhAiSubjectRef("task", project + "." + id);
    }

    /// <summary>
    /// Raises the alert of the day about <paramref name="task"/> from the application's code, for its
    /// owner and <paramref name="recipients"/>.
    /// </summary>
    private static async Task<RaisedAlert> RaiseAsync(
        AssistantWebApplication app,
        NhAiSubjectRef task,
        string name,
        NhAssistantAlertUrgency urgency,
        params string[] recipients)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var raised = await scope.ServiceProvider.GetRequiredService<INhAssistantAlerts>().RaiseAsync(new NhAssistantAlert(DueToday, task)
        {
            Key = "today",
            Urgency = urgency,
            Parameters = new Dictionary<string, string> { ["task"] = name },
            Recipients = recipients
        });
        Assert.True(raised.Success, string.Join(", ", raised.GetResultItems().Select(item => item.Name)));
        return new RaisedAlert(raised.Data!.AlertId, task, raised.Data.RecipientCount);
    }

    private sealed record RaisedAlert(Guid AlertId, NhAiSubjectRef Subject, int RecipientCount);

    private static HttpClient CreateClient(AssistantWebApplication app, string user, string language, string? impersonatedBy = null)
    {
        var client = app.CreateClient(user, impersonatedBy: impersonatedBy);
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue(language));
        return client;
    }

    private static NhAssistantAuditEvent[] Audit(AssistantWebApplication app)
    {
        return app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events.ToArray();
    }

    private static Guid[] Ids(JsonElement list)
    {
        return list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
    }

    private static (string?, int, int, bool, bool)[] Shape(JsonElement list)
    {
        return list.GetProperty("groups").EnumerateArray()
            .Select(group => (
                group.GetProperty("subjectId").GetString(),
                group.GetProperty("count").GetInt32(),
                group.GetProperty("total").GetInt32(),
                group.GetProperty("continuesFromPreviousPage").GetBoolean(),
                group.GetProperty("continuesOnNextPage").GetBoolean()))
            .ToArray();
    }

    private static (int Unread, int Info, int Warning, int Urgent) Summary(JsonElement summary)
    {
        var urgencies = summary.GetProperty("unreadByUrgency");
        return (
            summary.GetProperty("unread").GetInt32(),
            urgencies.GetProperty("info").GetInt32(),
            urgencies.GetProperty("warning").GetInt32(),
            urgencies.GetProperty("urgent").GetInt32());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(expected == response.StatusCode, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
    }
}

/// <summary>
/// The tasks of the alert list tests: a task's id names its project, which the resolver gives as its
/// parent; the owner is responsible. People read a task with the role they have; a deleted task no
/// longer exists.
/// </summary>
public sealed class AlertListSubjects
{
    public ConcurrentDictionary<string, NhAiSubjectRole> Roles { get; } = new(new Dictionary<string, NhAiSubjectRole>
    {
        ["owner-1"] = NhAiSubjectRole.Participant,
        ["reader-1"] = NhAiSubjectRole.Reader
    });

    public ConcurrentDictionary<string, bool> Deleted { get; } = new();
}

public sealed class AlertListTaskResolver(AlertListSubjects subjects) : INhAiSubjectAccessResolver
{
    public string SubjectType => "task";

    public ValueTask<NhAiSubjectInfo?> DescribeAsync(NhAiSubjectRef subject, CancellationToken cancellationToken = default)
    {
        if (subjects.Deleted.ContainsKey(subject.Id))
        {
            return ValueTask.FromResult<NhAiSubjectInfo?>(null);
        }

        var project = subject.Id.Split('.')[0];
        return ValueTask.FromResult<NhAiSubjectInfo?>(new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, null))
        {
            DisplayName = "Task " + subject.Id,
            ResponsibleActorId = "owner-1",
            Parent = new NhAiSubjectParent(new NhAiSubjectRef("project", project), "Project " + project)
        });
    }

    public ValueTask<NhAiSubjectAccess> ResolveAsync(NhAiSubjectRef subject, string actorId, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(subjects.Roles.TryGetValue(actorId, out var role) && role != NhAiSubjectRole.None
            ? new NhAiSubjectAccess(role)
            : NhAiSubjectAccess.None);
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (actorId, role) in subjects.Roles.Where(item => item.Value != NhAiSubjectRole.None))
        {
            await System.Threading.Tasks.Task.Yield();
            yield return new NhAiSubjectMember(actorId, role);
        }
    }
}
