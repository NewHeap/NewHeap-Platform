# Platform Feedback on 31.0.0 Plan

A consuming application wants an agent to warn people before something goes wrong, numbered FB87 in its own plan.
Its examples are "your task is due today and not reported done", and "a document of a project has its deadline in
three days, and you prepare the work". These warnings are no proposals: there is nothing to approve and nothing
changes. Today NewHeap notifies only about proposals and runs:
- the kinds of `NhAssistantAgentNotificationKind` are about waiting proposals, reminders, escalations, uncertain
  executions, failed runs and consultations;
- the recipients are a subject's approvers;
- a person can only switch all push on or off (`AssistantNotificationSetting.PushEnabled`);
- an application message notifies the participants of one conversation, not the person responsible for an
  object.

The finding was checked against `main` at 26e76d56. It holds. This plan records the choices made on the
application's suggestion, which proposed the module below. It is a plan, not usage documentation: once it is built,
the guidance rules, the release notes and the sample cases are authoritative.

## Status

Decided by Lars on 2026-11-02, one by one. As advised:
- the direction: a module in two phases;
- the same key updates its alert;
- the time zone: the application's zone with a hook per person;
- the name: alerts;
- one preference model;
- one train with three lanes.

Not as advised: the model tool comes in phase 2, with templates, instead of not at all.

Phase 1 was built on 2026-11-02 in train 34 and verified together. Lane AK built the core, then lanes AB (delivery)
and AU (API and UI) built on it in parallel. The verification found two things, fixed in the train:
- AU had removed two public members of `nh-assistant-preferences`; they are back as deprecated delegates.
- The SPM-392 test of AU counted the alerts that AB's test raises in the same host; it now reads only its own
  project's alerts.

The choices each lane made are in the "Lane choices" table below (AK1–AK10, AB1–AB12, AU1–AU10), all for Lars to
review.

Phase 2 was decided by Lars on 2026-11-02, one by one, all as advised (FB87-8 to FB87-13 below). It is not built yet.

## Decisions

| # | Decision |
|---|---|
| FB87-1 | **A module for alerts, in two phases.** Phase 1 has five parts. **Kinds and raising:** kinds in code; alerts from rules and from application code. **Recipients:** the responsible person, explicit people and the approvers, plus a policy of the application, checked live at delivery and at reading. **Lifecycle:** deduplication, resolving and expiry. **Delivery:** immediately and one daily digest, through Web Push, the in-app notifications and channels of the application. **People and API:** reading, dismissing and muting per kind, with the list, a badge and the preferences. Phase 2 follows later. |
| FB87-2 | **The model raises alerts only in phase 2, and only through a template**: a platform tool, offered only to an agent that lists alert kinds, chooses a kind and fills its template's parameters. The parameters are marked untrusted and fall under the noise limits. Phase 1 has no model tool. |
| FB87-3 | **The same key updates its alert in place.** It stays one alert, and the new content replaces the old. It is delivered again only when its urgency rises or after the kind's `RepeatAfter`. |
| FB87-4 | **Digests run in the application's time zone, with a hook per person.** The zone comes from a configured default, and an optional interface gives a person's time zone. Working days count in that zone. |
| FB87-5 | **The public name is alerts**, in Dutch "Attenties". Examples: `NhAssistantAlert`, `AddAlertKind`, `WithAlerts`, `GET alerts` and `nh-assistant-alerts`. |
| FB87-6 | **One preference model for every notification of the assistant**: one store and one component. In phase 1 it governs the alert kinds per kind. The existing push switch for proposals shows in the same component and keeps working as it does. |
| FB87-7 | **One train with three lanes**: AK (the core), then AB (delivery) and AU (API and UI) in parallel. |
| FB87-8 | **Phase 2 entirely, in one train (35) with four lanes**: AD (administration), AR (recipients and lifecycle), AT (time and the person) and AM (the model tool). |
| FB87-9 | **The model tool needs an opt-in in code and a rollout per agent**: a kind allows it with typed parameters without links; an administrator switches it on per agent, first in shadow (recorded, not delivered), then live, as running without approval does (FB82). |
| FB87-10 | **Revalidation is an explicit call**: the application calls `INhAssistantAlerts.RevalidateAsync(subject)`, and a kind declares its check in code, as proposals are revalidated; no event bridge. |
| FB87-11 | **"Do not tell me about this object for N days" mutes only that kind** on that object; a `Required` kind cannot be muted. |
| FB87-12 | **The built-in notification kinds do not join the preference model yet**; they keep the push switch. The model's category lets them join later without a migration. |
| FB87-13 | **The duplicate requests of the alert page**, found in train 34's browser check, are fixed in lane AT. |

