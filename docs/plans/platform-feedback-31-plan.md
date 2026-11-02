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

Not as advised: the model tool comes in phase 2, with templates, instead of not at all. Phase 1 is not built yet.

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
