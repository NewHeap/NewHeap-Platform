---
id: nh-ai-assistant-ui
title: "Host the NewHeap assistant panel in an Angular application"
area: frontend
reference: ai-assistant-ui
summary: "Register the assistant once with the host's token and access policy, place the launcher and one panel in the layout, let users run several conversations at once, share them and get desktop notifications, route to the lazily loaded administration page behind the admin permission, and test against the scripted mock API instead of a live model."
sample-cases: ["SPM-248", "SPM-253", "SPM-264", "SPM-265", "SPM-277", "SPM-278", "SPM-279", "SPM-280", "SPM-295", "SPM-296", "SPM-297", "SPM-298"]
public-symbols: ["provideNhAssistant", "NhAssistantConfig", "NhAssistantToolCallGroupComponent", "NhAssistantAccessPolicy", "NhAssistantApiService", "NhAssistantAdminApiService", "NhAssistantStore", "NhAssistantPanelService", "NhAssistantLauncherComponent", "NhAssistantPanelComponent", "NhAssistantPreferencesComponent", "NhAssistantAdminComponent", "NhAssistantClientContext", "normalizeNhAssistantClientContext", "NH_ASSISTANT_ICONS", "NH_ASSISTANT_TRANSLATIONS", "provideNhAssistantMockApi", "NhAssistantMockBackend", "NhAssistantLiveService", "NhAssistantPushService", "NhAssistantPushConfig", "NhAssistantActivityComponent", "NhAssistantShareComponent", "nhAssistantIsUnread", "nhAssistantShareLink", "NH_ASSISTANT_JOIN_FRAGMENT", "NH_ASSISTANT_CONVERSATION_FRAGMENT", "NhAssistantMockLiveService", "getRequestHeaders", "NhAssistantAgentRunApiService", "NhAssistantAgentInboxComponent", "NhAssistantAgentThreadViewComponent", "NhAssistantAgentProposalsComponent", "NhAssistantRunTimelineComponent", "NhAssistantAgentMemoryComponent", "NhAssistantAgentArtifactsComponent", "NhAssistantAgentConsultationsComponent", "getHubQueryParameters", "NhAssistantApprovalRight", "NhAssistantApprovalChoice", "ConversationThread", "NhAssistantProposalFeedbackComponent", "NhAssistantProposalFeedbackSaved", "NhAssistantAgentFeedbackReportComponent", "NhAssistantFeedbackPeriod", "AgentProposalFeedback", "AgentProposalFeedbackCategory", "AgentProposalFeedbackGroup", "AgentFeedbackOutcome", "AgentFeedbackSummary", "AgentFeedbackSummaryRow", "AgentFeedbackItem", "AgentFeedbackList", "AgentFeedbackFilter", "NH_ASSISTANT_FEEDBACK_CATEGORIES", "NH_ASSISTANT_FEEDBACK_GROUPS", "NH_ASSISTANT_MAX_FEEDBACK_TEXT", "NH_ASSISTANT_MAX_BULK_FEEDBACK", "LiveAgentWorkChanged", "LiveAgentWorkReason", "NhAssistantSubjectBadgeComponent", "NhAssistantSubjectSummaryService", "NhAssistantSubjectSuggestionsComponent", "AgentSubjectWork", "AgentSubjectSummary", "AgentProposalDecisionRight", "NH_ASSISTANT_MAX_SUBJECT_SUMMARY", "NhAssistantAgentRunConfigurationComponent", "RunCatalog", "AgentRunConfiguration", "DataSkill", "DataTrigger", "AgentRunSettings", "NhAssistantError", "AssistantAccount", "ApprovalPolicySummary", "ApproverSummary", "NhAssistantMcpConnectionsComponent", "McpConnection"]
skills: ["newheap-frontend-development"]
providers: ["frontend"]
risk: high
---
## Preferred approach