## Rules for all of it

- **Compatibility:**
  - Everything is opt-in. An application without alert kinds sees no change.
  - A breaking change gets a `Breaking change | Required action` row.
- **Repository rules:**
  - code and migrations for SQL Server and PostgreSQL;
  - tests with authorization negatives;
  - a release-notes section per release unit;
  - guidance and consumer guide updates;
  - a new executable sample case, SPM-392;
  - the public API snapshot;
  - English and Dutch texts and the Angular lifecycle extension point rules for the components.
- **Alerts and data:**
  - An alert never changes data, is never a proposal and never grants anything.
  - Its kinds, recipients and limits are code. Only delivery choices may become data.
  - It carries no content outside its own store: the audit is content-free, and a push shows only the title unless
    the kind says otherwise.
- Nothing names the consuming application.

## Choices

Each choice lists the options that were weighed. "(advice)" marks the recommendation.

### FB87. Alerts

#### What exists

- **Delivery:**
  - Web Push;
  - in-app delivery through `INhUserNotificationSink`, the platform notifications with `GroupKey`, read state and
    archive;
  - the notification policy `INhAssistantAgentNotificationPolicy`;
  - delivery channels through `INhAssistantAgentNotificationChannel`.
- **Runs:**
  - rule handlers without a model (`NhAssistantRuleResult`);
  - schedules with their own time zone;
  - shadow runs and shadow agents.
- **Subjects:**
  - subject access with roles;
  - `NhAiSubjectInfo.ResponsibleActorId`;
  - the principal factory that rebuilds an actor's rights for background work.
- **What is missing:**
  - an alert without a proposal;
  - deduplication;
  - a digest;
  - preferences per kind;
  - a time zone per person (`NhActorInfo` carries only a display name).

#### Direction (FB87-1)

- **(a) A module for alerts in two phases (advice, decided).** Phase 1 is listed under "Phase 1" below.
  - Phase 2:
    - delivery as data in the administration, within the bounds of the code;
    - more digest times and quiet hours;
    - `ResolveWhenNotRaised` and revalidation on events;
    - the subject's members by minimum role as recipients;
    - "do not tell me about this object for N days";
    - numbers per kind (sent, opened, dismissed, resolved before reading);
    - the model tool of FB87-2.
- **(a) all at once.** One large train with four tables, two components and an administration section.
- **(b) Only a notification kind `AgentNotice`**, with recipients from the notification policy. Without
  deduplication, a morning round sends the same warning every day.
- **(c) Nothing in NewHeap.** The application builds its own job and push and duplicates the access checks, and a
  person receives notifications from two systems.

#### Phase 1

1. **Kinds in code.**
   - `assistant.AddAlertKind(id, kind => …)` takes these settings:
     - a display name key and an urgency (`Info`, `Warning`, `Urgent`);
     - a localized text built from named parameters for each culture;
     - the recipients, deduplication with an optional `RepeatAfter`, and the default delivery: immediately, one
       daily digest time with `WorkdaysOnly`, or immediately from an urgency;
     - what a person may change (mute, digest time, channels), or `Required`;
     - `PushShows` (`TitleOnly` by default).
   - An agent lists the kinds it may raise with `.Alerts(...)`. A rule that raises another kind fails with a clear
     code.
2. **Raising.**
   - **From a rule:** `NhAssistantRuleResult.Done(...).WithAlerts(...)` and `ResolveAlerts(...)`.
   - **From application code without an agent:** `INhAssistantAlerts.RaiseAsync` and `ResolveAsync`.
   - **What an alert carries:**
     - its kind and subject;
     - optional explicit recipients;
     - a key;
     - bounded string parameters;
     - an optional expiry;
     - a link to its subject, which the host routes as it routes `openInbox`.
   - **Agent state:**
     - A paused or stopped agent raises nothing.
     - A shadow agent or a shadow run records its alerts as shadow and delivers none.
