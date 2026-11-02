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

## NewHeap.Platform.AI.Chat.AspNet: a repeated proposal for a person while the first is recorded

- `INhAssistantProposals.ProposeForAsync` with an `IdempotencyKey` returned `AlreadyOpen` instead of the repeat when
  another call with the same key recorded the proposal while this one was still checking it: it found that proposal
  as an open duplicate and audited `AgentProposalDeduplicated`. It now returns the proposal with `Repeated`, as a
  later call does, without an audit. One proposal was recorded either way.

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
- Recipients by role (FB87 phase 2): `NhAssistantAlertRecipients.SubjectMembers(minRole)` chooses the subject's members
  with at least that role (`NhAssistantAlertRecipientSelector.SubjectMembers`, `MinimumMemberRole`), combinable with
  `Or`. For a kind that selects only members the check at delivery and at reading asks that role too, so a member who
  dropped below it gets nothing more (`NhAssistantAlertDropReasons.BelowMinimumRole`); combined with another selector
  or under an alert policy the check stays at `Reader`.
- Resolving by omission: a kind with `ResolveWhenNotRaised()` lets an agent's rules that finish a run on a subject
  resolve the agent's open alerts of the kind about that subject whose key they did not raise again, audited as
  `AgentAlertResolved` with `not-raised` (`NhAssistantAlertCodes.NotRaised`). Only the run's own subject: a run on the
  whole application (`nh-system`) resolves explicitly, a run whose rules fail, hand over or are cancelled resolves
  nothing, and a shadow run touches only shadow alerts.
- Revalidation (FB87-10): a kind declares its check with `Revalidate<TCheck>()` (`INhAssistantAlertCheck`,
  `NhAssistantAlertCheckContext`, `NhAssistantAlertValidity`), and the application calls
  `INhAssistantAlerts.RevalidateAsync(subject)` after its change committed; nothing revalidates by itself. It checks at
  most `NhAssistantAlertLimits.MaxRevalidatedAlerts` open alerts of kinds with a check, resolves those whose check says
  so, audited with `revalidated` (`NhAssistantAlertCodes.Revalidated`), and returns an
  `NhAssistantAlertRevalidationResult` with the `NhAssistantResolvedAlert`s. A check that fails leaves its alert open,
  is logged without content and stops no other.
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
- A person's side: `GET alerts` lists the caller's own alerts, the most recently raised first or with `grouped=true` per
  parent, paged as the inbox pages, with title and text in the caller's language and the state `unread`, `read`,
  `resolved`, `expired` or `dismissed` (`NhAssistantAlertListStatuses`), filtered on status (also `open` and `closed`),
  kind, subject and urgency (`NhAssistantAlertUrgencyNames`). `GET alerts/summary` counts the unread ones in total and
  per urgency for a badge; an alert withheld from the caller's channels is listed as unread with `isWithheld` and its
  `withheldReason`, and one of a muted kind is not counted. `POST alerts/{id}/read` and `POST alerts/{id}/dismiss` are
  idempotent; a read delivery that
  waits for a digest is not sent, and the first dismissal is audited as `AgentAlertDismissed`, with
  `ImpersonationOriginActorId` in an impersonation session. Every alert passes the recipient check again with the
  caller's tenant: an alert of another person, or about a subject the caller lost access to, is not listed or counted
  and its id answers `404 assistant-alert-not-found` (`NhAssistantAlertCodes.NotFound`); a malformed filter answers
  `400 assistant-alert-filter-invalid` (`NhAssistantAlertCodes.FilterInvalid`). The responses are
  `NhAssistantAlertListDto`, `NhAssistantAlertDto` and `NhAssistantAlertSummaryDto` of
  `NhAssistantAlertJsonSerializerContext`.

| Breaking change | Required action |
|---|---|
| The `Alerts` migrations add the tables `AssistantAlert` and `AssistantAlertDelivery` on SQL Server and PostgreSQL. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. An application without alert kinds sees no other change. |
| The `NotificationPreferences` migrations add the table `AssistantNotificationPreference` on SQL Server and PostgreSQL. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. |
| `PUT notifications` takes `pushEnabled` optionally and `GET notifications` returns `timeZone` and `alertKinds` as well. | None: a request with `pushEnabled` alone works as before, and a request without `pushEnabled` and `alertKinds` is still refused with `400`. |
| A delivery of an alert can end as `withheld`, a status of `NhAssistantAlertDeliveryStatuses` that is new. | Code that switches over the statuses handles or ignores `withheld`. |
| An audit sink that switches over every `NhAssistantAuditEventKind` sees four new kinds. | Handle or ignore `AgentAlertRaised`, `AgentAlertResolved`, `AgentAlertDismissed` and `AgentAlertRecipientDropped`. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: the administration of alert kinds (FB87 phase 2)

- A kind's code declares what an administrator may change with `NhAssistantAlertKindBuilder.AdministratorMayChange`
  (`NhAssistantAlertAdminChoices`: `SwitchOff`, and for the default delivery `DigestTimes`, `WorkdaysOnly`,
  `ImmediatelyFrom` and `QuietHours`; `All` by default, `NhAssistantAlertKind.AdministratorChoices`). `Required()` takes
  `SwitchOff` away: a required kind is never off.
- `GET admin/alert-kinds` (admin policy) lists every kind with its bounds, its code's delivery, the administrator's
  settings with their version, what applies now, and per period (30 days by default, at most 366) the numbers of its
  alerts: raised, delivered, read, dismissed, resolved before reading, withheld and muted, and per agent the shadow
  alerts it would have sent (`NhAssistantAdminAlertKindsDto`, `NhAssistantAdminAlertKindDto` and the DTOs beside them).