Install `@newheap/platform-ai-chat` with its peers (`@angular/cdk`,
`@angular/router`, `@ngx-translate/core`, `marked`, `dompurify`) and load
`@angular/cdk/overlay-prebuilt.css` once in the application styles. Call
`provideNhAssistant(...)` once in the root providers with the assistant base URL,
`getAccessToken` and an `accessPolicy` class. `getAccessToken` runs in the
assistant's injection context before every request, so it can read the token from
the host's existing auth service through `inject(...)`. Base the access policy on
the same permission the API enforces (for example `app.assistant.access`) and
return an observable when the signed-in user can change, so the launcher follows
sign-in and sign-out. Emit on account changes even when both accounts have the
same access boolean, so the store discards the previous user's in-memory state.
Pass `getRequestHeaders` when the API's access policy or tools need more than the
token, for example the NewHeap active division
(`{ 'X-NH-ActiveDivisionId': authorization.activeDivision.id }`). It runs like
`getAccessToken` before every request; the library keeps `Authorization`, `Accept`,
`Accept-Language` and `Content-Type` under its own control and drops empty values.
Browsers send no custom headers with the WebSocket and server-sent event transports
of the live connection, so pass what the hub needs with `getHubQueryParameters` as
well (see live updates below).

Pass `getPageContext` when pages can say what the user has open. It runs in the
assistant's injection context when the panel opens and before every message and
returns `{ route, title?, entities? }`, for example the project a detail page shows.
Keep a small root service that entity pages set while they are active and clear on
destroy, and fall back to the route and title. The library validates and truncates
the result to the contract limits, and a failing getter only leaves the context out.
The page icon beside the composer actions describes the included context in its
tooltip; the user can leave it out of the next message, which sends `null`. Page context is untrusted data for the
model: entity ids are search hints and never replace authorization.

Pass `getStateScope` with a stable, non-secret user-and-tenant identifier when
the assistant should restore its selected agent, active conversation and open
drawer after a browser restart. Return `null` on sign-out; do not use a bearer
token or an identifier shared by multiple accounts. Without a scope, UI state
stays in memory. The library stores only these UI pointers in browser storage,
re-fetches the conversation through the current user's API session and discards
an inaccessible conversation. It never stores messages, drafts, credentials or
page context. Each new message reads `getPageContext` afresh, so a restored
conversation does not replay a stale screen hint. Pending approvals are read
from the authoritative server conversation, not browser storage.

Place `<nh-assistant-launcher />` in the header and exactly one
`<nh-assistant-panel />` in the application layout. Other components open the
panel through `NhAssistantPanelService.open(conversationId?)`. The launcher stays
hidden while the server reports the assistant disabled or the policy denies the
user; do not add a second feature flag in the host.

Select the assistant in the prompt bar below the message field. The panel shows the
picker only when more than one assistant is available; a single assistant remains
automatically selected without showing a disabled selector. In custom layouts,
project controls marked with `nhAssistantComposerControls` into `nh-assistant-composer`
and use `[compact]="true"` on `nh-assistant-agent-picker`. The compact picker keeps
capability information in its tooltip and accessible description. Selection preserves
the draft and uses the chosen agent for the next new conversation.

Personal preferences need no host code: the panel header opens
`nh-assistant-preferences`, which stores style, form of address, answer length and
the user's own instructions through the preferences endpoints. Preferences only
steer style; the server keeps them below the application context and the agent
instructions. When an administrator allows an MCP server that forwards the user's token
in background runs (`headlessUse: 'personal'`), the preferences also show
`nh-assistant-mcp-connections`: one switch per server of an agent the user may use,
which allows or withdraws background use with the user's own account at once
(`GET` and `PUT mcp-connections`). It renders nothing without such servers; a host
may place it on its own profile page instead.

