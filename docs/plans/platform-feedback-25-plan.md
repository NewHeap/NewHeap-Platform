# Platform Feedback on 25.0.1 Plan

A consuming application moved to NuGet 25.0.1, `@newheap/platform-ai-chat` 0.24.0,
`@newheap/platform-common` 5.0.0 and guidance 1.13.92, scripts included, and reported what it met: a bug
in the generated tool schemas, six points on the panel's page mode, two on approvals and two on channels
that use `INhAssistantConversations`. Each finding was checked against `main` at d9b8d84b (NuGet 25.0.1);
all are still open. This plan records one choice per finding. It is a plan, not usage documentation: the
guidance rules, the release notes and the sample cases are authoritative once it is built.

## Status

Decided by Lars on 2026-10-29: FB43 to FB55, every one as advised (option (a)); FB43 to FB45 one by one, the
rest with "everything as advised". The decisions are summarized below; the sections under "Choices" keep the
options that were weighed. Built on 2026-10-29: FB43 as a fix of its own (lane TD), then train 30 with lanes CC (FB52,
FB53, plus a fix for a stored page context that did not fit its column), AV (FB50, FB51) and PM (FB44 to FB49).
Lars kept every lane choice in the "Lane choices" table on 2026-10-29.

## Decisions

| # | Decision |
|---|---|
| FB43 | **`DateOnly` and `TimeOnly` are strings with `format` `date` and `time`** in the generator and every other schema writer; the script signatures show them and the call check validates them; a breaking row for the changed contract hash. |
| FB44 | **The store counts the panels that show the thread**: each registers its own state and unregisters when destroyed; `setViewing` stays for hosts. |
| FB45 | **A shown page panel registers with the panel service**, and `open` with a conversation, agent or draft acts on it while the drawer stays closed. |
| FB46 | **Slots above the thread and above the composer, and an optional `beforeSubmit`** that may hold a message for the host's decision. |
| FB47 | **A two-way `conversationsOpen` input** for the conversation list. |
| FB48 | **The list's class becomes `nh-assistant-sidebar`**, with a release-note row. |
| FB49 | **An input `conversationList: 'panel' | 'none'`**; `'none'` leaves the list and its toggle out. |
| FB50 | **An optional `INhAssistantApproverCandidates`** names the people a policy rule may admit and whether that list is complete, so the directory can answer `Complete: true`. |
| FB51 | **`PolicyId` and `Mode` on `NhAiProposalGuardContext`** at revalidation and decision, **and on `NhAssistantProposalHandOff`**. |
| FB52 | **`ClientContext` on `NhAssistantConversationMessage`**, validated and stored as the endpoint does. |
| FB53 | **A public HTTP status mapping of assistant codes**, which the endpoints use themselves. |
| FB54 | **Nothing waits for the application's evidence.** |
| FB55 | **FB43 first as a fix of its own, then train 30** with lanes for page mode (FB44 to FB49), approvals (FB50, FB51) and channels (FB52, FB53). |

## Rules for all of it

- Every change is opt-in or compatible unless its choice says otherwise. A breaking change gets a
  `Breaking change | Required action` row.
- Every change follows the repository rules:
  - code and migrations for SQL Server and PostgreSQL where persistence changes;
  - tests with authorization negatives;
  - a release-notes section per release unit;
  - guidance and consumer guide updates;
  - an executable sample case;
  - English and Dutch texts for Angular parts, and the Angular lifecycle extension point rules of
    `AGENTS.md` for the panel.
- Nothing names the consuming application.

## Choices

Each choice lists the options that were weighed. "(advice)" marks the recommendation, which Lars followed for all
of them.

### A. Tool schemas

#### FB43. `DateOnly` and `TimeOnly` in generated tool schemas

`NhAiToolGenerator` writes `DateTime`, `DateTimeOffset`, `Guid` and `TimeSpan` as strings with a format,
but has no case for `DateOnly` and `TimeOnly`, so they become objects of their public properties
(`{ day; dayNumber; dayOfWeek; ... }`) while System.Text.Json reads and writes them as `"2026-11-01"` and
`"14:30:00"`. A script's call is checked against the schema before it runs, so a script cannot pass a date
the tool accepts, and the model is shown the wrong shape. The MVC bridge already treats both as simple
values.