3. **Recipients.**
   - **Selectors:** `SubjectResponsible`, `Explicit` and `SubjectApprovers`, combined with `Or`.
   - **Override:** `INhAssistantAlertPolicy.SelectRecipientsAsync` replaces the selectors, as the notification
     policy does.
   - **At every delivery each recipient is checked again:**
     - the actor still exists;
     - the principal factory still builds them;
     - they still have at least Reader on the subject, in the subject's division or tenant.
   - **At every read:** `GET alerts` filters by the same access, so a person who lost access no longer sees the
     stored text.
   - A recipient who no longer qualifies is left out, with a content-free audit.
4. **Deduplication, resolving and expiry.**
   - **Deduplication:**
     - One open alert exists per kind, subject and key.
     - Raising the same key again updates it in place (FB87-3).
     - It is delivered again only when its urgency rises or after `RepeatAfter`.
     - A person who dismissed it does not get it again until the key changes.
   - **Resolving and expiry:**
     - A resolved or expired alert leaves every digest that has not gone out yet.
     - A delivered one shows as resolved or expired.
   - **Retention:** closed alerts are purged after a bounded retention, 30 days by default. Their parameters are
     the application's data.
5. **Delivery.**
   - **Moment:**
     - immediately, or one daily digest at the kind's time or the person's chosen time;
     - working days only, when the kind says so;
     - in the person's time zone (FB87-4).
   - **Channels:**
     - **Web Push**, showing the title only by default.
     - **The in-app notifications**, as one thread per digest, with its own `GroupKey`.
     - **`INhAssistantAlertChannel`**, for channels of the application such as e-mail.
     - **The alert list**, always.
   - **A digest:**
     - one notification per person per digest time;
     - its title counts per urgency ("3 alerts, 1 urgent");
     - it opens the list.
   - **Noise limits** in `NhAssistantLimits`:
     - at most so many alerts per run, per subject per day and per person per day;
     - a digest shows at most so many lines, with "and N more".
   - Deliveries are claimed, so several instances never send twice.
6. **A person.**
   - A person can mark an alert read and dismiss it.
   - A person can mute a kind unless it is `Required`, and choose a digest time and channels where the kind allows
     it. These choices live in the one preference model (FB87-6).
7. **API.**
   - `GET alerts`:
     - lists the person's own alerts;
     - filters on status, kind, subject and urgency;
     - groups by parent subject as the inbox does.
   - `GET alerts/summary` gives the badge counts per urgency.
   - `POST alerts/{id}/read` and `POST alerts/{id}/dismiss` mark one alert.
   - `GET/PUT notifications` gains the preferences per kind. The existing push switch stays where it is.
8. **UI in `@newheap/platform-ai-chat`.**
   - `nh-assistant-alerts` shows the list. It supports `density: 'compact'` and has an `openSubject` output.
   - `nh-assistant-notification-preferences` shows one person's choices.
   - The host gets a badge API.
   - All texts in English and Dutch.
9. **Governance.**
   - **Audit:** the content-free kinds `AgentAlertRaised`, `AgentAlertResolved`, `AgentAlertDismissed` and
     `AgentAlertRecipientDropped` (127–130). The delivery table records each delivery itself.
   - **Migrations** on both providers:
     - `AssistantAlert`;
     - `AssistantAlertDelivery`, one row per recipient with status, claim and digest id;
     - `AssistantNotificationPreference`, per actor and kind.
   - **Sample:** SPM-392 shows a rule that alerts the responsible person about a task due today. It proves
     deduplication over two rounds, resolving, a digest at 07:30 in another time zone, and the access checks.

#### The model tool (FB87-2)

- **(a) Not at all.** Only rules and application code raise alerts, so a document with an injection cannot make an
  agent warn people.
- **(b) In phase 2, through a template (decided).** The model chooses a listed kind and fills its template's
  parameters, which are marked untrusted and fall under the noise limits. Free text of the model is never an alert.