Route to `NhAssistantAdminComponent` from `@newheap/platform-ai-chat/admin` with a
lazy route guarded by the host's admin permission (for example
`app.assistant.admin`), and pass that route as `adminRoute` to
`provideNhAssistant`. The panel links to it only when the server reports
`canAdminister`, and the page shows a no-access state otherwise, so the host guard,
the server status and the admin policy on the endpoints agree. The separate entry
point keeps the administration out of the initial bundle.

Keep the bundled `en` and `nl` texts (`translations: 'bundled'`); a host language with
a region, such as `nl-NL`, receives the texts of its base language, so do not merge the
bundles into regional languages yourself. Add only host-specific keys under `nh-assistant.`: agent names and descriptions from the
server's `displayNameKey`/`descriptionKey`, and message keys of host-specific
error codes. Agents created by an administrator carry literal text in those fields,
which the panel shows as is. The administration also reads
`nh-assistant.agents.<agentId>.skills.<skillId>.name` and `.description` for skills (otherwise
a readable form of the skill id and the description from code) and `nh-assistant.policies.<policy>`
for the policy an agent requires (otherwise the policy as it is named in code). Its agent editor
shows the translations of an agent in code and sends the keys back unless the administrator
changes the text. Theme the panel through the `--nh-assistant-*` custom
properties mapped onto the host's tokens, and pass the host icon library through
`NH_ASSISTANT_ICONS`.

Use `NhAssistantStore.submit` for the panel's composer: it sends while idle and queues
follow-ups while `running` or `waiting-for-approval`. The queue is FIFO per conversation,
keeps the page context captured when each message was queued and continues when the
user opens another conversation or closes the drawer. It lives only in this tab's
memory and clears on account changes, loss of access, deletion or leaving the conversation.
Render `queuedMessages` with edit/remove controls. Pausing the queue while editing
prevents dispatching unfinished edits; changing a queued message must keep the current
composer draft. Stop pauses dispatch; `resumeQueue` is an explicit action after Stop or
a failure. A refused message stays in the queue without overwriting a newer draft.
`steerQueuedMessage` prioritizes the selected correction, cancels the active turn through
the normal authorized API and waits for confirmed idle state before sending it in the
same conversation. It does not inject instructions into a running model request or
automatically approve tools. Only the owner or the active turn's author may steer.
Keep the composer editable and use `[queueWhileBusy]="true"` with `canSubmit` for a
custom layout. Standalone composers retain their default send-blocking behavior.
Return focus after
an explicit send and restore it after a disappearing composer-owned control only when
the user has not moved focus elsewhere; skip automatic restoration on coarse pointers.
Use a compact localized approval status; do not show the old sending-unavailable notice.

A turn that fails leaves the conversation open, as the server does: the error bar keeps the
translated failure, the composer stays available and the next message starts a new turn in
the same conversation or agent thread. An explicit send goes out directly while nothing is
queued, even with the error bar shown; queued messages still wait for `resumeQueue`. When a
conversation whose last turn failed is reopened, the panel says that the last answer could
not be completed. Only an archived conversation is closed for new messages. Custom layouts
gate sending with `canSend`/`canSubmit` instead of blocking on the `failed` status.

Let the thread render tool calls. Consecutive calls collapse into one
`nh-assistant-tool-call-group` line that names the tool that runs now and counts
finished calls, and afterwards summarizes how many tools were used and how many
failed; expanding it shows every call card with its result code and technical
details. Text and approvals break a group, so an approval card is never hidden in a
collapsed group. Between tool calls, while the model decides what to do next, the
working indicator shows the elapsed seconds, so a long turn never looks stalled.
Custom layouts that render parts themselves should reuse the group component rather
than listing every call card.

Render `approval.presentation` as the primary explanation. When it is absent, use the
bundled localized fallback instead of `summary` or a tool descriptor. Keep tool ids,
targets and bounded/redacted argument previews in collapsed technical details. A
presentation is explanatory only: always decide with the approval id and exact proposal
hash supplied by the server.