- **(a) Strings with a format, everywhere a schema is written (advice).**
  - The generator writes `{"type":"string","format":"date"}` for `DateOnly` and
    `{"type":"string","format":"time"}` for `TimeOnly`, nullable variants included.
  - The script signatures show them as `string /* date */` and `string /* time */`, and the call check
    accepts only `yyyy-MM-dd` and `HH:mm[:ss[.fffffff]]`, as it checks `uuid` and `date-time` today.
  - Check the other schema writers (the MVC bridge, MCP imports) and align them.
  - A tool with such a parameter gets a new schema and contract hash; a breaking row says so, because a
    stored review of an MCP tool or a pending proposal of that tool follows its contract hash.
- **(b) Only the generator.** Smaller, but the script check still lets any string pass as a date.

### B. The panel's page mode

#### FB44. Which panel shows the open thread

The drawer and a page panel (`mode="page"`) both call `NhAssistantStore.setViewing`: whichever runs last
wins. A drawer created after the page, or opened and closed while the page shows the conversation, leaves
the page's thread unseen; a destroyed page panel leaves it seen.

- **(a) The store counts the surfaces that show the thread (advice).** Each panel registers its own
  "showing" state and unregisters when it is destroyed; the thread is in view while any panel shows it.
  `setViewing` stays for hosts that call it themselves.
- **(b) The page panel wins.** Simpler, but a drawer over another page would still be wrong.

#### FB45. The drawer over a page panel

`NhAssistantPanelService.open` shows the drawer also when a page panel already shows the conversation, so
two chats are on screen.

- **(a) A page panel registers with the panel service, which opens there (advice).** While a page panel
  is shown, `open(conversationId)`, `{ agentId }` and `{ draft }` act on the page panel instead of the
  drawer, and the drawer stays closed. Without a page panel nothing changes.
- **(b) An option on `open` that names the target.** Every caller then has to know which panel exists.

#### FB46. Host content and a hook before sending

The panel has no projected slots and no hook before sending. A host that shows its own bar above the thread
or must hold the first message of a new conversation for a decision (continue an existing one, start
apart, change the question) builds around the panel or replaces its composer.

- **(a) Named slots and an optional `beforeSubmit` (advice).**
  - Content projection slots above the thread and above the composer (attribute selectors with the
    `nh-assistant` prefix).
  - An optional input `beforeSubmit: (message) => Promise<boolean>` that may hold a message: `false`
    keeps the draft unsent for the host's decision, which then sends or changes it through the store.
  - Both follow the Angular lifecycle extension point rules.
- **(b) Only the hook.** The host keeps placing its content around the panel.

#### FB47. Opening the conversation list from outside

A host opens the list on narrow screens through the component's public `showConversations()` and
`toggleConversations()`, which are internal state.

- **(a) A two-way `conversationsOpen` input (advice)**, a `model()` the host binds; the internal signal
  follows it.
- **(b) Documented methods on the component.** The host needs a `viewChild` to call them.

#### FB48. A generic class name

The list in page mode has `class="sidebar"`, which an application's global `.sidebar` rule also hits.

- **(a) A prefixed class, `nh-assistant-sidebar` (advice)**, with a release-note row for hosts that styled
  `.sidebar` inside the panel.
- **(b) Leave it.**

#### FB49. Page mode without its own conversation list

A host with its own list of conversations next to the page panel shows two lists; the panel has only the
input `mode`, so the host hides the list with a scoped style.

- **(a) An input `conversationList: 'panel' | 'none'`, default `'panel'` (advice).** `'none'` leaves the
  list and its toggle out; the host opens conversations through the store or the panel service (FB45).
- **(b) Leave it to styling.**

### C. Approvals

#### FB50. An approver list the application may declare complete

`INhAssistantApproverDirectory` reports every list under a policy rule (`NhAiApprovers.Policy`) as
incomplete, because such a rule admits anyone who passes it. So it never says "exactly one person may
approve", which an application asks before a selector chooses four eyes, and again before a proposal with
one approver runs.