#### The same key with other content (FB87-3)

- **(a) Update in place (advice, decided).** "Two days left" replaces "three days left" in the same alert. It is
  delivered again only on a higher urgency or after `RepeatAfter`.
- **(b) A new alert**, as the suggestion proposed. A count that changes every day then warns every day.

#### The time zone of a digest (FB87-4)

- **(a) The application's zone plus a hook per person (advice, decided).** A configured default time zone, and an
  optional interface that returns a person's zone, which an application connects to its profiles.
- **(b) Only the application's zone.** A digest then arrives at an odd hour for a person elsewhere.

#### The name (FB87-5)

- **(a) alerts, in Dutch "Attenties" (advice, decided).** "An alert" is plain English and neutral enough for `Info`.
- **(b) attentions**, as suggested. A direct translation, but "an attention" is not idiomatic English.
- **(c) notices.** It collides with the notice that a question waits.

#### Preferences (FB87-6)

- **(a) One model for every notification of the assistant, alerts first (advice, decided).** A person has one place
  for their choices. The built-in kinds may join it later.
- **(b) A model of its own for alerts**, next to `PushEnabled`. A person then has two places.

#### Train (FB87-7)

- **(a) One train with three lanes (advice, decided).**
  - **AK (the core):**
    - kinds, raising from rules and code, and recipients with the live checks;
    - deduplication, resolving, expiry and retention;
    - shadow and the audit;
    - the migrations for `AssistantAlert` and `AssistantAlertDelivery`;
    - the store.
  - **AB (delivery):**
    - immediate delivery and the daily digest with claims;
    - time zones and working days, and the noise limits;
    - Web Push, in-app delivery and `INhAssistantAlertChannel`;
    - the preferences store with its migration.
  - **AU (API and UI):**
    - the endpoints with authorization negatives;
    - the list, the badge and the preferences component, with the API service and the mock.

  AB and AU start after AK, from its contracts. AB merges before AU, because AU's preferences endpoints read AB's
  store. Each lane extends SPM-392 and the guidance for its own part. The full matrix runs once.
- **(b) Two trains**, first the core and delivery, then the API and UI. Each train is smaller, but the whole takes
  longer.

### FB87 phase 2

#### Scope (FB87-8)

- **(a) Everything in one train with four lanes (advice, decided).**
  - **AD (administration):**
    - `GET admin/alert-kinds`: every kind with the bounds its code sets, the administrator's settings, and per
      kind over a period the numbers raised, delivered, read, dismissed, resolved before reading and withheld;
      for shadow alerts, "would have sent" per agent.
    - `PUT admin/alert-kinds/{id}`: switch a kind off (not a `Required` one) and change its default delivery
      (digest times, working days, immediately from an urgency, quiet hours) only where its code allows it.
    - The table `AssistantAlertKindSetting` with the migration `AlertKindSettings`, the audit
      `AdminAlertKindChanged` (131), and an "Alerts" section in `nh-assistant-admin`.
  - **AR (recipients and lifecycle):**
    - `NhAssistantAlertRecipients.SubjectMembers(minRole)`, checked live as the other recipients are.
    - `ResolveWhenNotRaised()` on a kind: a rule run on a subject resolves the agent's open alerts of that kind
      on that subject that the run did not raise; a run on the whole application resolves explicitly.
    - Revalidation (FB87-10).
    - No migration is expected.
  - **AT (time and the person):**
    - More digest times per day (at most four) and quiet hours, with defaults from the code and the
      administrator, changeable by the person where the kind allows it. Delivery that would go during quiet hours
      moves to the next digest; `Urgent` passes only when the kind says so.
    - "Do not tell me about this object for N days" on dismissing (FB87-11).
    - The preference store's extension with its migration, after AD's.
    - The duplicate requests of the alert page (FB87-13), with a spec that pins the number of requests.
  - **AM (the model tool):** the platform tool of FB87-2 with the rollout of FB87-9: the switch per agent and kind
    with its migration after AT's, the audit `AdminAlertModelSwitchChanged` (132), and the switch on the agent's
    Rollout tab. The platform derives the key from the kind, the subject and the parameters the kind names.
  - AD and AR start first. AT and AM start once AD is merged, because they read its settings; AT merges before
    AM. Reserved: audit 131–134, SPM-392 extended (SPM-393 if a lane needs a case of its own).