Conversations run in parallel: opening or starting another conversation never stops a
running turn, and the store keeps one session per opened conversation. The public
signals (`activeConversation`, `streaming`, `error`, ...) describe the open one;
`activity`, `runningCount` and `unreadCount` describe the others. The panel shows a
slim chip row below its header only while another conversation works, waits for
approval or has an unread answer, and the launcher shows one badge in that priority.
Use these signals in custom layouts instead of polling the list. The store marks the
open conversation as read only while the drawer shows it in a visible, focused
window; call `setViewing` when a custom layout shows the thread elsewhere.

Sharing needs no host code beyond the backend registration: separate link and people
icons above the composer open the invitation-link and participant views, where the owner creates, copies, renews and stops the invitation link and,
when the server reports a directory, searches and invites colleagues. The default link
is the current page with `#nh-assistant-join=<id>.<token>`; the panel joins when the
page opens with it and removes the fragment. Pass `buildShareLink` when invitations
must open a specific route. In a shared conversation the thread names other people's
messages, the composer says who is working, and an approval of someone else's turn
shows who decides instead of the buttons.

Show the work of agents that run on their own with the components of
`@newheap/platform-ai-chat/agent-runs`, a separate entry point that stays out of the
initial bundle. `nh-assistant-agent-inbox` lists the proposals waiting for the
signed-in person across subjects; `nh-assistant-agent-thread-view` shows one
agent's thread on one subject with its proposals, run timeline, consultations by other
assistants, working memory and files, lets participants start the agent and emits
`openConversation` so the host can open the conversation through
`NhAssistantPanelService.open`. Use the individual components
(`nh-assistant-agent-proposals`, `nh-assistant-run-timeline`,
`nh-assistant-agent-consultations`, `nh-assistant-agent-memory`,
`nh-assistant-agent-artifacts`) in custom layouts. Consultations show who asked through
which assistant and how it ended, never the question or the answer, and a proposal from
a consultation carries a badge. The
components call the server through `NhAssistantAgentRunApiService` and show only what
the server returns for the caller's subject role: decision buttons appear only for
people who may decide. Like the approval card, a proposal shows the application's
`presentation` as its explanation and keeps the tool id, targets and input under
collapsed technical details. In the conversation, the tool-call card marks results the agent
obtained with its own grant with a badge and shows them as restricted, without content,
to people without the tool's policy.

The panel opens an agent thread like any conversation (`kind: 'thread'`), but there the
subject's approvers decide the agent's proposals, not the author of a turn, and several
deferred proposals can wait while the thread is idle. The server returns
`thread.decisions` with each open proposal, whether the caller may decide it now and why
not (`assistant-four-eyes-required` for the person who started the work). The store turns
them into `approvalRights`, so every approval card gets its own right: an approver sees
the buttons, the person who started the work reads that another approver decides, and
everyone else waits for an approver. The panel decides a run's proposal with its proposal
hash through `NhAssistantAgentRunApiService` (`POST approvals/{id}/decisions`) and reloads
the thread; a proposal of a person's own turn in the thread still resumes that turn as a
stream. A message a trigger wrote (`authorKind: 'trigger'`) reads as an event instead of
a participant. A custom layout passes `[approvalRights]="store.approvalRights()"` to
`nh-assistant-thread` and handles `(approvalChoice)` with
`store.decide(choice.decision, reason, choice.approvalId)`.

