---
id: nh-ai-alerts
title: "Warn people about an object with alerts of a kind declared in code, raised by rules and by the application without a proposal"
area: backend
reference: ai-alerts
summary: "Declare each kind of alert in code with AddAlertKind, let an agent's rules raise and resolve the kinds it lists with WithAlerts and ResolveAlerts, raise them from the application's code with INhAssistantAlerts, and rely on one open alert per kind, subject and key, on the recipient check before anything reaches a person, on expiry and on the bounded retention. People read, dismiss and count their own alerts through GET alerts with nh-assistant-alerts, the badge service and nh-assistant-notification-preferences."
sample-cases: ["SPM-392"]
public-symbols: ["AddAlertKind", "NhAssistantAlertKindBuilder", "NhAssistantAlertKind", "NhAssistantAlertUrgency", "NhAssistantAlertRecipients", "NhAssistantAlertRecipientSelector", "NhAssistantAlertDelivery", "NhAssistantAlertDeliveryBuilder", "NhAssistantAlertDeliveryMode", "NhAssistantAlertChoices", "NhAssistantAlertPushContent", "NhAssistantAgentRunBuilder.Alerts", "AlertKinds", "NhAssistantRuleResult.WithAlerts", "NhAssistantRuleResult.ResolveAlerts", "AlertsToResolve", "NhAssistantAlert", "NhAssistantAlertKey", "INhAssistantAlerts", "NhAssistantAlertReceipt", "NhAssistantAlertOutcome", "INhAssistantAlertPolicy", "NhAssistantAlertRecipientContext", "NhAssistantDefaultAlertPolicy", "UseAlertPolicy", "NhAssistantAlertCodes", "NhAssistantAlertLimits", "NhAssistantAlertStatuses", "NhAssistantAlertDeliveryStatuses", "NhAssistantAlertDropReasons", "MaxAlertsPerRun", "MaxAlertsPerSubjectPerDay", "AlertRetention", "AssistantAlert", "AssistantAlertDelivery", "NhAssistantAuditEventKind.AgentAlertRaised", "NhAssistantAuditEventKind.AgentAlertResolved", "NhAssistantAuditEventKind.AgentAlertDismissed", "NhAssistantAuditEventKind.AgentAlertRecipientDropped", "NhAssistantAuditEvent.AlertId", "NhAssistantAuditEvent.AlertKind", "NhAssistantAuditEvent.AlertRevision", "NhAssistantAuditEvent.AlertRecipientCount", "NhAssistantAlertListStatuses", "NhAssistantAlertUrgencyNames", "NhAssistantAlertListDto", "NhAssistantAlertDto", "NhAssistantAlertParentDto", "NhAssistantAlertGroupDto", "NhAssistantAlertSubjectGroupDto", "NhAssistantAlertSummaryDto", "NhAssistantAlertUrgencyCountsDto", "NhAssistantAlertJsonSerializerContext", "NhAssistantAlertsComponent", "NhAssistantAlertBadgeService", "NhAssistantNotificationPreferencesComponent", "NhAssistantAlertDensity", "AlertSubjectTarget", "NotificationAlertKind", "NotificationAlertKindChange", "UpdateNotificationSettingsRequest", "getAlerts", "getAlertSummary", "markAlertRead", "dismissAlert", "updateNotificationPreferences"]
skills: ["newheap-backend-development", "newheap-background-processing"]
providers: ["sql-server", "postgresql"]
risk: high
---
## Preferred approach

Use an alert when an agent or the application must warn a person about an object and there is
nothing to approve: "your task is due today", "the document's deadline is in three days". An
alert never changes data, is never a proposal and grants nothing. When the agent should change
something, propose it instead (see the agent runs rule).

Declare every kind in code with `AddAlertKind(id, kind => ...)`. A kind needs a front-end
translation key for its name (`DisplayName`) and a `Title`, written from the alert's named
parameters per culture as `Func<CultureInfo, IReadOnlyDictionary<string, string>, string>`; add a
`Text` for more than a title. The platform asks for the texts with the reader's culture current
whenever it shows or delivers the alert, and once when the alert is raised, which a title that
fails or is empty refuses with `assistant-alert-text-invalid`. Keep the texts in the application's
resources, as SPM-392 does, so every language follows the translations. Give the kind an
`Urgency` (`Info`, `Warning`, `Urgent`), its recipients with `NhAssistantAlertRecipients`
(`SubjectResponsible`, `Explicit`, `SubjectApprovers`, combined with `Or`), an optional
`RepeatAfter` and its default `Delivery`: immediately, one `DailyDigest(time)` with
`WorkdaysOnly()`, and `ImmediatelyFrom(urgency)` for alerts that cannot wait. Declare what a person
may change with `PersonMayChange` (`Mute`, `DigestTime`, `Channels`), or make the kind `Required()`
so nobody mutes it. `PushShows` stays `TitleOnly` unless the text is safe on a lock screen.