- `PUT admin/alert-kinds/{id}` (`NhAssistantAdminAlertKindRequest`) saves the complete settings within the bounds with
  `expectedVersion`: whether the kind is on and its default delivery, immediately or one to four daily digest times
  with working days, the urgency from which it is immediate and quiet hours. A change the code does not allow answers
  `400` with `assistant-alert-kind-switch-off-not-allowed` or `assistant-alert-kind-delivery-not-allowed`, a malformed
  delivery `400 assistant-alert-kind-delivery-invalid`, an unknown kind `404 assistant-alert-kind-unknown` and a stale
  version `409 assistant-version-conflict`. Every change is audited as `AdminAlertKindChanged` (131) with
  `NhAssistantAuditEvent.ChangedFields`, the names of the settings that changed, never their values.
- What applies is one effective delivery per kind, the code's merged with the administrator's settings; the digest
  scheduler, the delivery and `GET`/`PUT notifications` read it, and a person's choices apply on top. Of several
  digest times the scheduler plans the first of the day; quiet hours are stored and validated for the scheduler that
  plans them.
- A kind that is off raises nothing: `INhAssistantAlerts.RaiseAsync` and a rule's alert are refused with
  `assistant-alert-kind-off` (`NhAssistantAlertCodes.KindOff`) and the run goes on. Its open alerts still resolve and
  expire, a delivery of it that still waits is withheld as `kind-off` (`NhAssistantAlertWithholdReasons.KindOff`), which
  the badge does not count, and a person's preferences leave the kind out.

| Breaking change | Required action |
|---|---|
| The `AlertKindSettings` migrations add the table `AssistantAlertKindSetting` on SQL Server and PostgreSQL, which a host with alert kinds reads when it raises and delivers alerts. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. |
| An audit sink that switches over every `NhAssistantAuditEventKind` sees `AdminAlertKindChanged`. | Handle or ignore `AdminAlertKindChanged`. |
| A withheld delivery can carry the reason `kind-off`, and raising an alert can fail with `assistant-alert-kind-off`. | Code that switches over `NhAssistantAlertWithholdReasons` or the alert codes handles or ignores them. |

## @newheap/platform-ai-chat: alerts (FB87)

- `nh-assistant-alerts` (`NhAssistantAlertsComponent`) shows a person's own alerts, grouped by parent by default, with
  `density: 'compact'`, a status filter, read and dismiss in place, resolved and expired alerts shown as such, and an
  alert of a muted kind labelled "Muted" (`isWithheld`, `withheldReason`). It
  emits `openSubject` (`AlertSubjectTarget`) with the subject and its link for the host to route, and never navigates.
- `NhAssistantAlertBadgeService` gives the shell the unread counts (`unread`, `unreadByUrgency`, `highestUrgency`):
  `watch()` keeps them current on load, on focus and visibility, every five minutes while the page is visible and after
  the person's own actions, and its release forgets them.
- `nh-assistant-notification-preferences` (`NhAssistantNotificationPreferencesComponent`) shows the push switch of
  `NhAssistantPushService`, the person's time zone and per alert kind whether they receive it, its digest time and its
  channels where the kind allows it. `NotificationSettings` gains the optional `timeZone` and `alertKinds`
  (`NotificationAlertKind`); a server without alerts sends neither.
- `NhAssistantApiService` gains `getAlerts`, `getAlertSummary`, `markAlertRead`, `dismissAlert` and
  `updateNotificationPreferences` (`UpdateNotificationSettingsRequest`), with the models `Alert`, `AlertList`,
  `AlertSummary` and their filters. `provideNhAssistantMockApi` serves them from the scenario's `alerts`, and
  `NhAssistantMockBackend.raiseAlert`, `resolveAlert` and `setAlertKind` change them while a test runs.
- The texts are in English and Dutch ("Attenties") under `nh-assistant.alerts` and `nh-assistant.notification-preferences`.
- The bundled push worker shows the notifications of alerts, `alert` and `alert-digest`, which belong to no
  conversation, and a click opens the host's alert list: in an open window through the new
  `NhAssistantPushService.openAlerts$` (an `NhAssistantAlertNotificationClick` with the alert or digest id), or at the
  new `push.alertsUrl` of `provideNhAssistant`, which defaults to `openUrl`. The texts of the new error codes of the
  notification preferences are in English and Dutch.

| Breaking change | Required action |
|---|---|
| `PUT notifications` in the mock API changes the push switch only when the body names `pushEnabled`. | Send `pushEnabled` to change it in a test, as the server requires. |
| The push worker's registration URL gains the `alerts` parameter, so browsers update the registered worker once. | Copy the new `nh-assistant-push-worker.js` into the application's assets. |

## @newheap/platform-ai-chat: the administration of alert kinds (FB87 phase 2)

- `nh-assistant-admin` gains an Alerts section ("Attenties"), the tab `alert-kinds`
  (`NhAssistantAdminAlertKindsComponent` of `@newheap/platform-ai-chat/admin`): every alert kind with its numbers in a
  period, the shadow alerts each agent would have sent, a switch off and its default delivery editable where its code
  allows it, with a reset to the code's delivery.
- `NhAssistantAdminApiService` gains `getAlertKinds` and `updateAlertKind`, with the models `AdminAlertKinds`,
  `AdminAlertKind`, `AdminAlertDelivery`, `AdminAlertKindChoices`, `AdminAlertKindSettings`, `AdminAlertKindNumbers`,
  `AdminAlertShadowAgent` and `AdminAlertKindChange`; `provideNhAssistantMockApi` serves and checks them, from the
  scenario's `admin.alertKinds` or an example view. The texts and the new error codes are in English and Dutch.

| Breaking change | Required action |
|---|---|
| `NhAssistantAdminTab` gains `alert-kinds`. | Code that switches over the tabs handles or ignores it. |