Rejecting an agent proposal stays one click; only afterwards may the person say what was
wrong. `nh-assistant-proposal-feedback` asks for a category from
`NH_ASSISTANT_FEEDBACK_CATEGORIES` and an optional text, sends one proposal with
`NhAssistantAgentRunApiService.giveFeedback` and proposals rejected together with
`giveFeedbackMany`, and can always be skipped. `nh-assistant-agent-proposals` shows it
above the list after a rejection, also when the inbox drops the rejected proposal, and
offers "Give feedback" or "Change feedback" on closed proposals whose `canGiveFeedback`
the server set; each person sees only their own `feedback`. In the panel, the store
remembers the proposals the user rejected in this session, `approvalRights` gives their
cards `canGiveFeedback`, and the approval card offers the same question behind a
low-key "What was wrong?" button; plain chat approvals never ask. Administrators review
the feedback per agent with `nh-assistant-agent-feedback-report` of
`@newheap/platform-ai-chat/admin`, also reachable from each agent in the administration
page: rejection rates and categories per tool and agent version, the texts and a JSON
Lines export through `NhAssistantAdminApiService`. The same page pauses, stops and resumes
an agent and each skill, puts them in shadow or live or lets the code decide again
(`setAgentShadow`, `setSkillShadow`), and replaces the technical account of an agent that
answers to one (`setServiceAccount`), each with an optional reason; a thread view marks a
shadow thread, which only administrators reach.

Each agent on that page also opens its run configuration
(`NhAssistantAgentRunConfigurationComponent`). Skills and triggers in code are read only;
skills, triggers and, for agents created in the administration, run settings are data,
made only of the names `getRunCatalog` lists: trigger events, subject sets, rule handlers,
approval policies and profiles. Every choice is a searchable picker that shows a name with what it
means, approval policies with a sentence built from `approvalPolicySummaries`, and the technical
account is found by name through `searchAccounts` when the catalog reports `accountSearch`
(otherwise by its id, with the name `getAccounts` knows). The editor of a new agent asks how it is
used: in the chat, on its own (`offeredInChat: false`, kept out of the agent picker) or both; an
agent that works on its own continues in its run configuration's guided setup, which asks when it
starts, what it does, on whose behalf, who approves and its limits, and saves run settings, a skill
and a trigger in shadow or puts them live at once. Run settings read as questions with the rarely
changed options under "Advanced". A skill's tool choice lists the agent's tool selectors with the
tool's description or how many tools a pattern matches (pass the catalog with `tools`), and the
reason of every change is a note for other administrators that the agent never reads. Run settings name who the agent answers to (the person
responsible for the subject, a technical account or the agent itself); grants stay in
code, so in runs without a person the agent uses what that account may use, within its
tools, and an agent that answers to itself can only propose. Skills and triggers wait
for run settings on such an agent. A new skill or trigger starts
in shadow. A change of a skill runs in shadow by default as the skill's candidate, next to
the live version, until the administrator compares, promotes or discards it
(`compareSkillCandidate`, `promoteSkillCandidate`, `discardSkillCandidate`); choosing live
replaces the live version at once. Saves and removals send the version they read and an
optional reason. The forms check required fields and numbers before sending; an
`assistant-validation` refusal carries the refused fields in `fieldErrors` of `NhAssistantError`, by
camelCase field name. Errors appear under their field, a form-level message next to the save button
and the first invalid field receives the focus; a failed action of a skill, trigger or agent shows
its error at that item. Settings that no longer fit the application, such as a removed rule
handler, are listed above the skills with the server's problem; they stay out of effect
until they are fixed.

The agent-run components refresh themselves: `NhAssistantLiveService.agentWork$` emits the
hub's `AgentWorkChanged` for every run, proposal or feedback change on a subject the user
belongs to, and the inbox, the thread view and the open panel session reload after a short
pause. Tool calls of proposals that closed without running read `not-executed`, and
proposals and the report show names (`decidedByDisplayName`, `authorDisplayName`) with the
id as fallback.

Show agent work on the application's own pages. Put `nh-assistant-subject-badge` on every row
of a list: badges on one page share one `POST subjects/{type}/summary` per tick through
`NhAssistantSubjectSummaryService` and refresh on agent-work updates of their subject. On a
detail page, load `nh-assistant-subject-suggestions` from `@newheap/platform-ai-chat/agent-runs`
lazily (for example in an `@defer` block) with the subject and the agent whose conversation the
page offers: it shows the open proposals of every agent with the viewer's own decision rights,
the feedback question after a rejection, and opens the conversation through
`NhAssistantPanelService.openSubject`, which creates the agent's thread without starting a run.

