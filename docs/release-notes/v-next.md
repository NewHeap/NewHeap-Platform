# v-next

## @newheap/platform-common: the notification bell reads its overview once per change

- A bell or list built on `NhUserNotificationsAbstractComponent` reads its shown pages again when the polled
  overview changes, as before, but no longer reads the overview a second time right after the poll that brought
  the change. `reload()` takes an optional `refreshOverview` (default `true`), so a call from an application
  still refreshes the unread count.

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

## @newheap/platform-ai-chat: time and the person for alerts (FB87 phase 2)

- `nh-assistant-notification-preferences` edits up to four digest times of a kind and the person's own quiet hours
  where the kind allows them, shows the kind's quiet hours otherwise and whether urgent alerts pass them, and lists the
  objects the person muted (`subjectMutes`), which a save lifts (`liftSubjectMutes`). A server that sends no
  `defaultDigestTimes` gets the single `digestTime` as before.
- `nh-assistant-alerts` offers, where `mayMuteSubject` allows it, to dismiss an alert and hear nothing more of its kind
  about its object for 7 or 30 days, and labels what was withheld for that "Muted for a while";
  `NhAssistantApiService.dismissAlert(id, { muteSubjectForDays })` (`AlertDismissOptions`) sends it.
- The models gain `NotificationQuietHours`, `NotificationSubjectMute`, the new fields of `NotificationAlertKind`,
  `NotificationAlertKindChange` and `NotificationSettings`, `Alert.mayMuteSubject` and
  `NH_ASSISTANT_ALERT_MAX_SUBJECT_MUTE_DAYS`; the mock serves and checks them, withholds an alert about a muted object
  and takes the scenario's `alerts.subjectMutes` and `collaboration.push`.
- A page with the alert list and the notification choices asked `GET notifications` three times (FB87-13): the
  assistant's start activated push, the choices read the settings themselves and then activated push again, and the
  store's first account reset dropped what the page had read. `NhAssistantPushService` now holds the person's settings
  for the page: `loadSettings()` shares one request in flight, activation reuses loaded settings and is shared,
  `updatePreferences(request)` keeps `settings` current, and the store forgets push only after it activated push for a
  person. The choices read and save through it and show settings another part of the page changed, such as a mute set
  on the list.
- The texts are in English and Dutch.

| Breaking change | Required action |
|---|---|
| `NhAssistantPushService.activate(true)` reuses the settings loaded for the signed-in person and shares a running activation instead of reading `GET notifications` on every call, and cancels this browser's subscription when the person turned notifications off. The store no longer deactivates push at its first account activation. | None for a host that calls `activate` as the assistant does; a host that relied on `activate` to read the settings again calls `loadSettings()`. |
| `PUT notifications` in the mock API answers `400 assistant-validation` for a body without `pushEnabled`, `alertKinds` and `liftSubjectMutes`, as the server does. | Name what the test changes. |

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

## @newheap/platform-ai-chat: the model tool of alerts (FB87 phase 2)

- The agent's Rollout tab in `nh-assistant-admin` gains the section "Alerts from the model" ("Attenties van het
  taalmodel") for an agent that lists alert kinds whose code lets a model raise them: per kind when it applies, the
  parameters the model fills and its switch, which goes to shadow first and only from shadow to people, explaining each
  step and taking a note before it applies. An agent without such a kind shows no section.
- `NhAssistantAdminApiService` gains `getAgentAlertModel`, `setAgentAlertModel` and `getAgentAlertModelVersions`, with
  the models `AgentAlertModel`, `AlertModelSwitch`, `AlertModelParameter`, `AlertModelRequest`, `AlertModelVersion` and
  `AlertModelMode`; `provideNhAssistantMockApi` serves and checks them from the scenario's `admin.alertModel`.
- `Alert` gains the optional `raisedByModel`, and `nh-assistant-alerts` labels such an alert "Raised by AI" ("Door AI"),
  with its title and text as plain text. The texts and the new error codes are in English and Dutch.