- **(a) Application candidates for a policy rule (advice).** An optional
  `INhAssistantApproverCandidates` (registered on the assistant builder) names the people a policy rule
  may admit at a subject, the whole application included, and says whether that list is complete. The
  directory checks each candidate against the rule as it checks members, and answers `Complete: true`
  when the application declared its list complete and no other limit applied.
- **(b) `Members<T>(complete: true)` that also covers policy rules.** It mixes the system subject's members
  with the people of every policy.
- **(c) Leave it.** The application keeps its own count.

#### FB51. The policy a proposal waits under, for guards and the executor

The selector chooses the policy once, when the proposal is recorded. What decided the choice can change
while the proposal waits, but neither `NhAiProposalGuardContext` nor `NhAssistantProposalHandOff` names
the policy, so an application reads `PolicyId` and `Mode` from the assistant's tables.

- **(a) The recorded policy on the guard context and the hand-off (advice).** `PolicyId` and `Mode`
  (`NhAiApprovalMode`) on `NhAiProposalGuardContext` at revalidation and decision (they are not chosen yet
  when a call proposes) and on `NhAssistantProposalHandOff`.
- **(b) A public lookup by proposal id.** One more call and one more place to authorize.

### D. Channels on `INhAssistantConversations`

#### FB52. The page a person has open, from a channel

`POST conversations/{id}/messages` takes the page a person has open (`clientContext`) and the turn shows it
as the person's screen, but `NhAssistantConversationMessage` of `INhAssistantConversationTurns.SendAsync`
has only text and files, so a website widget passes the screen through a turn context provider of its own.

- **(a) `ClientContext` on `NhAssistantConversationMessage` (advice)**, validated and stored as the
  endpoint does, with the endpoint's codes for an invalid context.
- **(b) Leave it to turn context providers.**

#### FB53. The HTTP status of an assistant code

An application that serves a channel through its own API and NewHeap's services maps NewHeap's codes to
HTTP statuses, and the endpoints' mapping (`NhAssistantHttpErrors.StatusOf`) is internal.

- **(a) A public mapping (advice)**, for example `NhAssistantHttpStatuses.Of(code)`, which the endpoints
  use themselves, so both stay the same.
- **(b) Document the table.** It drifts from the code.

### E. Delivery

#### FB54. What waits for the application's evidence

- **(a) Nothing (advice).** Every finding is confirmed in the code.
- **(b) Ask the application for its page-mode setup first.**

#### FB55. Trains

- **(a) FB43 first as a fix on its own, then one train (advice).**
  - FB43 is a bug that touches every tool with a date; it lands first, so it can go out as a patch.
  - **Train 30**, in three lanes: the panel's page mode (FB44 to FB49), approvals (FB50, FB51) and
    channels (FB52, FB53).
- **(b) Everything in one train.** FB43 then waits for the rest.

## Lane choices