Live updates connect to the hub path the server reports in `status.collaboration`,
using `@microsoft/signalr`, loaded on first use, and the same `getAccessToken`. When
a proxy serves the API below a prefix, set `hubBaseUrl` to that prefix (for example
`'/api'`), as for the NewHeap background operation hub. Set `liveUpdates: false` to
reload snapshots instead. When the hub's access policy needs the active division, return
it from `getHubQueryParameters` (`() => ({ divisionId })`): the NewHeap JWT handler copies
`divisionId` to `X-NH-ActiveDivisionId` on `/hub` paths, so WebSockets pass the policy
instead of falling back to long polling after refused attempts. The getter runs in the
assistant's injection context each time the connection starts; `access_token`, `id` and
`negotiateVersion` stay under the control of the library and SignalR.

For desktop notifications, copy `node_modules/@newheap/platform-ai-chat/push/nh-assistant-push-worker.js`
into the application's assets (for example to `/nh-assistant/`) and pass
`push: { serviceWorkerUrl: '/nh-assistant/nh-assistant-push-worker.js', openUrl }`.
The worker registers with its own scope, never controls pages and therefore coexists
with an application service worker. Notifications are on by default: the browser
asks for permission on the next message sent. The header bell turns them off or on,
is hidden when unavailable, and shows off or blocked states in its icon and tooltip.
The preferences view also explains a blocked permission. The worker shows a notification only while no
window of the application is focused, and a click opens the conversation in an open
window or through `#nh-assistant-conversation=<id>`.

Test hosts and demos with `provideNhAssistantMockApi(script)` from
`@newheap/platform-ai-chat/testing`, registered after `provideNhAssistant` in the
same injector. The mock plays scripted turns as contract events over a real
event stream, including approvals, cancellation and the disabled flag, and serves
the preference and administration endpoints with the same rules as the server, so
the panel and the administration page are exercised without a model or assistant
back-end. Give the administration scenario a `runCatalog` with the building blocks the
application registers, `codeTriggers` for its triggers in code and `accounts` for the account
search, so the run configuration offers the same names as the server; the mock refuses other names
with field errors, accepts only those accounts when there are some and treats an account id
starting with `unknown` as one that may not sign in. The mock also offers sharing, read state and in-memory live updates through
`NhAssistantMockLiveService`; `NhAssistantMockBackend.simulateParticipantTurn` lets a
colleague continue a conversation in a demo or test. A route may host its own assistant scope with route-level providers; put
the administration route under the same parent to share that scope.

## Avoid

- Sending the assistant requests through host `HttpClient` interceptors or
  `EventSource`; the library streams through `fetch` with the token from
  `getAccessToken`.
- Registering `provideNhAssistant` in feature components or placing more than one
  panel in the same injector scope.
- Showing the launcher from a host-side permission check alone; the server status
  and the access policy together decide visibility.
- Rendering model text or previews with `bypassSecurityTrustHtml` or custom
  `innerHTML`; use the thread component, which renders Markdown through a strict
  DOMPurify allow-list and shows inline HTML as text.
- Displaying raw server messages. Show the translated `messageKey` of an `error`
  event and fall back to the code.
- Leaving a completed turn unexplained in a custom layout. Render
  `NhAssistantStore.notice` as a dismissible, non-blocking notice: it carries the
  `errorCode` of a `completed` turn (for example `assistant-tool-call-limit-reached`)
  or `assistant-no-answer` when the turn produced no answer text. Only `failed`
  turns use the blocking `error`.
- Deciding approvals outside the approval card or without the proposal hash of
  the pending approval.