- **(b) Without the model tool**, which would follow in a train of its own.
- **(c) Two trains**: first AD and AT, then AR and AM.

#### The model tool's rollout (FB87-9)

- **(a) Opt-in in code plus shadow per agent (advice, decided).** An administrator sees what the model would send
  before anyone receives it.
- **(b) Only the opt-in in code**: a kind that allows it and an agent that lists it are live at once.

#### Revalidation (FB87-10)

- **(a) An explicit call (advice, decided)**, as proposals are revalidated, in line with keeping triggers explicit.
- **(b) Bound to events** (`AddAlertRevalidation<TEvent>(check)`), as suggested: less code in the application, but
  an implicit coupling.

#### Muting an object (FB87-11)

- **(a) Only that kind (advice, decided)**: a person still hears about another kind on the same object.
- **(b) Every kind on the object**: a person may then miss another, possibly more important warning.

#### The built-in kinds (FB87-12)

- **(a) Not yet (advice, decided)**: approvers cannot mute proposals away, and the category lets them join later.
- **(b) Now**: one place for everything, but which built-in kinds stay required would have to be decided too.

## Lane choices

| # | Choice | Status |
|---|---|---|
| AK1 | A rule result that raises or resolves a kind its agent does not list fails the run with `assistant-alert-kind-not-listed`, and none of that result's alerts are raised or resolved. Other refusals of one alert (a limit, a subject that is gone, a title that fails) are logged with their code and the run completes. | for Lars to review |
| AK2 | A paused or stopped agent (also `Enabled(false)` in code) raises nothing but still resolves: resolving only lowers noise. An agent that is switched off still raises in runs started by hand. | for Lars to review |
| AK3 | The sample's tasks have no due date or responsible person, so SPM-392 alerts about the project's deadline and its owner, whom the project's resolver names as responsible, and the members who take part; no sample migration. | for Lars to review |
| AK4 | "The actor still exists" is what the principal factory says: an unknown person, or one who may no longer sign in, is not rebuilt for the subject (`INhAssistantRecipientRules.IsResolvedForAsync`). A host whose factory cannot tell lets everyone pass, as the notifications do. The division and tenant come from the resolver's role on the subject and the principal rebuilt for the subject's division and tenant; a read in a request also passes the caller's tenant. | for Lars to review |
| AK5 | The identity of an open alert includes the subject's scope key and the shadow mark. One open alert per identity is held by the subject's transaction lock and a unique index over the identity hash and a closed marker (empty while open, the alert's id once closed), so neither provider needs a filtered index. | for Lars to review |
| AK6 | The limit per subject per day counts new alerts only, per UTC day of the application's clock; an update in place is no new alert. The limit per run counts the alerts of one rule result. Shadow alerts count apart. | for Lars to review |
| AK7 | Titles and texts are written when an alert is shown or delivered, from its stored parameters with the kind's functions in the reader's culture, and once when it is raised to refuse a title that fails. A kind removed from code leaves its stored alerts without a text. | for Lars to review |
| AK8 | A person chosen earlier and not named when the alert is raised again keeps their delivery, since raisers may name different people; only people chosen now who fail the check lose a waiting delivery. Recipients on an alert whose kind lacks `Explicit` are refused with `assistant-alert-recipients-not-selected`. | for Lars to review |
| AK9 | Expiry and purge run in the existing run maintenance only while the host registers alert kinds; a host that removes every kind stops purging old alerts until it adds one again. | for Lars to review |
| AK10 | The audit names the agent (`assistant-agent:{id}`) or `application` as the actor of raised and resolved alerts, and the person concerned for a dismissal or a dropped recipient, with the new fields `AlertId`, `AlertKind`, `AlertRevision` and `AlertRecipientCount`. | for Lars to review |
| AB1 | Deliveries go out on the next pass of the run maintenance, so "immediately" means within `MaintenanceInterval` (one minute by default). With the assistant disabled nothing goes out and the deliveries wait. A pass claims at most 10 batches of 100. | for Lars to review |
| AB2 | A digest is one notification per person per moment: every delivery of the person due at that moment, also of different kinds with the same digest time. Its title counts all alerts, then the urgent ones and the warnings ("5 alerts, 1 urgent, 2 warnings"); its lines are the alerts' titles, most urgent first, also on Web Push, where the titles are allowed under `TitleOnly`. A digest of one alert is still a digest. | for Lars to review |
| AB3 | A muted kind, a delivery over the daily limit, a title that fails and a kind removed from code end the delivery as the new status `withheld` with a reason (`muted`, `limit-reached`, `text-invalid`, `kind-unknown`), without an audit. The alert stays in the person's alert list, and raising it again plans the delivery again, so one over the limit goes out on a later day when it is raised again. | for Lars to review |
| AB4 | `MaxAlertsPerPersonPerDay` (50, 1 to 1,000) counts the alerts sent to a person per UTC day of the application's clock, as AK6 does for subjects; each alert of a digest counts and a required kind counts too. `MaxAlertDigestLines` is 10 (1 to 50). | for Lars to review |
| AB5 | Working days are Monday to Friday in the person's zone; phase 1 knows no holidays. A digest time the clock skips falls on the first minute after the gap, one it shows twice on its first pass. A changed digest time applies to deliveries planned after the change; a waiting delivery keeps its moment. | for Lars to review |
| AB6 | The application's time zone for alerts is `NewHeap:AI:Assistant:Alerts:TimeZone` (or `ConfigureAlerts`), checked at startup; without it the zone of `UseTimeZone` applies, UTC by default. `INhAssistantPersonTimeZones` returns a zone id; an unknown id or a lookup that throws falls back to the application's zone with a log line that does not name the person. | for Lars to review |
| AB7 | In-app alerts go out whenever the host has an `INhUserNotificationSink`, without `UseInAppAgentNotifications`, which stays about agent notifications; they link to `NewHeap:AI:Assistant:Alerts:ListUrl` and group per digest (`assistant-alert:digest:{id}`) or per alert (`assistant-alert:{id}`). A person's culture is the actor directory's, otherwise the process culture; Web Push uses it, otherwise the browser's language. | for Lars to review |
| AB8 | A channel with nothing to deliver to (no browser, a sink that delivers nothing) counts as skipped. When every channel that tried failed, the deliveries are released for two minutes, at most three attempts, and then recorded as sent without channels, since the alert list has them. An application channel has a dash-case `Id`, checked at startup and recorded on the delivery; a person chooses only Web Push and in-app, and the application's channels receive every notification that goes out, with the texts. | for Lars to review |
| AB9 | `AssistantNotificationPreference` is keyed by actor, category and kind, with the category `alert`, so the built-in notification kinds can join later without a migration. The effective channels are the person's choice or on; `pushEnabled` and `availableChannels` apply on top. `PUT notifications` accepts a value that repeats the kind's default even where the kind allows no choice, so a client can send back what it read; a kind named twice or more than 200 kinds is `assistant-validation`; a body without `pushEnabled` and `alertKinds` stays `400`. An impersonation session may change the person's choices, as it may the push switch. | for Lars to review |
| AB10 | The push payload of an alert is `{ type: 'nh-assistant', kind: 'alert', alertId, title, body, tag }`, of a digest `kind: 'alert-digest'` with `digestId`, without a conversation. The worker routes both to the alert list: `NhAssistantPushService.openAlerts$` in an open window, otherwise `push.alertsUrl` (default `openUrl`), passed to the worker as its `alerts` parameter. The sample leaves `ListUrl` and `alertsUrl` unset until lane AU's alert list page exists. | for Lars to review |
| AB11 | AK's store planned a delivery that was read before it went out again on the next raise without a delivery again; it now keeps it like a sent one. An alert whose expiry passed before the maintenance closed it is never sent. | for Lars to review |
| AB12 | The sample takes a person's time zone from the division they work in (`NhUser.ActiveDivision.TimeZoneId`), so it needs no sample migration, and demonstrates `INhAssistantAlertChannel` with an in-memory stand-in for e-mail (`SampleAlertOutbox`), since it sends no e-mail. | for Lars to review |
| AU1 | `{id}` of `POST alerts/{id}/read` and `/dismiss` is the alert's id: the endpoints change only the caller's own delivery of it, and every item carries the alert id and the delivery id. Both are idempotent and answer `200` with the alert as the list now shows it; reading a dismissed alert, or one that closed before it went out, changes nothing. A delivery that does not exist, was dropped, is of a kind no longer registered or about a subject the caller may no longer read answers `404 assistant-alert-not-found`; a malformed filter `400 assistant-alert-filter-invalid`. Both codes are in `NhAssistantAlertCodes` and the one status table. | for Lars to review |
| AU2 | A person sees one of five states per alert: `unread` and `read` while it is open, `resolved` and `expired` once it closed (an expiry that passed counts before the maintenance closed it), and `dismissed`, which wins. `status` filters on one of them or on `open`/`closed`; without it the list shows everything not dismissed. A delivery that waits for its digest is `unread` in the list at once. The badge counts `unread` only. | for Lars to review |
| AU3 | The recipient check needs the application's resolver, so it runs after the query: the store selects, filters and orders in SQL, the list reads the newest 500 matching deliveries, checks each subject once per request with the caller's tenant, and then groups and pages in memory; `total` counts within that window. Alerts of a kind the host no longer registers are left out; a title that fails is `title: null` and the list shows "An alert about {subject}". A host without alert kinds reads no table. | for Lars to review |
| AU4 | Dismissing also clears a closed alert whose delivery was taken back (a new conditional `DismissOnceAsync` beside AK's store), and only the dismissal that changed the row is audited, so a repeat or a race audits once. An impersonation session reads and dismisses as the person, as the assistant endpoints act for the person; the audit names the user who impersonates (`ImpersonationOriginActorId`, a new optional parameter of `AlertDismissedAsync`). | for Lars to review |
| AU5 | The badge uses no live signal: alerts come from rules and the application's code, which send the recipient no hub update, and agent-work updates go to subject members. `NhAssistantAlertBadgeService.watch()` reads on start, on focus and visibility (at most every 10 s), every 5 minutes while the page is visible and after the person's own actions; the last release forgets the counts. Alerts of a muted kind stay in the list but leave the badge (AU9). | for Lars to review |
| AU6 | The components live in the main entry point, not in `agent-runs` or a new entry point: alerts are not agent work, the badge must be in the shell, and unused standalone components are tree-shaken. The push switch moved into one internal component that both preference components show, so `nh-assistant-preferences` keeps its public `push` field and `toggleNotifications` only as deprecated members that delegate to `NhAssistantPushService` (restored in the train, so the change stays compatible as the plan's rules require). The list is grouped by default. | for Lars to review |
| AU7 | The notification choices save together: one Save sends only the changed kinds and fields. "Receive these alerts" is the inverse of `muted`; the time field shows the kind's default, and choosing the default or clearing it sends `digestTime: null`. The mock refuses a change the kind does not allow with AB's `assistant-notification-*` code, and the component shows its text. | for Lars to review |
| AU8 | The sample's Alerts page is `/management/alerts` (assistant access), with the list beside the choices; the shell's bell and navigation count use a warning icon so they differ from the platform notifications' bell. `openSubject` follows the server's link of the subject type. `/management/assistant/alerts` shows the same page on the mock API. | for Lars to review |
| AU9 | A delivery that the dispatcher withheld (AB3) stays in the person's list as `unread` with `isWithheld` and `withheldReason`, and the component labels it "Muted" for a muted kind and "Not notified" otherwise. Reading it marks it read (beside AK's `MarkReadAsync`, which leaves withheld rows alone) and dismissing it works as for any other. The badge leaves out what was withheld for a muted kind, so muting a kind also quiets its badge; one withheld for the daily limit still counts. | for Lars to review |
| AU10 | The sample sets `NhAssistantAlertOptions.ListUrl` and `push.alertsUrl` to `/management/alerts`, and its shell routes `NhAssistantPushService.openAlerts$` there, as it routes `openRun$` to the inbox. The mock refuses notification choices with AB's `assistant-notification-*` codes. | for Lars to review |