| # | Choice |
|---|---|
| TD1 | Built by lane TD (2026-10-29): **the `time` check accepts `HH:mm`, `HH:mm:ss` and `HH:mm:ss` with one to seven fraction digits, two-digit fields only and no offset; System.Text.Json reads all of these and also `9:5`, which the check refuses, so it is never looser than the tool**, kept by Lars (2026-10-29). |
| TD2 | Built by lane TD (2026-10-29): **the `date` and `time` checks apply to every schema with these formats, imported MCP tools included, with the platform's meaning (a `DateOnly` and a `TimeOnly`); an imported tool that follows JSON Schema's RFC 3339 `time` with an offset is refused, which the release note says**, kept by Lars (2026-10-29). |
| TD3 | Built by lane TD (2026-10-29): **the messages are `expected a date string yyyy-MM-dd` and `expected a time string HH:mm[:ss[.fffffff]]`, without the value, like the `uuid` and `date-time` messages**, kept by Lars (2026-10-29). |
| TD4 | Built by lane TD (2026-10-29): **the MVC bridge (`JsonSchemaExporter`) and composed tools (`AIJsonUtilities`) already write `date` and `time`, so only tests pin them; MCP imports pass remote schemas on and are no type writer; the collection contract's `NhCollectionFieldType` still describes a `DateOnly` filter field as `object` in the gateway's describe result, which is a filter description in `NewHeap.Platform.Common` with a string value, left for a change of its own**, kept by Lars (2026-10-29). |
| TD5 | Built by lane TD (2026-10-29): **a new case SPM-375 instead of extending SPM-373, because the new `projects.due-by` tool belongs to both the local tool contract rule and the scripting rule, which both list it**, kept by Lars (2026-10-29). |
| TD6 | Built by lane TD (2026-10-29): **the sample's `projects.due-by` reads its day and time in UTC and says so in its description, queries the sample's DbContext as `project-tasks.complete` does, is exposed locally and to agents, and is registered by `AddSampleProjectDeadlines` in Program.cs and the script test host only, so other hosts' tool lists stay as they are**, kept by Lars (2026-10-29). |
| TD7 | Built by lane TD (2026-10-29): **`NewHeap.Platform.AI.Scripting.Tests` references the tool generator as an analyzer and `NewHeap.Platform.AI.Test`, so a generated tool proves without a database that it receives the `DateOnly` and `TimeOnly` a script passed**, kept by Lars (2026-10-29). |
| TD8 | Built by lane TD (2026-10-29): **one release-note section for the generator and scripting (both `nuget-common`) with three rows: the new contract hash and open proposals, the MCP import that turns `schema-changed`, and the stricter script check**, kept by Lars (2026-10-29). |
| CC1 | Built by lane CC (2026-10-29): **`NhAssistantConversationMessage.ClientContext` takes the endpoint's public `NhAssistantClientContextDto`** (with `NhAssistantClientEntityDto`), not a new record; the service maps it to the internal context, kept by Lars (2026-10-29). |
| CC2 | Built by lane CC (2026-10-29): **the service reads the page through the endpoint's own reader** (`NhAssistantClientContext.From` over the DTO's JSON), so the screen and the stored context are the endpoint's; text is cleaned and cut off as there. The endpoint has no code for an invalid context (it ignores one), so **what the endpoint would ignore or only partly keep is refused with `assistant-message-invalid`**: a route without text, more than five entities, a `null` entity, a type that is not dash-case, an id that is empty, too long or holds a control character, kept by Lars (2026-10-29). |
| CC3 | Built by lane CC (2026-10-29): **the service also refuses a page whose stored form exceeds the 4,000-character `ClientContextJson` column** (`assistant-message-invalid`). The stored JSON escapes text outside ASCII, so a page with such text near every limit reaches about 7,900 characters; the endpoint's own path is unchanged and would fail in the store with it, which is reported, not fixed, kept by Lars (2026-10-29). |
| CC4 | Built by lane CC (2026-10-29): **an invalid page is refused with the message's other checks**: after the person, the chat and the agent's policy, before the display name is refreshed and before the turn claims the chat; nothing is stored, kept by Lars (2026-10-29). |
| CC5 | Built by lane CC (2026-10-29): **`NhAssistantHttpStatuses.Of(code)` with `UnknownCodeStatus` (`400`)** as the documented default; a `null` or blank code throws `ArgumentException`; no `TaskResult` overload and no public "known code" check, kept by Lars (2026-10-29). |
| CC6 | Built by lane CC (2026-10-29): **the table stays in the internal `NhAssistantHttpErrors`**, which other lanes extend; the public class reads it and the endpoints' error responses answer with `NhAssistantHttpStatuses.Of`, so one table serves both, kept by Lars (2026-10-29). |
| CC7 | Built by lane CC (2026-10-29): **new sample cases SPM-380 (the provider's web widget sends `page` with a message) and SPM-381 (`SampleAssistantMessagingChannel.StatusOf`: own codes, then `NhAssistantHttpStatuses.Of`)** on the messaging channel instead of extending SPM-332/SPM-341; the channel's own status switch is gone, so for example `assistant-disabled` now answers `404` and `assistant-proposal-check-failed` `503` where it answered `400`, kept by Lars (2026-10-29). |
| CC8 | Built by lane CC (2026-10-29): **the FB53 table test pins the 225 codes of 25.0.1 with their statuses** in a new `AssistantHttpStatusTests`; a code another lane adds needs no row there (`AssistantHttpErrorTests` still requires every code to have a status), kept by Lars (2026-10-29). |
| CC9 | Built by lane CC (2026-10-29), at the coordinator's request: **the stored client context is written with `JavaScriptEncoder.Create(UnicodeRanges.All)`**, so text outside ASCII stays as it is while HTML-sensitive characters stay escaped; a valid page at every limit in any language now fits the 4,000-character column, and contexts stored before read back unchanged. This fixes the endpoint failure CC3 reported, kept by Lars (2026-10-29). |
| CC10 | Built by lane CC (2026-10-29): `&` and emoji are still escaped six characters each, so a page full of them near every limit can still exceed the column. **The shared reader (`NhAssistantClientContext.From`) ignores a context whose stored form would not fit**: the endpoint shows and stores the message without it, as for an unreadable context, and the service refuses it with `assistant-message-invalid` through the same reader. This replaces CC3's separate check, kept by Lars (2026-10-29). |
| AV1 | Built by lane AV (2026-10-29): **`UseApproverCandidates<TCandidates>()` on the assistant builder**, a scoped service that replaces an earlier one; `INhAssistantApproverCandidates` lives in `NewHeap.Platform.AI.Chat` next to the policy selector, because the builder does, kept by Lars (2026-10-29). |
| AV2 | Built by lane AV (2026-10-29): **the candidates cover policy rules and the role holders of a subject without listed members** (the whole application, whose roles follow its approve policy), told apart by `PolicyNames` and `SubjectRoleHolders` on `NhAssistantApproverCandidateQuery`; the directory asks only when people outside the members may approve and the members did not fill the limit, kept by Lars (2026-10-29). |
| AV3 | Built by lane AV (2026-10-29): **the query passes the subject's tenant as its resolver described it** (after the caller's tenant was checked), its division and `MaxCandidates`; the answer is a list of `NhAssistantApproverCandidate` (actor id, optional `DisplayName`) with `Complete`, or `NhAssistantApproverCandidateList.Unknown`, listed after the members in the order given, kept by Lars (2026-10-29). |
| AV4 | Built by lane AV (2026-10-29): **`MaxCandidates` (1,000) counts the application's candidates apart from the members**; a longer list is never complete, kept by Lars (2026-10-29). |
| AV5 | Built by lane AV (2026-10-29): **a candidates service that throws is logged with its type and the exception's type and leaves the list incomplete** instead of failing the directory call; cancellation still propagates, kept by Lars (2026-10-29). |
| AV6 | Built by lane AV (2026-10-29): **`PolicyId` and `Mode` are set at execution too**, since one context serves all stages after the proposal was recorded; they stay `null` for the keys of a new proposal, also one an asker leaves for the subject's approvers, kept by Lars (2026-10-29). |
| AV7 | Built by lane AV (2026-10-29): **`Mode` is the mode the proposal recorded** (after the route, such as inline for a person's message in an agent's thread), not the policy's configured mode, and `PolicyId` is `null` for a person's own chat, kept by Lars (2026-10-29). |
| AV8 | Built by lane AV (2026-10-29): **`NhAssistantProposalHandOff.Mode` is `NhAiApprovalMode?`** like the guard context's, always set by the assistant and `null` only in a hand-off an application builds; the record-equality change of both records is a breaking row, kept by Lars (2026-10-29). |
| AV9 | Built by lane AV (2026-10-29): **new cases SPM-378 and SPM-379 extend SPM-356**: the selector lets the application's project managers decide under `project-intake-portfolio` (a policy rule on `app.project.manage`, named by `SampleApproverCandidates`) before it chooses the sole-approver policy, and `SampleSoleApproverProposalGuard` (revalidation and decision) closes a sole-approver review as `not-applicable` once two people may approve; SPM-356's outcome and the catalog say so, and SPM-378 runs in a host of its own, kept by Lars (2026-10-29). |
| AV10 | Built by lane AV (2026-10-29): **no endpoint, audit kind or migration**; the approval already stores its policy and mode, kept by Lars (2026-10-29). |
| PM1 | Built by lane PM (2026-10-29): **FB44 as a public `NhAssistantStore.registerThreadSurface()` that returns `NhAssistantThreadSurface` (`setShowing`, `unregister`), with a read-only `threadInView()`**; the panel registers one surface per instance and unregisters it on destroy, and a custom layout with several surfaces can do the same, kept by Lars (2026-10-29). |
| PM2 | Built by lane PM (2026-10-29): **`setViewing` is the host's own surface, combined with the panels by "or"**: the thread is in view while it or any panel shows it, so `false` never hides what a panel shows and the panels no longer reset a host's `true`; a breaking row asks a host to call `setViewing(false)` when its surface stops showing the thread, kept by Lars (2026-10-29). |
| PM3 | Built by lane PM (2026-10-29): **FB45 registration by host element: `registerPagePanel(element)`, `unregisterPagePanel(element)` and a public `pagePanelShown()`**; the page panel registers in `ngAfterViewInit` as the drawer does (it has no NewHeap lifecycle base), and the last page panel that registered is the target, kept by Lars (2026-10-29). |
| PM4 | Built by lane PM (2026-10-29): **a page panel that shows closes an open drawer, and the drawer neither opens nor restores while one shows; `open()` without a target, also the launcher's toggle, moves the focus to the page's composer**, so the chat is never on screen twice and the launcher still does something on a chat page; a draft is dropped when the page panel left before the assistant was ready, as for a closed drawer; a breaking row, kept by Lars (2026-10-29). |
| PM5 | Built by lane PM (2026-10-29): **slots `nhAssistantAboveThread` and `nhAssistantAboveComposer`**, camel-case attributes like the composer's `nhAssistantComposerControls`; the first sits between the header (and activity) and the thread, the second in the footer directly above the composer, so it leaves with the composer while the preferences or share view show; an empty place takes no room, kept by Lars (2026-10-29). |
| PM6 | Built by lane PM (2026-10-29): **the message is `NhAssistantOutgoingMessage` (`text` trimmed, `files`, `conversationId` or `null` for a new conversation, `firstMessage`, `agentId`) and the input type `NhAssistantBeforeSubmit`**; `firstMessage` is also true for a conversation created for its attachments, which already has an id, kept by Lars (2026-10-29). |
| PM7 | Built by lane PM (2026-10-29): **while `beforeSubmit` runs the message stays in the composer as the person's own unsent text and only sending is blocked (the editor keeps the focus); `false` leaves it there; `true` sends it unless the person changed it or opened another conversation meanwhile; a rejected promise holds it and goes to Angular's `ErrorHandler`**; the host decides with existing store methods (`keepUnsentMessage(null)` then `submit`, `setDraft(..., { replace: true })`, `openConversation`), so no new store method, and messages the host sends through the store do not pass the hook, kept by Lars (2026-10-29). |
| PM8 | Built by lane PM (2026-10-29): **`conversationsOpen` is a `model(false)` and `showConversations` stays as a deprecated alias of the same signal**, so no breaking row; only a change of the open conversation closes the list, not the first conversation the panel shows, so a host that binds `true` keeps the list open, kept by Lars (2026-10-29). |
| PM9 | Built by lane PM (2026-10-29): **`conversationList="none"` applies in the drawer too**: an input that does nothing in one mode would surprise, and a host with its own list opens conversations with `open(conversationId)` there as well; with `none` `conversationsOpen` shows nothing, and a page without a list (or with the assistant unavailable) uses one column instead of an empty list column, kept by Lars (2026-10-29). |
| PM10 | Built by lane PM (2026-10-29): **the class becomes `nh-assistant-sidebar` without keeping `sidebar` as an alias**, with a breaking row, since an alias would keep the clash with an application's global `.sidebar` that FB48 removes, kept by Lars (2026-10-29). |
| PM11 | Built by lane PM (2026-10-29): **new cases SPM-376 (the page as the place the assistant opens, thread surfaces, `conversationsOpen`, `conversationList`, the class) and SPM-377 (slots and `beforeSubmit`) on the management portal's Chat page**: a topic bar names the open conversation and its "Conversations" button switches `conversationList`; `beforeSubmit` holds the first message of a new conversation that does not exist yet (so without files) while the person has an earlier conversation with that agent, and offers "Continue in" the latest one, "Start a new conversation" and "Change the question"; `conversationsOpen` is proven by the library specs only; SPM-253 keeps the page mode's basics, kept by Lars (2026-10-29). |