- Offering an agent thread's proposals to the author of a turn, or to everyone with a role
  on the subject, instead of following `thread.decisions`; deciding a run's proposal
  through the conversation's `decide` stream.
- Listing every tool call as its own card in a custom thread, or folding an approval
  card into a collapsed tool group.
- Disabling the message editor while a turn runs, clearing a draft when blocked Enter
  is pressed, or stealing focus after the user moved to another control.
- Showing `approval.summary`, raw scope ids or argument JSON as the primary approval
  explanation.
- Replacing library translations wholesale in the host files; override single
  keys only.
- Putting permissions, tokens, personal data or free text from forms into the page
  context, or treating it as authorization; send route, title and entity references
  only.
- Hiding the page-context icon or removing its tooltip and opt-out action.
- Putting an access token, page context or another user's identity in
  `getStateScope`, or sharing a scope between the live API and a mock playground.
- Importing `NhAssistantAdminComponent` in an eagerly loaded module or showing the
  administration link from a host permission check alone.
- Displaying, logging or prefilling an MCP server secret. Omit `secret` to keep it,
  send `""` to clear it, and send a new value only when the administrator replaces
  it.
- Enabling remote MCP tools by default or deriving their effect from remote
  annotations such as `readOnlyHint`; they are untrusted hints.
- Offering background runs with the server's own credential (`headlessUse: 'organization'`)
  for a server that forwards the user's token; it only offers `personal`, which needs the
  person's consent in `nh-assistant-mcp-connections`.
- Sending a stored MCP secret and a `secretReference` together; the editor sends one of
  them, and a new one replaces the other.
- Retrying an administration save after `assistant-version-conflict` without
  reloading the latest version.
- Offering free text for events, subject sets, rules, policies or profiles in a custom run
  configuration screen, or sending a skill change with `apply: 'live'` unless the
  administrator chose live over a candidate in shadow.
- Asking administrators for an account's GUID when the catalog reports `accountSearch`, or showing
  a refused save only at the top of the page instead of at its fields and next to the save button.
- Offering an agent that only works on its own in the chat; keep it out with `offeredInChat: false`
  or `HideFromChat()`.
- Blocking new conversations while a turn runs, or rebuilding a second status list
  next to `activity`, `runningCount` and `unreadCount`.
- Marking conversations as read on arrival instead of when the user sees them.
- Registering the push worker at the root scope next to an application service worker,
  requesting notification permission without a user action, or showing a notification
  while the user looks at the application.
- Sharing the invitation token in logs, analytics or a URL query string; keep it in the
  fragment.
- Overriding `Authorization` or other library headers through `getRequestHeaders`, or
  sending secrets in extra headers.
- Relying on `getRequestHeaders` alone for a hub whose policy needs a header value, or
  returning a token or another secret from `getHubQueryParameters`; URLs appear in
  server and proxy logs.
- Hiding proposals or runs in the host based on its own role checks instead of the
  subject role the server reports, or showing the content of a restricted result.
- Asking for a reason before an approver may reject a proposal, making the feedback
  question required, or showing other people's feedback texts outside the administrators'
  report.
- Requesting agent work once per list row, or importing the `agent-runs` entry point into an
  eagerly loaded page.

## Verification