```csharp
assistant
    .AddAlertKind("task-due-today", kind => kind
        .DisplayName("app.alerts.task-due-today")
        .Urgency(NhAssistantAlertUrgency.Warning)
        .Title((culture, parameters) => Texts.Format("TaskDueToday", culture, parameters["task"]))
        .Recipients(NhAssistantAlertRecipients.SubjectResponsible.Or(NhAssistantAlertRecipients.Explicit))
        .RepeatAfter(TimeSpan.FromDays(1))
        .Delivery(delivery => delivery.DailyDigest(new TimeOnly(7, 30)).WorkdaysOnly().ImmediatelyFrom(NhAssistantAlertUrgency.Urgent)))
    .ConfigureAgent("deadline-watch", agent => agent
        .UseRuleHandler<DeadlineWatchRules>()
        .Alerts("task-due-today"));
```

An agent lists the kinds its rules may raise with `Alerts(...)`; startup refuses a kind that is not
registered. Rules raise alerts with `NhAssistantRuleResult.Done(...).WithAlerts(...)` and resolve them
with `ResolveAlerts(...)`; both apply when the rules end. A rule result that raises or resolves a kind
its agent does not list fails the run with `assistant-alert-kind-not-listed` and applies none of its
alerts, so the mistake shows in the runs view instead of silently warning nobody. A paused or stopped
agent raises nothing (it still resolves), and a shadow agent or shadow run records shadow alerts that
reach nobody. The application's own code raises and resolves alerts without an agent through
`INhAssistantAlerts.RaiseAsync` and `ResolveAsync`, which return `TaskResult` with the stable codes of
`NhAssistantAlertCodes`.

An alert carries its kind, its subject, a `Key`, bounded string `Parameters`, optional explicit
`Recipients` and an optional `ExpiresAt`; its subject is its link, which the host routes as it routes
the subject. One open alert exists per kind, subject and key: raise the same alert every round and let
the platform deduplicate. Raising it again with new content updates it in place as its next revision;
it reaches people again only when its urgency rises or after the kind's `RepeatAfter`, and never a
person who dismissed it until the key changes. Choose the key so that it changes exactly when people
should hear about it again, for example the due date. Resolve an alert when its reason is gone, or
give it an expiry; a resolved or expired alert takes back the deliveries that did not go out, and the
run maintenance purges closed alerts after `NhAssistantLimits.AlertRetention` (30 days by default).

Every recipient is checked before anything reaches them, when the alert is raised, delivered and read:
the person exists, the application's principal factory still rebuilds them for the subject in its
division or tenant, and the subject's resolver gives them at least `Reader`. Anyone else is left out
with the content-free audit `AgentAlertRecipientDropped`. Replace the selectors only with an
`INhAssistantAlertPolicy` registered through `UseAlertPolicy`; the check applies to what it returns too.
`MaxAlertsPerRun` and `MaxAlertsPerSubjectPerDay` bound the noise. The audit kinds
`AgentAlertRaised`, `AgentAlertResolved`, `AgentAlertDismissed` and `AgentAlertRecipientDropped` carry
identifiers and codes only; the parameters are the application's data and stay in the alert's store.

Alerts add the `AssistantAlert` and `AssistantAlertDelivery` tables: apply the `Alerts` migration of
the SQL Server or PostgreSQL assistant storage before raising the first alert. Everything is opt-in;
an application without alert kinds sees no change.

### A person's alerts: the API and the components

