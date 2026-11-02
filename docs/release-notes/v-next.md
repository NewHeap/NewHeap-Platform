# v-next

## NewHeap.Platform.Common: time filters of a collection on every provider

- A collection filter on a `DateTime` or `DateTimeOffset` member reads its value as an instant in UTC on SQL Server
  and PostgreSQL, whatever zone the host runs in. `Z` and an offset keep their instant, and a value with neither,
  or a date alone such as `2026-11-01`, is read as UTC. PostgreSQL used to fail with a `500` on every such value
  but one with `Z` on a `DateTimeOffset` member, and on every value for a `DateTime` member.
- A filter value that its member's type cannot hold, such as `next week` for a date and time or `first` for a
  number, fails with `InvalidFilterCollectionResultException` before the query runs, which the base controllers
  return as `400`, also behind a collection key such as `tasks{any}.dueAt`. It used to fail when the query ran, as a
  `500`. The message names the field, and for a date and time the form `2026-11-01T09:00:00Z`.

| Breaking change | Required action |
|---|---|
| On SQL Server a value without a zone, and every value for a `DateTime` member, used to be read in the host's time zone; it is now read as UTC. On a host that runs in UTC nothing changes. | Send `Z` or an offset when a filter means another zone than UTC. Keep `DateTime` columns in UTC, as the providers' guidance asks. |
| `CollectionProcessingService.GetExpressionConstant` converts the value when it builds the constant and throws `InvalidFilterCollectionResultException` for a value the type cannot hold. | A subclass that calls it handles that exception as an invalid filter. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: alerts (FB87)

- An alert warns people about an object without a proposal: nothing is approved and nothing changes. Kinds are code:
  `NhAssistantBuilder.AddAlertKind(id, kind => ...)` with a display name key, an `NhAssistantAlertUrgency`, a title
  and text written from named parameters per culture, recipients (`NhAssistantAlertRecipients.SubjectResponsible`,
  `Explicit` and `SubjectApprovers`, combined with `Or`), an optional `RepeatAfter`, the default delivery
  (`NhAssistantAlertDelivery`: immediately, one daily digest with `WorkdaysOnly`, `ImmediatelyFrom` an urgency), what
  a person may change (`NhAssistantAlertChoices`) or `Required()`, and `PushShows` (`TitleOnly` by default). The
  run maintenance delivers them, and every person chooses per kind in their notification preferences.
- An agent lists the kinds its rules may raise with `NhAssistantAgentRunBuilder.Alerts(...)`, which startup checks.
  Rules raise them with `NhAssistantRuleResult.WithAlerts` and resolve them with `ResolveAlerts`; a kind the agent does
  not list fails the run with `assistant-alert-kind-not-listed`. A paused or stopped agent raises nothing and a shadow
  agent or run records shadow alerts that reach nobody. The application's code raises and resolves alerts without an
  agent through `INhAssistantAlerts`, with `TaskResult` and the codes of `NhAssistantAlertCodes`.
- One open alert exists per kind, subject and key: raising it again updates it in place as its next revision, and it
  reaches people again only when its urgency rises or after `RepeatAfter`, never a person who dismissed it until the
  key changes. Resolving or an expiry closes it and takes back what did not go out; the run maintenance purges closed
  alerts after `NhAssistantLimits.AlertRetention` (30 days). `MaxAlertsPerRun` and `MaxAlertsPerSubjectPerDay` bound
  them. Every recipient is checked before anything reaches them: the principal factory still rebuilds them for the
  subject and they have at least `Reader` on it; `INhAssistantAlertPolicy` (`UseAlertPolicy`) replaces the selectors.
- The content-free audit kinds `AgentAlertRaised` (127), `AgentAlertResolved` (128), `AgentAlertDismissed` (129) and
  `AgentAlertRecipientDropped` (130), with `NhAssistantAuditEvent.AlertId`, `AlertKind`, `AlertRevision` and
  `AlertRecipientCount`. Forgetting a user also removes their alert deliveries and their notification preferences.