Run the library tests headless (`npm run nh-ai-chat:test -- --browsers=ChromeHeadless`):
the SSE parser handles fragmented chunks, keep-alive comments and terminal events;
the store turns a contract event sequence into the expected messages, tool calls
and approvals and sends the proposal hash exactly once, and a completed turn with
an error code or without answer text sets a notice while the conversation stays
usable; the launcher disappears for
`enabled: false` and a denying policy; model text with a script tag renders no
script; `en` and `nl` have identical keys and a regional language such as `nl-NL`
receives the `nl` texts; and the mock emits exactly the contract
event fields. The preference tests save, validate the 1,000-character limit and show
failures; the administration tests save context versions and report conflicts,
create, disable, reset and delete agents after confirmation, put an agent and a skill in
shadow and back and replace and restore a technical account, never render a stored
secret and send it only on replace or clear, ask a user name for `basic`, send a
`secretReference` instead of a secret and show the server's reference, certificate and
fallback transport, offer `personal` only for a forwarded token, keep synced MCP tools
disabled as changes, show remote hints as hints and warn after a remote schema change. The
preference tests also allow and withdraw background use of a personal server. The run
configuration tests give an administration agent run settings, show refused fields and an
account that may not sign in, add a skill that starts in shadow and a trigger that is put
live, keep the run settings while skills or triggers remain, keep skills and triggers in
code read only, and save a skill change as a candidate that is compared and promoted. They also
show errors at their fields and next to the actions before and after a refused save, choose the
technical account by name, and create an agent that works on its own through the guided setup, kept
out of the chat; the picker tests cover the combobox keys, filtering and the server search. The
page-context tests send the getter result read in the injection context, truncate
long fields, keep sending when the getter throws, and send `null` after the user
removes the chip. The UI-state tests restart the store, verify scoped restoration
and inaccessible-conversation fallback, and check that restored chats send only
the current page hint. In the
host, open the assistant playground and walk through a new conversation, a
streamed answer, a tool call, approve, reject, stop, an error, an agent switch,
the page-context tooltip and opt-out on a simulated project page, the preferences and the
administration tabs at desktop and mobile width in light
and dark mode. The collaboration tests run two conversations at once, mark only a
viewed conversation as read, show a colleague's turn live with a name, refuse a
decision about another participant's approval, create, revoke and join links, invite
from the directory and forget a conversation removed elsewhere (SPM-265). In the
playground, start a second prompt while the first still answers, watch the chip row
and the launcher badge, share the conversation and let the simulated colleague
continue it. The agent-run API tests call every agent-run endpoint with the documented
method and path, bind a decision to the proposal hash and report a four-eyes refusal
with its code; the transport tests send the extra headers without letting them replace
the library's own, and the live tests put `getHubQueryParameters` on the hub URL without
letting them replace `access_token`. The store tests offer each open proposal of an agent
thread to the approvers the server names, decide a run's proposal once with its hash
through the agent-run endpoint and reload the thread, and show a refused decision; the
thread tests label a trigger message as an event and give each approval card its own
right. The agent-run component tests offer decisions only to people who may
decide, approve the proposal shown, resolve an unknown outcome without running it again,
start the agent with the trimmed message and drop the start notice once the run ended.
In the management portal, sign in as the project lead, open the
intake reviews page, start the reviewer for a project and follow its run, memory and
files, and decide a waiting proposal from the inbox. Let a manager create a project,
add the lead as approver on the project members page, open the thread in the assistant
panel as the lead and approve the proposal there; confirm that the hub connects over
WebSockets (SPM-277). The feedback tests keep sending disabled until a category is chosen,
send one proposal through `PUT` and several through the bulk endpoint, offer the question
after a rejection and on closed proposals the caller may comment on, and give the panel's
approval card the question only for proposals the user rejected; the report tests load
the summary, list and export with the chosen period and filters. Reject a proposal on the
intake reviews page, answer the question, and open the report as an administrator
(SPM-279). Open the project directory and assert one summary request for all badges, open a
project's dialog and decide or reject its suggestions there, and open the reviewer's
conversation from it (SPM-280). In the assistant playground's administration, open the
project planner's run configuration, add a skill by the progress-note rules and see it in
shadow next to the skills in code (SPM-295). In the portal's real assistant administration, sign in
as the project lead and open the project starter's run configuration: the account by name, the
policy explained, the kickoff skill and its live trigger (SPM-298). SPM-248, SPM-253, SPM-265,
SPM-277, SPM-279, SPM-280, SPM-295 and SPM-298 are the executable references.