`MapNewHeapAssistant` maps a person's side of alerts with the access policy of the assistant
endpoints, and each endpoint reads only the caller's own deliveries. `GET alerts` lists them the most
recently raised first, paged as the approval inbox pages (`page`, `itemsPerPage` up to 100), with the
title and text written in the caller's language, the kind and its name key, the urgency (`info`,
`warning`, `urgent`, `NhAssistantAlertUrgencyNames`), the subject with its parent and their links, and
the caller's state as `status` (`NhAssistantAlertListStatuses`): `unread` or `read` while the alert is
open, `resolved` or `expired` once it closed, `dismissed` once the caller dismissed it. Filter with
`status` (one of those, or `open` and `closed`), `kind`, `subjectType` with `subjectId` and `urgency`;
without a status the list shows every alert the caller did not dismiss. `grouped=true` groups by the
parent the subject's resolver names, as the inbox groups. `GET alerts/summary` gives a badge its
unread count in total and per urgency. `POST alerts/{id}/read` and `POST alerts/{id}/dismiss` are
idempotent and return the alert as the list now shows it; a read delivery that still waits for a
digest is not sent, and the first dismissal is audited as `AgentAlertDismissed`, with the user who
impersonates in an impersonation session. Every alert passes the recipient check again with the
caller's tenant when it is read, so a person who lost access to a subject no longer reads its stored
text: it leaves the list and the counts, and its id answers `404 assistant-alert-not-found`, as do
another person's alerts. A malformed filter answers `400 assistant-alert-filter-invalid`.

In Angular, `@newheap/platform-ai-chat` shows the list with `nh-assistant-alerts`
(`NhAssistantAlertsComponent`): grouped by default, `density: 'compact'` for one row per alert, read
and dismiss in place, resolved and expired alerts shown as such, and `openSubject` with the subject
and its link, which the host routes; the component never navigates. `NhAssistantAlertBadgeService`
gives the shell its badge: call `watch()` where the badge shows and the function it returns when it
no longer does; it reads the counts on load, on focus and visibility, every five minutes while the page
is visible and after the person's own actions. `nh-assistant-notification-preferences`
(`NhAssistantNotificationPreferencesComponent`) shows the push switch of `NhAssistantPushService`, the
person's time zone and per kind what the kind lets them change, and saves through `PUT notifications`.
`NhAssistantApiService` has `getAlerts`, `getAlertSummary`, `markAlertRead`, `dismissAlert` and
`updateNotificationPreferences`; `provideNhAssistantMockApi` serves all of it from the scenario's
`alerts` without a server.

```typescript
// The shell: a bell with the unread count, coloured by the highest urgency.
readonly alertBadge = inject(NhAssistantAlertBadgeService);
private readonly release = this.alertBadge.watch();

// The alerts page: the host routes the subject of an opened alert.
openSubject(target: AlertSubjectTarget): void {
  void this.router.navigateByUrl(target.link ?? `/projects/${target.subjectId}`);
}
```

## Avoid

- Raising a proposal, or an application message in a thread, to warn a person about an object.
- Building the text of an alert in one language when it is raised, or putting the text into the key.
- A key that changes every round, such as a timestamp or a count, which warns people every round.
- Choosing recipients by hand and skipping the platform's check, or naming people who cannot read
  the subject and relying on the application to filter them later.
- Writing parameters, titles or texts into logs or the audit.
- Showing the text on a lock screen (`PushShows(TitleAndText)`) when it may carry confidential data.
- Reading the alert store yourself for a person's list or badge instead of `GET alerts`, which checks
  every alert against the person's access again.
- Navigating from inside the alert list, or polling the summary yourself next to the badge service.

## Verification

Run the rules twice on the same subject and assert one open alert at its first revision; raise it
again with other content, a higher urgency and after `RepeatAfter`, and assert the revision and which
deliveries wait. Dismiss it and assert the person is not planned again until the key changes. Resolve
it and let it expire, then move the application's clock past the retention and assert the maintenance
purged it. Name a person without `Reader`, a person of another division and a person the principal
factory no longer rebuilds, and assert they get no delivery and an `AgentAlertRecipientDropped` audit.
Assert that an unlisted kind fails the run, that a shadow run's alerts reach nobody and that a paused
agent raises nothing. Run the store tests on SQL Server and PostgreSQL. SPM-392 is the executable
reference.

For a person's side, read `GET alerts` as the recipient and assert the title in their language, the
subject's link and the groups per parent across pages; mark one read and dismiss one twice and assert
the badge's counts and one `AgentAlertDismissed`. Assert that another person, a person who lost access
to the subject and an impersonation session's origin user list nothing and get `404` for the id, on
SQL Server and PostgreSQL. In Angular, render the list and the notification choices on
`provideNhAssistantMockApi` with an `alerts` scenario.