- Delivery: the run maintenance claims the deliveries that are due, so several instances never send one twice, and
  checks every recipient again right before sending. An alert delivered immediately goes out on its own; every other
  alert of a person goes out in one daily digest per digest time, at the kind's time or the person's, on working days
  when the kind says so, in the person's time zone. The digest's title counts its alerts per urgency ("3 alerts, 1
  urgent", "3 attenties, 1 urgent") and it lists at most `NhAssistantLimits.MaxAlertDigestLines` (10) alerts with "and N
  more". `MaxAlertsPerPersonPerDay` (50) bounds what one person receives a day. A muted kind, the daily limit and a
  title that fails withhold a delivery (`NhAssistantAlertDeliveryStatuses.Withheld` with
  `NhAssistantAlertWithholdReasons`); the alert stays in the person's alert list.
- Channels: Web Push with the subscriptions and the one push switch of the assistant, showing an alert's title only
  unless its kind shows its text; the in-app notifications through `INhUserNotificationSink`, one thread per digest,
  linking to `NhAssistantOptions.Alerts.ListUrl`; and the application's own channels, `INhAssistantAlertChannel`
  registered with `AddAlertChannel<TChannel>()`, which receive an `NhAssistantAlertMessage`. A failing channel never
  stops the others; when every channel fails, the delivery is tried again later.
- Time zones (FB87-4): `NewHeap:AI:Assistant:Alerts:TimeZone` (or `ConfigureAlerts`), checked when the host starts,
  is the application's time zone for alerts; without it the zone of `UseTimeZone` applies. `INhAssistantPersonTimeZones`
  (`UsePersonTimeZones<T>()`) gives a person's own zone from the application's profiles.
- One preference model (FB87-6): `AssistantNotificationPreference` keeps a person's choices per kind: muted, digest time
  and the channels Web Push and in-app. `GET notifications` gains `timeZone` and `alertKinds`; `PUT notifications`
  takes `pushEnabled` optionally and `alertKinds` with `muted`, `digestTime` and `channels`, and refuses an unknown kind
  or a change the kind does not allow with `400` and `assistant-notification-kind-unknown`,
  `assistant-notification-mute-not-allowed`, `assistant-notification-digest-time-not-allowed`,
  `assistant-notification-channels-not-allowed` or `assistant-notification-digest-time-invalid`.

| Breaking change | Required action |
|---|---|
| The `Alerts` migrations add the tables `AssistantAlert` and `AssistantAlertDelivery` on SQL Server and PostgreSQL. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. An application without alert kinds sees no other change. |
| The `NotificationPreferences` migrations add the table `AssistantNotificationPreference` on SQL Server and PostgreSQL. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. |
| `PUT notifications` takes `pushEnabled` optionally and `GET notifications` returns `timeZone` and `alertKinds` as well. | None: a request with `pushEnabled` alone works as before, and a request without `pushEnabled` and `alertKinds` is still refused with `400`. |
| A delivery of an alert can end as `withheld`, a status of `NhAssistantAlertDeliveryStatuses` that is new. | Code that switches over the statuses handles or ignores `withheld`. |
| An audit sink that switches over every `NhAssistantAuditEventKind` sees four new kinds. | Handle or ignore `AgentAlertRaised`, `AgentAlertResolved`, `AgentAlertDismissed` and `AgentAlertRecipientDropped`. |

## @newheap/platform-ai-chat: notifications of alerts (FB87)

- The bundled push worker shows the notifications of alerts, `alert` and `alert-digest`, which belong to no
  conversation, and a click opens the host's alert list: in an open window through the new
  `NhAssistantPushService.openAlerts$` (an `NhAssistantAlertNotificationClick` with the alert or digest id), or at the
  new `push.alertsUrl` of `provideNhAssistant`, which defaults to `openUrl`. The texts of the new error codes of the
  notification preferences are in English and Dutch.

| Breaking change | Required action |
|---|---|
| The push worker's registration URL gains the `alerts` parameter, so browsers update the registered worker once. | Copy the new `nh-assistant-push-worker.js` into the application's assets. |
