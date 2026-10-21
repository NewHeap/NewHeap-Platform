# v-next

## NewHeap.Platform.AspNet.Common: reconciliation finishes a cancellation whose worker stopped

Fix. An operation that was being cancelled while an attempt held it (`CancelRequested` with a
current attempt) stayed `CancelRequested` for good when that attempt's worker stopped: only the
worker finishes such a cancellation, and reconciliation recovered stale `Running` attempts only.
The readiness check reported it as a stale attempt that an operator had to resolve. Reconciliation
now recovers it by the same definition the check counts by: once the attempt's heartbeat is older
than `StaleAttemptTimeout` on the system clock, it takes the operation's transaction lock, reads it
again, abandons the attempt (`stale-heartbeat`) and fences it off with a new dispatch generation,
releases the leases the attempt held and ends the operation `Cancelled` (failure code `cancelled`,
root step cancelled) with the `background-operation.cancelled` milestone, its notification
projection and live update, after an operator-only `background-operation.stale-attempt-recovered`
event. A cancelled child reaches its fan-out parent as from the worker; a cancelled parent with
children waits without an attempt until they ended and is then cancelled by the fan-out
coordinator, as before. A cancellation whose worker still sends heartbeats is left to that worker.

| Breaking change | Required action |
|---|---|
| A `CancelRequested` operation whose attempt's heartbeat is older than `StaleAttemptTimeout` now ends `Cancelled` through reconciliation; late writes of the stopped attempt fail on fencing, and its leases are free at once. | None. Drop operator procedures that finished such cancellations by hand. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: runs whose background operation ended without them

Fix. A run that waited inline for a decision stayed `waiting-for-approval` for good, and kept its
thread waiting so that the agent's next runs stayed busy, when its background operation ended
without the run's handler: an operator cancelled it in the background-operation administration or
a `Supersede` trigger cancelled it, reconciliation failed it as `dispatch-stalled`, or a worker
failed it before the handler ran, for example on a payload schema mismatch or a redacted payload.
A queued or running run whose operation failed, timed out or succeeded without ending it stayed
open in the same way. The maintenance job now checks open runs against their operations, a
bounded page per round, and ends a run once its operation has been terminal for the background
operations' `StaleAttemptTimeout` on the system clock. It ends the run through the same end as the
run's operation, once: summary, `AgentRunEnded`, work update, hand-off follow-ups and, for a failed
run, `RunFailed`. Its pending inline proposals are closed first, `expired` when their lifetime
passed and `withdrawn` otherwise, and the thread's waiting turn is released. A run that a person,
the kill switch or subject retention cancels while it waits inline (`POST runs/{id}/cancel`,
`StopAsync`) now releases that turn as well; the thread used to keep waiting for it.

| Breaking change | Required action |
|---|---|
| A run that has not ended while its background operation ended now ends through the maintenance job: `cancelled` (class `cancelled`, `assistant-run-operation-cancelled`) for a cancelled operation, otherwise `failed` (class `internal`) with `assistant-run-operation-stalled` for `dispatch-stalled` or `assistant-run-operation-ended`. | Show the new codes where you show run failures with your own texts. |
| Cancelling a run that waits inline returns its thread to `idle`, so the agent's next run starts. | None. |
| `NhAssistantErrorCodes.RunOperationCancelled`, `RunOperationStalled` and `RunOperationEnded` are new. | None. |

## NewHeap.Platform.AI.Chat.AspNet: busy runs keep their retries, and ended runs close cleanly

Fix. A run whose thread a person's turn held, or whose agent already had `maxConcurrentRuns` runs
going, spent one of its operation's retries each time it tried, so after three tries its operation
failed and the maintenance job ended the run with `assistant-run-operation-ended`. Such a run now
gives way as the background operations do for a busy resource: the attempt is abandoned without
spending a retry and the operation is due again after 15 seconds (busy thread) or 30 seconds (full
agent) on the application's clock, twice as long after every attempt that gave way, at most five
minutes. `Retry` counts only attempts that failed, so attempts that waited for an inline decision
no longer count either. When the maintenance job ends a run whose operation ended, an inline
proposal that a person approved after the operation ended, and that never executed, is now
`withdrawn` with the run's failure code as its result, once and with its `AgentWorkChanged` update;
it used to stay `approved`. Resuming such a run (`POST runs/{id}/retry` with `mode: "resume"`) no
longer goes back into the wait the run gave up: the run drops it and starts a new turn in the
thread. A running or waiting run whose operation the cleanup of ended operations removed now fails
with `assistant-run-operation-ended` once the run started (or was created) at least the stale
timeout ago on the system clock; it stayed open. The job pages open runs by creation time and id, so runs created at the same moment at a page
boundary are checked in the same pass.

| Breaking change | Required action |
|---|---|
| A run that finds its thread busy or its agent full is retried until it can start, its operation `RetryScheduled` with failure code `concurrency-busy`, instead of failing after three tries. | None. Look for `concurrency-busy` instead of `conversation-busy` or `assistant-agent-concurrency` on the operation. |
| An inline proposal approved after its run's operation ended is `withdrawn` (result code the run's failure code) when the maintenance job ends the run. | None. |
| Resuming a run that the maintenance job ended while it waited inline starts a new turn instead of waiting for the old proposal. | None. |

## @newheap/platform-ai-chat: runs whose background operation ended without them

The bundled English and Dutch texts and `NH_ASSISTANT_SERVER_ERROR_CODES` include the failure codes
of a run that the maintenance job ended because its background operation ended without it:
`assistant-run-operation-cancelled`, `assistant-run-operation-stalled` and
`assistant-run-operation-ended`.

| Breaking change | Required action |
|---|---|
| Three new error codes with texts under `nh-assistant.errors`. | Add the three keys to translations a host provides itself (`translations: 'host'`). |

## NewHeap.Platform.AI.Chat.AspNet: an expired inline proposal ends its run by its policy

Fix. When an inline proposal expired before anyone decided, the run that waited for it ended
`completed` with `assistant-approval-expired` whenever its background operation woke before the
maintenance job expired the proposal, although the approval policy said
`OnExpiry(NhAiApprovalExpiryAction.FailRun)`. When the job came first, it set the run to `failed`
without an `AgentRunEnded` event, a summary or a notification, and the run's operation then left
the waiting turn alone, so the thread stayed `waiting-for-approval` and blocked its next runs. The
run's own operation now ends a run that waits inline in both orders, by the proposal's policy:
`FailRun` fails it with the failure class `policy` and notifies like any failed run, `Withdraw` and
`Escalate` (an inline proposal never escalates) let it complete with `assistant-approval-expired`.
The maintenance job expires an inline proposal but leaves its run to the operation, whose wait
expires at the same time on the same clock; it still fails the run of a deferred proposal under
`FailRun`. Whichever expires the proposal publishes its `AgentWorkChanged` update, once.

| Breaking change | Required action |
|---|---|
| A run whose inline proposal expires under `FailRun` now always ends `failed` (class `policy`) with `assistant-approval-expired`, sends `RunFailed`, and its background operation ends `Failed` with that code instead of `Succeeded`. | None. Assert a run's status after an inline expiry by its policy, not only its failure code. |

## NewHeap.Platform.AspNet.Common: a readiness check that does not count work before it is due

Fix. The `nh-background-operations` readiness check counted a `PendingDispatch`, `RetryScheduled` or
`WaitingForChildren` operation as overdue dispatch once its last change was `StaleAttemptTimeout`
old (two minutes by default, or ten `DispatchInterval`s when that is longer), also while its due
time still lay ahead: a trigger debounce of more than two minutes, a long retry backoff or a fan-out
parent with a recheck interval (`ReconciliationInterval`) of more than two minutes made a healthy
application degraded. It counted a signal wait by its expiry on the application's clock alone, so a
forward jump of a registered `TimeProvider` degraded it as well. The check now uses the
reconciler's definition of a stalled dispatch for every status that waits for a due time: due for
`StaleAttemptTimeout` on the application's clock (an operation without a due time counts as due)
and unchanged for as long on the system clock. A queued operation that no worker started is
overdue by its last change, and an attempt is stale by its heartbeat, as before. Reconciliation now
treats a pending or retry-scheduled operation without a due time as due, as the dispatcher does, so
the check and reconciliation agree on when a pending dispatch has stalled. Each count in the
check's data stops at 1,000.

| Breaking change | Required action |
|---|---|
| Debounced, backed-off, signal-waiting and fan-out work that is not due, and a jump of the application's clock, no longer degrade the readiness check; work is overdue after `StaleAttemptTimeout` instead of the longer of that and ten `DispatchInterval`s, and `staleAttempts` and `overdueDispatches` stop at 1,000. | None. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: proposal lifetimes on the application's clock

A proposal's lifetime now follows the application's clock: a registered `TimeProvider`, otherwise
the system clock as before. The assistant takes a proposal's `GeneratedAt` and expiry
(`NhAssistantLimits.ApprovalLifetime` or the approval policy's `Lifetime`), its reminders and
escalation and the `DecidedAt` of every decision from that clock, and every check whether a
proposal expired reads it: the conversation's `expired` status, the inbox, the thread and subject
lists, a person's or an approver's decision, the maintenance job, the rejection cooldown and the
windows over recent decisions. `NhAiToolInvoker` validates approval evidence at the same clock's
time; `AddNewHeapPlatformAI` passes it a registered `TimeProvider` through a new constructor. A run
that waits inline for a decision already waited on that clock, so its wait and its proposal now
expire together. `CreatedAt`, which proposal guards receive as `ProposedAt`, executions, tool calls
and audit events keep the system clock. A test registers a clock and moves it past a lifetime
instead of waiting or shortening the lifetime.

| Breaking change | Required action |
|---|---|
| With a registered `TimeProvider` that is not the system clock, proposals expire, remind and escalate by that clock, decisions record its time as `DecidedAt`, and `NhAiToolInvoker` validates every approval's time window at its time. | Take an `NhAiProposal`'s `GeneratedAt` and `ExpiresAt` and an `NhAiApproval`'s `ApprovedAt` and `ExpiresAt` from the same `TimeProvider`, and move a test clock forward to let a proposal expire. |
| `NhAiToolInvoker` has a public constructor with a `TimeProvider`. | None; the existing constructors keep the system clock. |

## NewHeap.Platform.AI.Chat.AspNet: schedule ticks on the application's clock

The minute a scheduled trigger's tick belongs to now comes from the application's clock: a
registered `TimeProvider`, otherwise the system clock as before. Hangfire still fires the cron on
real time; only the deduplication of a tick follows the application's clock, so a test moves its
clock to the next minute to tick again instead of waiting for the wall clock.

| Breaking change | Required action |
|---|---|
| With a registered `TimeProvider` that is not the system clock, a tick is deduplicated by that clock's minute; a clock that stands still counts every later tick as the same minute and starts nothing. | Move a test clock to the next minute before a tick that must start runs again. |

## NewHeap.Platform.AspNet.Common and NewHeap.Platform.AI.Chat.AspNet: due times on the application's clock

When a background operation becomes due now follows the application's clock: a registered
`TimeProvider`, otherwise the system clock as before. The dispatcher and reconciliation compare
`NhBackgroundOperationEnqueueOptions.NotBefore`, retry and contention backoff, the expiry of
`WaitForSignalAsync` and fan-out rechecks with it, and agent triggers start their debounce from it,
including the 30-second debounce of proposal follow-up runs. Leases, heartbeats, stale-attempt
recovery and audit timestamps keep the system clock. A test can therefore register a clock and move
it past a debounce or retry instead of waiting, without taking work away from a running attempt.
Reconciliation takes an unclaimed `PendingDispatch` operation for a stalled dispatch only when it has
been due for `StaleAttemptTimeout` on the application's clock and unchanged for as long on the system
clock, so a forward jump of the application's clock never fails work that just became due.
`NhBackgroundOperationService` gained a constructor that takes the `TimeProvider`; dependency
injection uses it when one is registered.

| Breaking change | Required action |
|---|---|
| With a registered `TimeProvider` that is not the system clock, operations become due, retry and expire by that clock; a test clock that stands still holds debounced, retried and expiring work back. | Take `NotBefore` and signal-wait expiry from the same `TimeProvider`, and move a test clock forward where work should become due. |
| `NhBackgroundOperationService` has a fourth public constructor, with a `TimeProvider`. | None; the existing constructors keep the system clock. |
| A pending dispatch counts as stalled only after `StaleAttemptTimeout` without a change on the system clock as well; with the system clock this changes nothing, because an operation is changed when it becomes pending. | None; a test that moves its clock forward no longer causes dispatch recoveries or `dispatch-stalled` failures. |

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: finding actors by name

`INhActorSearch` finds actors whose name or e-mail address contains a query
(`SearchAsync(NhActorSearchRequest)`, at least two characters, at most `Limit` results, users by
default). NewHeap Identity registers a search over its users that ignores case and leaves out
locked-out users; an application with its own identity model registers its own with
`AddNewHeapActorSearch<T>()`, which NewHeap Identity does not replace. Without a search, modules ask
for an actor's id. The assistant's administration uses it to choose the account an agent answers to.

| Breaking change | Required action |
|---|---|
| None; the interface and its registration are new. | Register `AddNewHeapActorSearch<T>()` in an application without NewHeap Identity to offer accounts by name. |

## NewHeap.Platform.Common and NewHeap.Platform.AspNet.Common: secrets that fail closed

A `${Secrets:…}` reference in appsettings whose secret is missing from the secrets file stays as
literal text. For the JWT signing key that text, which anyone can read in appsettings, became the key
tokens were signed and validated with. `NhJwtSigningKey` now refuses a missing key, an unresolved
reference and a key shorter than 32 bytes, so `AddNewHeapPlatformAspNetCommon` (and
`ConfigureNhJwtBearerValidationOptions` and `NhAuthenticationService`) stop with a message that names
the setting, never its value. The key is read from the setting itself or else from `Secrets:` in the
secrets file.

`NhConfigurationSecrets.Read(configuration, key)` reads any secret the same way and returns `null` for a
blank value or an unresolved reference; `IsUnresolvedReference` and `FindUnresolvedReferences` (keys
only) are available for other checks. `AddNewHeapPlatformCommon` registers a startup check that logs a
warning naming every setting that still holds an unresolved reference. Webhook triggers of
`NewHeap.Platform.AI.Chat.AspNet` refuse a secret that is still such a reference, as they refuse one
shorter than 32 characters.

| Breaking change | Required action |
|---|---|
| The application no longer starts when the JWT signing key is missing, an unresolved `${Secrets:…}` reference or shorter than 32 bytes. | Set a random key of at least 32 bytes, for example 64 random bytes in Base64, as `NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key` in the secrets file, and replace every token signed with the old value. |
| A webhook trigger whose secret is an unresolved reference refuses its calls. | Configure the secret. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: agents outside the chat, accounts by name and agents created by applications

An agent that only works on its own can be kept out of the chat: `ConfigureAgent(id, run =>
run.HideFromChat())` in code, or `offeredInChat: false` on `POST` and `PUT admin/agents` for an agent
created in the administration (`OfferedInChat` on `NhAssistantAdminAgentInputDto`; omitted keeps it
on update, a new agent is offered). It is left out of the status and `GET agents` lists, and `POST
conversations` answers `assistant-agent-not-found` for it; its triggers, runs, subject threads,
consultations and existing conversations keep working. `NhAssistantAdminAgentDto` reports
`OfferedInChat`.

The run catalog explains its choices: `ApprovalPolicySummaries` gives per approval policy who
decides (the initiator, a role on the subject or an authorization policy), how long a proposal
waits, four eyes and whether the run waits (`NhAssistantApprovalPolicySummaryDto`,
`NhAssistantApproverSummaryDto`), and `AccountSearch` tells whether accounts can be found by name.
`GET admin/accounts?query=` searches them through `INhActorSearch` (`NhAssistantAccountDto`; without a
search `assistant-account-search-unavailable`), and `GET admin/accounts?ids=` returns the names the
application knows for stored account ids.

`INhAssistantAgentConfiguration.CreateAgentAsync(NhAssistantAgentRequest, actorId)` creates an agent
the way an administrator does, for example from a package import; it then gets run settings, skills
and triggers as data as before.

Apply the `AgentChatAvailability` migration of `NewHeap.Platform.AI.Chat.SqlServer` or
`NewHeap.Platform.AI.Chat.PostgreSql`: it adds `HiddenFromChat` to `AssistantAgent`. Existing agents
stay offered in the chat.

| Breaking change | Required action |
|---|---|
| Database schema change. | Apply the `AgentChatAvailability` migration. |
| `INhAssistantAgentConfiguration` has the new member `CreateAgentAsync`. | Implement it in your own implementations and test doubles. |
| `NhAssistantAdminAgentInputDto` has the new optional parameter `OfferedInChat`. | Recompile code that constructs the record. |

## @newheap/platform-ai-chat: a run configuration that explains itself

The run configuration of `@newheap/platform-ai-chat/admin` was reworked after a review in a consuming
application:

- Every catalog choice is a searchable picker that shows a name with what it means; approval policies
  read as a sentence built from `approvalPolicySummaries`. The technical account is found by name
  (`NhAssistantAdminApiService.searchAccounts`, `getAccounts`) when the catalog reports
  `accountSearch`, and otherwise entered by id with the name shown next to it.
- Run settings are grouped as questions (on whose behalf, how it works, who approves, limits) with
  the rarely changed options under "Advanced".
- Forms check required fields and numbers before sending. Errors appear under their field, a
  form-level message appears next to the save button, the first invalid field gets the focus, and a
  failed action of a skill, trigger or agent shows its error at that item.
- An agent created in the administration that only chats is offered a guided setup that asks when
  it starts, what it does, on whose behalf, who approves and its limits, and saves run settings, a
  skill and a trigger in shadow or puts them live at once.
- The agent editor asks how a new agent is used: in the chat, on its own (`offeredInChat: false`)
  or both; a new agent that works on its own continues in that guided setup. The agent list shows
  "Not in the chat", names technical accounts and translates the names of agents in code.
- Number fields, time zones and the other controls share the administration's styling, and the
  forms keep to the width of their host.
- Names and descriptions of agents in code are translated everywhere in the administration; the
  agent editor shows the translations and sends the keys back unless the administrator changes the
  text. Skills show a readable name with their id and a translated description when the host has
  `nh-assistant.agents.<agentId>.skills.<skillId>.name` or `.description`; the policy an agent
  requires reads from `nh-assistant.policies.<policy>`, otherwise as it is named in code. People who
  changed something are shown by name.
- A skill's tool choice shows each of the agent's tool selectors with the tool's description or how
  many tools a pattern matches (`NhAssistantAgentRunConfigurationComponent` takes the catalog as
  `tools`) and says where an agent started by a trigger writes its answer.
- The reason of a change is labelled as a note for other administrators that the agent does not
  read, and confirmations offer "Cancel" instead of "Keep".
- An agent created in the administration, and its skills, return to their own setting instead of
  offering "Follow the code", and the confirmation says which mode that setting is.

`AdminAgent` and `AdminAgentInput` gain `offeredInChat`, `RunCatalog` gains `approvalPolicySummaries`
and `accountSearch`, and `AssistantAccount`, `ApprovalPolicySummary` and `ApproverSummary` are new.
The mock API of `@newheap/platform-ai-chat/testing` serves `admin/accounts` from the scenario's new
`admin.accounts`, policy summaries and chat availability. The new texts are bundled in English and
Dutch, including `assistant-account-search-unavailable`.

| Breaking change | Required action |
|---|---|
| The run configuration's markup changed: selects became pickers and the forms moved into child components. | Update tests and styles that target the old selects or element ids of the run configuration. |
| `NhAssistantAdminAgentEditorComponent` emits the new `workRequested` instead of `saved` for a new agent that works on its own. | Handle `workRequested` when you host the editor yourself; `NhAssistantAdminAgentsComponent` does. |
| The texts `nh-assistant.admin.keep` and `nh-assistant.admin.agents.state.reason-label` were removed; confirmations use `nh-assistant.admin.cancel` and changes `nh-assistant.admin.agents.state.note-label`. | Move host overrides of the removed texts to the new keys. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: object types with names, entity binding, search and links

A subject type describes itself for people where it is registered:
`AddSubjectType<TResolver>(type => type.Named(new() { ["en"] = "Project", ["nl"] = "Project" },
fallbackLanguage: "en").NamedPlural(...).ForEntity<Project>().OpenAt("/projects/{id}"))`
(`NhAssistantSubjectTypeBuilder`). Names are texts per culture name of at most 100 characters with
one in the fallback language; readers get them in their request language. `ForEntity<T>()` records the
entity's short and full type name like the NewHeap log's `ObjectType` and `ObjectTypeFull`. `OpenAt`
takes a path in the application or an absolute `https` URL with exactly one `{id}`, which the subject
id fills URL-encoded. The parameterless `AddSubjectType<TResolver>()` keeps working.

A resolver that also implements the new `INhAiSubjectSearch` (`SearchAsync(NhAiSubjectSearchRequest)`)
lets people find subjects by name with `GET subjects/{type}/search?query=&limit=` (2 to 100
characters, at most 50 results, `NhAssistantSubjectSearchDto`): the platform keeps the results whose
`ResolveAsync` gives the caller at least the reader role in the caller's tenant. A type without search
answers `404 assistant-subject-search-unavailable`. The run catalog lists the types as `SubjectTypes`
(`NhAssistantSubjectTypeDto`), and `NhAssistantThreadDto`, `NhAssistantProposalDto` (threads, inbox,
lookups), `NhAssistantSubjectWorkDto` and `NhAssistantConversationThreadDto` gain `SubjectTypeName`
and `SubjectLink`.

Subject types that start with `nh-` are reserved for the platform (`NhAiSubjectRef.ReservedTypePrefix`,
`IsReservedType`). No migration is needed.

| Breaking change | Required action |
|---|---|
| Startup fails for a subject type that starts with `nh-`. | Rename the type; threads stored under the old type are not moved. |
| Startup fails when two different resolver classes handle the same subject type; previously the last registration won silently. | Register one resolver per subject type. |
| Startup fails for a subject type configuration whose resolver is no longer registered as `INhAiSubjectAccessResolver`. | Keep the resolver registered or drop the configuration. |

## @newheap/platform-ai-chat: object types with names, search and links

Agent proposals in `nh-assistant-agent-inbox` and `nh-assistant-agent-proposals`, and the heading of
`nh-assistant-agent-thread-view`, show the subject's type name (`subjectTypeName`, such as "Project")
and link the subject's name to `subjectLink`, the server's link from the type's `OpenAt` template. A
link within the application navigates through the router; an `https` link leaves it; anything else
shows no link. A host whose routes differ replaces the link per subject type with
`provideNhAssistant({ subjectLinks: { project: subject => ['/projects', subject.id] } })`
(`NhAssistantSubjectLinkBuilder`, `NhAssistantSubjectRef`); `null` hides it and `undefined` keeps the
server's link.

`NhAssistantAgentRunApiService.searchSubjects(type, query, limit?)` finds the subjects of one type
that the caller may read (`AgentSubjectSearch`, `AgentSubjectSearchResult`,
`NH_ASSISTANT_SUBJECT_SEARCH`). `RunCatalog` gains `subjectTypes` (`AgentSubjectType`), and
`AgentThread`, `AgentProposal`, `AgentSubjectWork` and `ConversationThread` gain the optional
`subjectTypeName` and `subjectLink`. The mock API of `@newheap/platform-ai-chat/testing` serves them
from the scenario's new `subjectTypes` (`NhAssistantMockSubjectType`, `NhAssistantMockSubject`) with
the server's validation. The new texts, including `assistant-subject-search-unavailable`, are bundled
in English and Dutch.

| Breaking change | Required action |
|---|---|
| A proposal card's subject and the thread view's heading render through a link component and may contain a link. | Update tests and styles that read the plain `strong` text or the heading text of these components. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: what an agent works on

An agent says what it works on: `ConfigureAgent(id, run => run.WorksOn("project", "quote"))` names
registered object types (at most 20), `WorksOnAllObjects()` chooses every type on purpose and
`WorksOnApplication()` adds the whole application, the platform's subject type
`NhAiSubjectRef.SystemType` (`nh-system`), for work that concerns no single object. `PerDivision()`,
`RunPolicy(name)` (who may run it for the whole application; by default the administration policy)
and `ViewPolicy(name)` (who sees its results there; by default its required policy) complete the
statement (`NhAssistantAgentWorksOn`, `NhAssistantAgentRunOptions.WorksOn`). An agent that says
nothing works on all object types and not on the application, as before.

Agents created in the administration store the same statement: `NhAssistantAgentRequest`,
`NhAssistantAdminAgentInputDto` and `NhAssistantAdminAgentDto` gain `WorksOnSubjectTypes`,
`WorksOnAllSubjects`, `WorksOnApplication`, `PerDivision`, `RunPolicy` and `ViewPolicy`, sent together
(omitted keeps the statement; a new agent works on all object types), and the DTO adds
`WorksOnDeclared` and `KnownSubjectTypes`, the types an agent is known to work on (its hand-offs,
consultations, typed triggers and threads). Run and view policies are chosen from the policies the
application lists with `AddAgentPolicy(name, description)`; the run catalog shows them as
`AgentPolicies` and reports `Divisions`, which stays `false` until the application lists divisions.
Saving answers with field errors `worksOn`, `worksOnSubjectTypes`, `perDivision`, `runPolicy` and
`viewPolicy`.

One rule enforces the statement: trigger fires (events, schedules, webhooks, follow-ups, requests and
hand-offs), `POST threads/start`, `POST threads/open` for a new thread, hand-offs and consultations on
a type the agent does not work on are refused with the new code `assistant-agent-does-not-work-on`
(`409` from the endpoints, audited as a rejected trigger), and `canStart` reads `false`. Triggers and
trigger events that know their type say so with the new `Subject("project", e => e.ProjectId.ToString())`
overload: startup then checks them, the run catalog shows the type on the event
(`NhAssistantRunBlockDto.SubjectType`), a data trigger on it is refused for an agent that does not work
on the type, and one that no longer fits is out of effect with the problem `not-worked-on`.

| Breaking change | Required action |
|---|---|
| The migration `AgentWorkOnObjects` adds what an agent works on to `AssistantAgent`; existing agents work on all object types, not on the application, app-wide, without policies. | Apply the migrations of `NewHeap.Platform.AI.Chat.SqlServer` or `NewHeap.Platform.AI.Chat.PostgreSql`, or let `RunMigrations` apply them. |
| `NhAssistantAgentRequest` and `NhAssistantAdminAgentInputDto` gained optional constructor parameters. | Recompile code that constructs them; named arguments stay valid. |
| Startup fails for `AcceptHandOffs`, `AllowConsultation` or a typed trigger on a type the agent's `WorksOn` does not list, for an unknown type in `WorksOn`, and for an unregistered run, view or listed agent policy. | Add the type to `WorksOn` or remove the declaration; register the policy. |

## @newheap/platform-ai-chat: what an agent works on

The agent editor asks "What does this agent work on?" for agents created in the administration: all
objects or chosen kinds of objects by the names of the run catalog's `subjectTypes`, the whole
application, per division only when the catalog reports `divisions`, and the run and view policy from
the catalog's new `agentPolicies`. It sends `worksOnSubjectTypes`, `worksOnAllSubjects`,
`worksOnApplication`, `perDivision`, `runPolicy` and `viewPolicy` together and shows the server's field
errors at their fields; `NhAssistantAdminAgentEditorComponent` takes the run catalog as the new input
`catalog`, which `NhAssistantAdminAgentsComponent` passes. The agent cards show what an agent works on;
an agent in code shows its code setting, and one that does not say shows "All objects" with the kinds
it is known to work on (`knownSubjectTypes`). The trigger forms offer an event that names its kind of
object (`RunBlock.subjectType`) only to agents that work on it. `AdminAgentInput`, `AdminAgent`,
`RunCatalog` and `RunBlock` gain the optional fields; the mock API checks the statement as the server
does and the scenario's admin agents accept `worksOnDeclared` and `knownSubjectTypes`. The texts,
including `assistant-agent-does-not-work-on`, are bundled in English and Dutch.

| Breaking change | Required action |
|---|---|
| The agent editor has a works-on section with radio buttons, check boxes and two policy selects after the use choice, and agent cards have a works-on line. | Update tests and styles that select the editor's controls or the card's paragraphs by position. |
| The editor of an agent created in the administration sends what it works on with every save. | Pass the run catalog to `NhAssistantAdminAgentEditorComponent` with `catalog` when you host it yourself, so administrators can choose kinds of objects and policies. |

## NewHeap.Platform.Common, NewHeap.Platform.AspNet.Common, NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the whole application as a scope

Agents run for the whole application on the platform's own subject type `nh-system`, which the
assistant registers itself: `NhAiSystemSubjects.Global` (`nh-system/global`) for an agent that works
app-wide and `NhAiSystemSubjects.ForDivision(id)` (`nh-system/division-{id:N}`) for one that works per
division. Threads, memory, artifacts, run timelines, retention and budgets work as for any subject,
per scope (`global` or `division:{id}`). The run catalog lists the type with `IsSystem` and its name
"The whole application" in the reader's language; thread, proposal and work DTOs name it so. A
schedule in code may leave out `Subjects`, and a data schedule its subject set: each tick then starts
one run on `nh-system/global`, or one per listed division (ordered by name, at most
`MaxSubjectsPerTick`) for an agent with `PerDivision()`. Only an agent that works on the application
may have such a schedule. A run for the whole application has no subject execution scope; in a
division the division is its execution scope.

People who pass the read policy read the whole application (by default the assistant's access
policy) and people who pass the approve policy (by default the administration policy) take part in
it and decide its proposals; configure both with
`UseSystemSubject(system => system.ReadPolicy("…").ApprovePolicy("…"))` (`NhAssistantSystemSubjectBuilder`).
The policies are evaluated for the person as `INhAiPrincipalFactory` rebuilds them, against the
subject with its division; a division subject also needs `INhDivisionAccess`. Who runs an agent by
hand for the whole application is decided by its run policy (see "run an agent now"). The whole
application lists no members, so the approval inbox shows its proposals to their initiator only.

Divisions stay optional. `NewHeap.Platform.Common.Actors` gains `INhDivisionDirectory` and
`NhDivisionInfo`; NewHeap Identity registers a directory over its divisions, and an application lists
its own with `AddNewHeapDivisionDirectory<T>()`. Without one the run catalog reports
`Divisions: false`, data agents cannot work per division and nothing runs per division.
`NhAssistantAgentWorksOn.Covers(NhAiSubjectRef)` also checks the scope of the whole application:
an app-wide agent is refused in a division and a per-division agent app-wide with
`assistant-agent-does-not-work-on`.

| Breaking change | Required action |
|---|---|
| The assistant registers its own `INhAiSubjectAccessResolver` for `nh-system`, so the run catalog's `SubjectTypes` and `INhAiSubjectAccessResolver` enumerations include it. | Filter on `IsSystem` (or the `nh-` prefix) where you list the application's own object types; do not remove the platform's resolver. |
| Startup fails for `PerDivision()` in code when the application lists no divisions, for a schedule without `Subjects` on an agent that does not work on the application, and for an unregistered `UseSystemSubject` policy. | Use NewHeap Identity or `AddNewHeapDivisionDirectory<T>()`, add `WorksOnApplication()` or name the subjects, and register the policy. |
| Saving a data schedule without a subject set no longer answers `subjectSet: required` for an agent that works on the application; it runs for the whole application. | Send a subject set when the schedule should run per object. |
| Tool calls of a run on `nh-system` have no subject execution scope, and a per-division run has the division as its execution scope. | Do not require an `nh-system` execution scope in capability grants or context sources. |

## @newheap/platform-ai-chat: the whole application as a scope

For a schedule of an agent that works on the whole application, the trigger form and the guided setup
ask what it runs for each time: "The whole application" (the default; sent as `subjectSet: null`, once
per division for an agent that works per division) or every object in a subject set. The server's
`subjectSet` refusal shows at that choice, a stored schedule for the whole application of an agent
that no longer works on it asks for a set, and the trigger list names "The whole application" or
"Every division". The editor and the agent cards leave the platform's type `nh-system` (`isSystem`)
out of the kinds of objects, and the subject link shows "The whole application" once instead of the
type name next to the same name. The mock API lists `nh-system` in the run catalog's `subjectTypes`
in English and Dutch, answers its subject work and refuses its search, accepts a schedule without a
subject set only for an agent that works on the application (`subjectSet: required` otherwise), and
marks stored triggers that no longer fit what the agent works on as `inEffect: false` with
`not-worked-on`. New texts are bundled in English and Dutch.

| Breaking change | Required action |
|---|---|
| The run catalog's `subjectTypes`, from the server and the mock API, include the platform's `nh-system` type. | Find types by id or leave out `isSystem` where you list the application's own kinds of objects or take `subjectTypes[0]`. |
| A schedule in the trigger form and the guided setup has a target choice before the subject set, and the set picker shows only for "Every object in a set". | Update tests and styles that select the schedule fields by position or expect the set picker for every schedule. |
| Subject links no longer repeat a type name that equals the subject's name. | Update tests that expect the type span for such subjects. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: run an agent now

People run an agent now, by hand, with `POST threads/start` on an object or on the whole application
(`nh-system/global`, or `nh-system/division-{id:N}` for a per-division agent). One rule decides who
may, for the start, a new thread of `POST threads/open` and every `canStart`: the agent works on the
subject and, for the whole application, its scope; the person passes the agent's required policy; on
an object the person is a participant; on the whole application the agent's `RunPolicy` decides, by
default the administration policy, evaluated like the roles of the whole application, and the person
reads it. `GET subjects/{type}/{id}/agents` (`NhAssistantObjectAgentsDto`, `NhAssistantObjectAgentDto`,
`NhAssistantLatestRunDto`) lists the agents that work on a subject and that the caller may use, with
`CanStart`, the thread people open, the newest run with status, time and the first 300 characters of
its result (until runs keep their full answer, the summary they end with) and the open proposals.
With listed divisions `GET subjects/nh-system/search` finds the divisions a person works in by name.
A headless turn a person asked for uses only that person's own personal MCP connection, with their
consent: a run started by hand, through another assistant's request tool or by a message typed while
a run worked, and every turn that answers such a message, also in a scheduled run. `GET
subjects/{type}/{id}/work` names each agent (`NhAssistantSubjectAgentDto.DisplayNameKey`: a key for an
agent in code, the text for one created in the administration), also one kept out of the chat.

| Breaking change | Required action |
|---|---|
| Starting an agent by hand on the whole application (`POST threads/start`, a new thread of `POST threads/open`, `canStart`) follows the agent's `RunPolicy` (by default the administration policy) instead of the approve policy of the whole application. | Give the people who run such agents the run policy, or name a run policy with `RunPolicy` that they pass. |
| `canStart` of `GET subjects/{type}/{id}/work` also checks the scope of the whole application and the run policy, as the start does. | None; a start that the server refused before now reads `canStart: false`. |
| The run catalog reports `Searchable: true` for `nh-system` when the application lists its divisions, and its search finds divisions. | Do not treat a searchable `IsSystem` type as one with objects to search. |
| A headless turn a person asked for (a run started by hand, an agent request, a queued message, or a turn that answers a person's message in any run) that acts for someone else, such as the account an agent with `ActWithAccountableRights` answers to, no longer uses that person's personal MCP connection; the server is left out with `assistant-mcp-initiator-only`. | Use `DelegateFromInitiator()` when such runs should use the asking person's own connection, or an organization credential. |
| The JSON of `GET subjects/{type}/{id}/work` gains `displayNameKey` per agent. | Update tests that compare the whole JSON of this response. |

## @newheap/platform-ai-chat: run an agent now

`nh-assistant-object-agents` (`NhAssistantObjectAgentsComponent`, `subjectType`, `subjectId`) of
`@newheap/platform-ai-chat/agent-runs` shows the agents that work on an object, or on the whole
application with `NH_ASSISTANT_SYSTEM_SUBJECT_TYPE` and `NH_ASSISTANT_SYSTEM_GLOBAL_ID`, each with its
latest result (`AgentLatestRun`, at most `NH_ASSISTANT_RESULT_PREVIEW_LENGTH` characters, as text), its
open proposals, "Run now" for people who may run it and its work on demand; it emits
`openConversation`. `NhAssistantAgentRunApiService.getObjectAgents` returns `AgentObjectAgents`. The
administration's agent cards offer "Run now": a dialog asks for the whole application, a division
found by name for a per-division agent or an object of one of its kinds, and an optional instruction,
and ends with a receipt that opens the agent's conversation. The assistant's form fields use
`box-sizing: border-box`, so they keep within their width on a host page that does not set it. The
mock API serves `POST threads/start`, `GET threads`, a thread's runs and `GET subjects/{type}/{id}/agents`
with the server's rules, finds the scenario's `divisions` by name and takes `callerPolicies` and
`runNow` (`NhAssistantMockDivision`, `NhAssistantMockRunNow`). `nh-assistant-subject-suggestions` names
each agent by `AgentSubjectWorkAgent.displayNameKey` of the subject's work, so an agent kept out of the
chat shows its name instead of its id. New texts, among them the error `assistant-mcp-initiator-only`,
are bundled in English and Dutch.

| Breaking change | Required action |
|---|---|
| The mock API answers `POST threads/start`, `GET threads`, `GET threads/{id}/runs`, `proposals`, `memory`, `artifacts` and `consultations`, and `GET subjects/{type}/{id}/agents`, instead of `404`. | Update tests that expected `404` from these paths; script results with `runNow`. |
| The agent cards of the administration show "Run now" for agents that run on their own. | Update tests and styles that select card buttons by position. |
| Fields and buttons of the assistant's forms use `box-sizing: border-box`. | Remove host styles that compensated for the overflow. |
| The mock's `nh-system` type is `searchable` when the scenario lists `divisions`. | Leave `divisions` out of scenarios that expect no division search. |
| `NhAssistantStore.openConversation` and `NhAssistantPanelService.open` open an agent's thread also for an agent kept out of the chat, without choosing that agent in the picker; before, the panel stayed empty. | None; hosts that hid such threads on purpose check `kind` and the agent themselves. |

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.Generators, NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: proposals placed at their object

A tool declares which input property names which object type with `[NhAiSubject("project")]`
(`NhAiSubjectAttribute`) on a property of its input record, or the parameter of a positional record,
of type `Guid`, `string` or an integer. The source generator writes `NhAiToolDescriptor.Subject`
(`NhAiToolSubject`: the type and the JSON argument name), adds it to the contract hash only when
declared and reports `NHAI015` to `NHAI017` for invalid declarations; hand-built and attested catalogs
set `Subject` themselves, MCP tools never declare one. When an agent run proposes such a tool for
another object than its own, for example a weekly run for the whole application that proposes to
complete a project, the platform checks the object before the call runs: the agent works on its type
(per division only in its own division, a run bound to a tenant only in that tenant), the object
exists, the account the run answers to reads it, and the run placed fewer than
`NhAssistantLimits.MaxPlacedProposalsPerRun` (default 20). The call runs in the object's scope and
waits, deferred, in the agent's thread at that object, created when needed, with the object's version
and execution scope and the run that made it; the object's approvers decide it in its thread, on its
page and in their inbox, and it executes in the object's scope with the agent's account. A refused
placement does not fail the run: the call ends with `assistant-proposal-not-placed` and a reason
(`NhAssistantPlacementReasons`: `does-not-work-on`, `not-found`, `no-access`, `limit`), which rule
handlers read as `NhAssistantRuleProposal.PlacementReason`; a placed one answers
`assistant-proposal-placed` and `NhAssistantRuleProposal.PlacedAt` names the object. The audit records
`AgentProposalPlaced` and `AgentProposalNotPlaced`, proposal DTOs name the run with `PlacedBy`
(`NhAssistantProposalOriginDto`), and the approvers of each object are notified once per object when
the run ends. The whole application, which lists no members, now reaches its approvers: the approval
inbox and `POST subjects/nh-system/summary` ask the caller's live role on its subjects, and
notifications about its proposals reach the approvers among the people of its thread. The migration
`AgentPlacedProposals` adds the nullable column `PlacedFromConversationId` to `AssistantApproval`.

| Breaking change | Required action |
|---|---|
| A tool that starts declaring `NhAiSubject` gets a new contract hash; its pending approvals no longer execute (`tool-contract-mismatch`). | None, or let the agents propose again; tools without the attribute keep their hashes. |
| A run's proposal of a tool with a declared subject for another object than the run's own waits at that object, deferred, for that object's approvers, instead of in the run's thread under the run's scope; a refused one ends with `assistant-proposal-not-placed`. | Give the account an agent answers to read access to the objects it proposes for, and let the objects' approvers decide; read `PlacementReason` in rule handlers. |
| An agent that runs on a subject type other than the one a tool declares (for example an application that reuses a `project` tool for its own `partner-project` objects) no longer proposes that tool's calls in its own thread: the call names another object, which is refused (`does-not-work-on` or `not-found`) or placed. | Register the objects under the type the tool declares, or give the application a tool that declares its own type. |
| A proposal placed at another object is no longer decided by the run's initiator through the initiator approver rule, and it is no longer in the initiator's inbox. | Let the object's approvers decide it. |
| The approval inbox and `POST subjects/nh-system/summary` list and count proposals of the whole application for callers whose live role is approver; notifications about them reach the approvers among the people of its thread. | None; inboxes of administrators may grow. |
| `NhAssistantAuditEventKind` gains `AgentProposalPlaced` and `AgentProposalNotPlaced`; the agent notification `RunEndedWithProposals` also goes to the approvers of each object a run placed proposals at, once per object. | Handle the new kinds in exhaustive switches of business audit sinks; notification policies and channels see the object's subject in the context. |
| Migration `AgentPlacedProposals`. | Apply it for SQL Server or PostgreSQL. |

## @newheap/platform-ai-chat: proposals placed at their object

`AgentProposal.placedBy` (`AgentProposalOrigin`) names the run that placed a proposal at its object
from another subject. The proposal cards of `nh-assistant-agent-proposals`, the inbox, the thread view
and the subject suggestions say "Proposed by a run for the whole application" (with the division's name
in a division) or "Proposed by a run for another object" with its kind, and offer that run's
conversation: `nh-assistant-agent-proposals`, `nh-assistant-agent-inbox` and
`nh-assistant-agent-thread-view` emit `openConversation`, and `nh-assistant-subject-suggestions` opens
it in the panel and emits `runConversationOpened`. The errors `assistant-proposal-placed` and
`assistant-proposal-not-placed` are registered and bundled in English and Dutch. The mock API serves the
scenario's `proposals` (`NhAssistantMockProposal`) in `GET approvals/inbox`, `GET approvals/{id}`,
`POST approvals/lookup`, a thread's proposals and a subject's work, and decides them with
`POST approvals/{id}/decisions` and `POST approvals/decisions` with the server's validation.

| Breaking change | Required action |
|---|---|
| `nh-assistant-agent-inbox` and `nh-assistant-agent-thread-view` emit `openConversation` for the run that placed a proposal; the thread view emitted it only for its own thread before. | Open the emitted conversation with `NhAssistantPanelService.open`; check the id when the thread view's own conversation needs other handling. |
| The mock API answers `approvals/inbox`, `approvals/{id}`, `approvals/lookup` and the decision endpoints instead of `404`. | Update tests that expected `404`; list proposals in `NhAssistantMockScenario.proposals`. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: results and history

A run keeps its whole answer as Markdown in `AssistantRun.Result`, at most
`NhAssistantLimits.MaxRunResultCharacters` (20,000) characters with `ResultTruncated` when it was cut:
the final answer of each of its turns that completed, the summary a rule handler wrote included, next
to the small summary the agent's next runs start from. The answer is model output and may carry MCP
tool output; render it only with a safe Markdown renderer. `GET runs` lists the runs a person may see
(`NhAssistantRunHistoryListDto`, `NhAssistantRunHistoryDto`): runs on objects the person reads, or
whose thread they were invited to, and runs for the whole application of agents whose `ViewPolicy`,
by default their required policy, the person passes, evaluated like the roles of the whole
application; shadow work for administrators only. It filters by `agentId`, `subjectType`,
`subjectId`, `status`, `from` and `to`, pages up to 50 runs and considers the 100 agent threads with
the most recent matching runs (`Limited`). `GET runs/{id}` (`NhAssistantRunResultDto`) returns one run
with its whole result, its summary, its proposals (those it placed at objects the person reads, the
others counted in `HiddenProposals`) and its files, which `GET runs/{id}/artifacts/{artifactId}/content`
downloads; administrators open every run. `GET admin/agents/{id}/runs` lists every run of an agent,
live and shadow, for its runs view. An invalid filter answers `400 assistant-run-filter-invalid`
(`NhAssistantErrorCodes.RunFilterInvalid`). A proposal a run places at another object no longer
creates the agent's thread there when a later check, such as the duplicate check, refuses it.

| Breaking change | Required action |
|---|---|
| `NhAssistantLatestRunDto.Result` of `GET subjects/{type}/{id}/agents` is the beginning of the run's stored answer instead of the summary it ended with; runs without a stored answer keep the summary. | None; keep showing it as text. |
| The assistant maps `GET runs`, `GET runs/{id}`, `GET runs/{id}/artifacts/{artifactId}/content` and `GET admin/agents/{id}/runs` below its prefix. | Move host routes that used these paths below the assistant's prefix. |
| Migration `AgentRunResults` adds `Result` and `ResultTruncated` to `AssistantRun` and an index of `AssistantThread` by agent and subject type. | Apply it for SQL Server or PostgreSQL. |

## @newheap/platform-ai-chat: results and history

`nh-assistant-run-result` (`NhAssistantRunResultComponent`, `runId`) of
`@newheap/platform-ai-chat/agent-runs` shows one run: what it worked on, its status, trigger, start,
duration and usage, its whole answer rendered with the assistant's safe Markdown rendering, the
proposals it made (also those it placed at objects the person reads, and how many wait elsewhere) and
its files; it emits `openConversation`, and `conversationLink` hides the conversation where it is
already shown. The run timeline opens it with "Show the result", and `nh-assistant-object-agents`
shows the latest run's whole result on demand. `NhAssistantAgentRunApiService.getRunHistory`
(`AgentRunFilter`, `AgentRunHistory`, `AgentRunHistoryItem`), `getRunResult` (`AgentRunResult`,
`NH_ASSISTANT_RUN_RESULT_MAX_LENGTH`) and `downloadRunArtifact` read the server's results and history;
`NH_ASSISTANT_RUN_HISTORY_MAX_PAGE` bounds a page. The administration's agent cards offer "Runs", which
opens `nh-assistant-agent-run-history` (`NhAssistantAgentRunHistoryComponent`) of
`@newheap/platform-ai-chat/admin` with `NhAssistantAdminApiService.getAgentRuns`: every run of the
agent with shadow runs marked, filtered by kind of object, object, the whole application, status and
period, each opened with its result. The mock API serves the run histories with the server's
visibility, filters and validation and takes earlier runs in `NhAssistantMockScenario.runs`
(`NhAssistantMockRun`). The error `assistant-run-filter-invalid` is registered and the new texts are
bundled in English and Dutch.

| Breaking change | Required action |
|---|---|
| The cards of `nh-assistant-run-timeline` and `nh-assistant-object-agents` gain "Show the result", and the administration's agent cards gain "Runs". | Update tests and styles that select card buttons by position. |
| The mock API answers `GET runs`, `GET runs/{id}`, `GET runs/{id}/artifacts/{artifactId}/content` and `GET admin/agents/{id}/runs` instead of `404`, and a run by hand keeps `runNow.result` as its answer and the first 600 characters as its summary. | Update tests that expected `404` or the whole result as the summary; seed runs with `runs`. |
| The conversation's Markdown styles come from a shared partial that also styles thematic breaks (`hr`). | None; remove host styles that styled `hr` inside the panel. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: groups and boards

`NhAssistantBuilder.AddAgentGroup(id, group => group.Named(...).Describe(...))` registers a group of
agents with its name per language and an optional description (`NhAssistantAgentGroupBuilder`); the
same group registered again is kept. An agent in code joins with
`NhAssistantAgentRunBuilder.InGroup(id)`, and startup fails for an unknown group or a shadow variant in a
group. Administrators put any agent in groups or take it out with `PUT admin/agents/{id}/groups` or
`INhAssistantAgentGroups.SetMembershipAsync` (`NhAssistantAgentGroupMembershipDto`): the choice is stored
where it differs from code and wins over code; agents created in the administration join groups this way
only. The run catalog lists `AgentGroups` (`NhAssistantAgentGroupDto`) and the admin agent DTO carries
`Groups` and `CodeGroups`. `GET boards` (`NhAssistantBoardListDto`) lists the groups whose board shows the
caller at least one agent; `GET boards/{group}` (`NhAssistantBoardDto`, `NhAssistantBoardAgentDto`)
returns per agent what it works on, its whole application in the board's scope (with `divisionId` a
division), `CanStart`, its thread when the caller reads it, its latest run, its open proposals (also
those its runs placed at objects) and `IsNew` since the caller's last visit of the board in that scope;
`POST boards/{group}/visit` (`NhAssistantBoardVisitDto`) records the visit. An agent that works on the
whole application shows by its view policy, by default its required policy, another by its required
policy. New error codes: `assistant-board-not-found` and `assistant-board-scope-invalid`
(`NhAssistantErrorCodes.BoardNotFound`, `BoardScopeInvalid`). An agent's thread of the whole application,
its conversation, proposals and files are now read only by people who have the read role of the whole
application and pass the agent's view policy, as its results are.

| Breaking change | Required action |
|---|---|
| Reading an agent's thread of the whole application (`GET threads`, its conversation, proposals, runs, memory and files, `GET approvals/{id}`, the subject's work and agents) needs the agent's view policy, by default its required policy, next to the read role; an invitation no longer opens it to others. Approvers still decide its proposals from the inbox. | Give people who read such threads the agent's view policy, or set a `ViewPolicy` they pass. |
| Starting an agent by hand on the whole application (`POST threads/start`, a new thread of `POST threads/open`, `canStart`) needs the agent's view policy as well, so its thread can be opened. | Give people who run such agents the view policy too. |
| The assistant maps `GET boards`, `GET boards/{groupId}`, `POST boards/{groupId}/visit` and `PUT admin/agents/{id}/groups` below its prefix. | Move host routes that used these paths below the assistant's prefix. |
| Migration `AgentBoards` adds `AssistantAgentGroupMember` and `AssistantBoardVisit`. | Apply it for SQL Server or PostgreSQL. |

## @newheap/platform-ai-chat: groups and boards

`nh-assistant-agent-board` (`NhAssistantAgentBoardComponent`, `group`, optional `divisionId`) of
`@newheap/platform-ai-chat/agent-runs` shows one group of agents anywhere in an application: per agent
what it works on, its latest result for the whole application rendered with the assistant's safe
Markdown rendering, "New" for what arrived since the person's last visit, "Run now" through the run-now
dialog when the server allows it, its open proposals in `nh-assistant-agent-inbox` and its history with
every result on demand. Showing it records the visit after reading the board; it emits
`openConversation` and reloads on live updates. `NhAssistantAgentRunApiService.getBoards`, `getBoard` and
`visitBoard` (`AgentBoardList`, `AgentBoardSummary`, `AgentBoard`, `AgentBoardAgent`,
`AgentBoardWorksOn`, `AgentBoardSubjectType`, `AgentBoardVisit`) read the boards. The agent editor of the
administration lists the run catalog's `agentGroups` (`AgentGroup`) for every agent, marks the groups of
its code and saves the choice with `NhAssistantAdminApiService.setAgentGroups` (`AdminAgentGroups`); the
agent cards name the agent's groups (`AdminAgent.groups`, `codeGroups`). The mock API serves groups,
memberships, boards and visits from `NhAssistantMockScenario.agentGroups` (`NhAssistantMockAgentGroup`)
with the server's validation. The errors `assistant-board-not-found` and `assistant-board-scope-invalid`
are registered and the new texts are bundled in English and Dutch.

| Breaking change | Required action |
|---|---|
| The agent editor has a groups section after the works-on section when the run catalog lists groups, and saves an existing agent's changed groups before its definition. | Update tests and styles that select the editor's fieldsets by position; mock or serve `PUT admin/agents/{id}/groups` where you host the editor. |
| The administration's agent cards show a groups line for agents in a group. | Update tests and styles that select the card's paragraphs by position. |
| The mock API answers `GET boards`, `GET boards/{group}`, `POST boards/{group}/visit` and `PUT admin/agents/{id}/groups` instead of `404`, and its admin agents and run catalog carry `groups`, `codeGroups` and `agentGroups`. | Update tests that expected `404` or compare whole agent objects; list groups in `agentGroups`. |

## NewHeap.Platform.AI.Mcp, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: MCP servers in background runs

An administrator-connected MCP server is now offered in headless agent runs, and in the execution
of their deferred proposals, only when an administrator sets its new `headlessUse` to
`organization` (`NhAssistantMcpHeadlessUses`). Every server, existing and new, starts at `never`;
the `McpConnectionPolicy` migrations add the column. Before, servers with `none`, `bearer` or
`api-key` and no `requiredPolicy` were offered to background runs although the documentation said
they were not, and an approved deferred MCP proposal always failed with `assistant-approval-invalid`.
Now such a proposal imports the tool again and executes it once; a server that no longer allows
background runs fails it with `assistant-mcp-headless-not-allowed`. `organization` is refused for
`forward-user-token`. In a background run `requiredPolicy` is checked for the person whose rights the
run uses, and tools of an `organization` server also accept a code grant (`Grant("mcp.<server>.*")`).

The MCP client now attaches the credential to every HTTP request and checks every request against
the planned origin and `AllowedHosts`. A `403` with `error="insufficient_scope"` reports
`assistant-mcp-insufficient-scope` instead of `assistant-mcp-unauthorized`, also for tool calls, which
previously all reported `assistant-mcp-unreachable`. A tool list seen with one caller's forwarded
token no longer disables a tool for everyone. Tests, syncs and turns with the shared credential record
`lastConnectAt` and `lastConnectCode` on `NhAssistantMcpServerDto`. In `NewHeap.Platform.AI.Mcp`,
`NhAiMcpImportedToolPolicy.AllowAgentGrant` lets a code grant allow an imported tool, and
`NhAiMcpCallContext.Current` carries the authorized invocation context to the client transport while
the remote call runs.

| Breaking change | Required action |
|---|---|
| MCP servers are no longer offered in background runs unless `headlessUse` is `organization`. | Apply the `McpConnectionPolicy` migration; set `headlessUse` to `organization` for each server whose own credential agents may use without a person, and grant `mcp.<server>.*` to agents that act for themselves. |
| A forbidden MCP response with `insufficient_scope` reports `assistant-mcp-insufficient-scope`, and failed MCP tool calls report the connection code instead of `assistant-mcp-unreachable`. | Map the new code where you map `assistant-mcp-unauthorized`. |
| An imported tool with `AllowAgentGrant` has a different contract hash. | None for existing policies; approvals of a tool change when its server's headless use changes. |

## @newheap/platform-ai-chat: background runs per MCP server

The MCP server editor of `@newheap/platform-ai-chat/admin` gains **Background runs**: `Not in
background runs` (the default) or `Background runs with this credential`; servers that forward the
user's token offer the personal variant described below instead. The server list shows the choice and the last connection state, and a
connection test refreshes it. `McpServerInput.headlessUse` is optional (omitted keeps the stored
value), and `McpServer` gains `headlessUse`, `lastConnectAt` and `lastConnectCode`; the new type
`McpHeadlessUse` names the values. The codes `assistant-mcp-insufficient-scope`,
`assistant-mcp-headless-not-allowed` and `assistant-mcp-credential-mismatch` are bundled in English
and Dutch. The mock API of `@newheap/platform-ai-chat/testing` keeps `headlessUse` per server,
refuses `organization` for a forwarded token and records the connection state on test and sync.

| Breaking change | Required action |
|---|---|
| `McpServer` has the required fields `headlessUse`, `lastConnectAt` and `lastConnectCode`. | Add them to hand-built `McpServer` objects in tests. |

## NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: MCP credentials and personal servers in background runs

MCP servers gain the authentication mode `basic` (`userName` plus the write-only secret as
password). A secret no longer has to be stored: `secretReference` `config:<name>` reads
`NewHeap:AI:Assistant:Mcp:Secrets:<name>`, and any other prefix is resolved by an
`INhAssistantMcpSecretResolver` that the application registers with
`AddMcpSecretResolver<TResolver>(prefix)`, for example for a vault. A reference is resolved for every
request, so a rotated secret applies at once, and its name contains no `:` or `..`, so it never reaches
other configuration. A `config:` value is read with `NhConfigurationSecrets.Read`, so it may also
come from the secrets files, and a blank value or an unresolved `${...}` reference counts as missing. A stored secret and a reference replace each other.
`clientCertificateReference` names a client certificate under
`NewHeap:AI:Assistant:Mcp:ClientCertificates:<name>` (a PKCS#12 file or a certificate store entry with
a private key) for mutual TLS. `transport` `auto`
(`NhAssistantMcpTransports`) falls back to the deprecated HTTP+SSE transport for servers without
Streamable HTTP; the SSE endpoint must stay on the configured origin.

`headlessUse` gains `personal` for `forward-user-token` servers. In a background run, such a server
receives a short-lived delegated NewHeap token of the person whose rights the run uses, minted for every
request, marked with the invocation and bound to the server's origin, and only after that person allowed
it: `GET mcp-connections` lists the person's servers and `PUT mcp-connections/{serverId}`
`{ allowBackgroundUse }` records or withdraws the consent, which a changed address or authentication
invalidates. Without consent the server is skipped with `assistant-mcp-consent-required`; a person who may
not sign in, a failing issuer or a host whose JWT bearer schemes do not restrict such tokens gives
`assistant-mcp-delegated-token-unavailable`. A call authorized for anyone but that person is not sent.
`NhAssistantMcpOptions.ForwardedTokenLifetime` (two minutes, at most five) sets the token's lifetime.
The token that planning a connection obtains serves its first request. `McpBackgroundUseChanged` audits
consent, and `McpDelegatedTokenIssued` audits issuing once per turn, server, person and outcome with the
number of tokens in the new `NhAssistantAuditEvent.TokenCount`, never the token itself, and the `McpServerCredentials` migrations add the server columns and the
`AssistantMcpConnection` table on SQL Server and PostgreSQL.

`NewHeap.Platform.AI.AspNet.Common` adds `AddNewHeapPlatformAIMcpDelegatedTokens`: every JWT bearer
scheme of the host refuses a token with the `nh-ai-mcp-delegated` claim unless `Accept(origin, path)`
names its audience and the request is below that path. `INhAiMcpDelegatedTokenIssuer` issues the tokens
(by default through `INhAuthenticationService.CreateDelegatedTokenAsync`), `NhAiMcpDelegatedTokenDefaults`
holds the claims and limits and `IsDelegatedTokenRestrictionEnforcedAsync` reports whether the host
applies the restriction. `AddNewHeapAssistant` calls it without `Accept`, so an assistant's host never
accepts its own delegated MCP tokens. These tokens are signed with the host's symmetric key: every host
that can validate them can also mint them, so use `personal` only between hosts that already share that
key.

| Breaking change | Required action |
|---|---|
| `AddNewHeapAssistant` wraps the events of every JWT bearer scheme, as delegated reads do. Tokens without the new claim are not affected. | None. A scheme with `EventsType` keeps working, but `personal` servers then get no tokens; set `Events` instead. |
| Startup fails when `ForwardedTokenLifetime` is not longer than zero or above five minutes. | None for the default. |
| New columns and the `AssistantMcpConnection` table. | Apply the `McpServerCredentials` migration. |

## @newheap/platform-ai-chat: MCP credentials and consent to background use

The MCP server editor offers **User name and password** (`basic`) with a user name, a choice between a
stored secret and a reference to a secret of the application, a client certificate name and the
transport, and **Background runs with the person's own account, after consent** (`personal`) for servers
that forward the user's token. The server list shows the reference, the certificate and the fallback
transport. The preferences show `nh-assistant-mcp-connections` (`NhAssistantMcpConnectionsComponent`)
when a personal server is assigned to an agent the user may use: one switch per server that allows or
withdraws background use at once. `NhAssistantApiService` gains `getMcpConnections` and
`updateMcpConnection`, with the models `McpConnection` and `UpdateMcpConnectionRequest`.
`McpAuthMode` adds `basic`, `McpHeadlessUse` adds `personal`, the new `McpTransport` names the
transports, `McpServerInput` gains the optional `userName`, `secretReference`,
`clientCertificateReference` and `transport`, and `McpServer` gains them as fields. The codes
`assistant-mcp-consent-required`, `assistant-mcp-delegated-token-unavailable`,
`assistant-mcp-secret-reference-invalid` and `assistant-mcp-certificate-reference-invalid` are bundled
in English and Dutch. The mock API validates the new fields, resolves `config:` references or the
scenario's `secretReferences`, knows the scenario's `clientCertificates` and serves
`mcp-connections` with the scenario's `mcpConnections`. Like the server, the mock now refuses a
`bearer`, `api-key` or `basic` server without a stored secret or a reference
(`assistant-validation` with `secret: ["required"]`), and the editor no longer offers **Clear** for a
secret, which the server always refused for those modes; choosing a reference or a mode without a
secret removes the stored one.

| Breaking change | Required action |
|---|---|
| `McpServer` has the required fields `userName`, `secretReference`, `clientCertificateReference` and `transport`, and `McpAuthMode` and `McpHeadlessUse` have new values. | Add the fields to hand-built `McpServer` objects and handle the new values in exhaustive switches. |
| The mock API refuses `bearer`, `api-key` and `basic` servers without a secret or reference, and the server editor has no **Clear** button for a secret. | Give such servers a secret in mock scenarios and tests; tests that clicked **Clear** choose another mode instead. |

## NewHeap.Platform.AI.Test, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: MCP servers with OAuth for the organization

MCP servers gain the authentication modes `oauth-client-credentials` and `oauth`
(`NhAssistantMcpAuthModes`). With client credentials the host's own client (`oauth.clientId` with a
write-only `clientSecret`, a `clientSecretReference` or, for `private_key_jwt`, the
`clientCertificateReference`) obtains a token on first use. With `oauth` and `connectionScope`
`organization` (`NhAssistantMcpConnectionScopes`) an administrator authorizes the organization's
account once, named in `organizationAccountLabel`, because everyone using the server's agents then acts
as it.

`POST admin/mcp-servers/discover` reads what a server's challenge and protected resource metadata
(RFC 9728) name and the authorization server's metadata (RFC 8414 or OpenID Connect), and stores
nothing. Saving with `oauth.confirmAuthorization` pins that issuer and the canonical resource; a new
server or address needs it, and `AdminMcpServerAuthorizationConfirmed` audits it. Tokens are requested
from the pinned issuer only, always with `resource` (RFC 8707); PKCE `S256` is required and the `iss` of
the redirect is checked (RFC 9207). A server that later names another authorization server suspends its
connections (`issuer-changed`) until an administrator confirms again. Without a client id the host
identifies itself by its Client ID Metadata Document (`<prefix>/mcp/oauth/client-metadata.json`, when
`ClientMetadataDocumentUri` is set) or registers itself dynamically once per issuer.

`POST admin/mcp-servers/{id}/connection/authorize` starts the authorization; the anonymous
`<prefix>/mcp/oauth/callback` checks the state, exchanges the code and hands a one-time completion code to
the starter's page, which `POST mcp-connections/complete` turns into the connection with the starter's own
session and the admin policy. `GET` and `DELETE admin/mcp-servers/{id}/connection` show and remove it;
`GET admin/mcp-servers/{id}/connections/summary` and `DELETE admin/mcp-servers/{id}/connections` count and
revoke personal connections. Tokens are protected with ASP.NET Data Protection, renewed once across
instances before they expire and after a `401` (a lease in the database), and revoked at the
authorization server (RFC 7009) when the connection is removed, when the address, mode, scopes, client or
issuer change and when the server is deleted. `insufficient_scope` asks client credentials for the union
of scopes within `oauth.scopes`. A refused refresh ends the connection with
`assistant-mcp-reauthorization-required` and `McpConnectionReauthorizationRequired`; connecting and
disconnecting are audited as `McpConnectionConnected` and `McpConnectionDisconnected`. Expired
authorizations are removed in the background.

`NhAssistantMcpOptions.OAuth` (`NhAssistantMcpOAuthOptions`) sets `RedirectUri` (required for `oauth`,
otherwise `assistant-mcp-oauth-not-configured`), `ReturnOrigins`, `ClientMetadataDocumentUri`,
`ClientName`, `ClientUri`, `FlowLifetime`, `RefreshSkew`, `RefreshLeaseDuration`, `ConnectionRetention`
and `MaxPendingFlowsPerActor`; the host checks them at startup. Discovery, metadata, token and
revocation requests pass the host guard and never reach private networks unless the host is in
`AllowedHosts`; the new `BlockPrivateNetworks` applies that to MCP servers too. The opt-in health check
`AddNewHeapAssistantMcpHealthCheck` reports servers whose last connection failed and connections that
wait for someone, and the `NewHeap.Platform.AI` meter counts `nh.ai.mcp.requests`,
`nh.ai.mcp.token_requests` and `nh.ai.mcp.authorizations` by outcome. The new codes are
`assistant-mcp-connection-required`, `assistant-mcp-reauthorization-required`,
`assistant-mcp-scope-not-allowed`, `assistant-mcp-authorization-failed`,
`assistant-mcp-authorization-expired`, `assistant-mcp-authorization-invalid`,
`assistant-mcp-discovery-failed`, `assistant-mcp-pkce-unsupported`,
`assistant-mcp-client-registration-required`, `assistant-mcp-token-request-failed`,
`assistant-mcp-issuer-changed`, `assistant-mcp-authorization-not-confirmed`,
`assistant-mcp-oauth-not-configured` and `assistant-mcp-too-many-authorizations`. The
`McpOAuthConnections` migrations add the server and connection columns and the
`AssistantMcpAuthorizationFlow` and `AssistantMcpClientRegistration` tables on SQL Server and PostgreSQL.

`NewHeap.Platform.AI.Test` adds `NhAiTestAuthorizationServer`, an OAuth authorization server on
loopback with PKCE, refresh rotation with reuse detection, client credentials, `private_key_jwt`,
dynamic registration, Client ID Metadata Documents and revocation, and `NhAiTestProtectedResource`,
which puts protected resource metadata and bearer validation in front of a test MCP server.

| Breaking change | Required action |
|---|---|
| New columns and tables. | Apply the `McpOAuthConnections` migration. |
| The server DTO has the new fields `connectionScope`, `oauth`, `authorization` and `organizationConnection`, and the `mcp-connections` DTO has `connectionStatus`, `grantedScopes`, `requiredScopes`, `connectedAt` and `offersBackgroundUse`. | Clients that reject unknown fields accept them. |
| Startup fails when `NhAssistantMcpOptions.OAuth` has an invalid URL or limit. | None when OAuth is not configured. |

## @newheap/platform-ai-chat: OAuth for MCP servers

The MCP server editor offers **OAuth (authorization)** and **OAuth client credentials**: it reads the
server's authorization, shows the authorization server, resource, offered scopes and how the host
registers, and saves only after the administrator confirmed it. It asks the connected account's label
for an organization connection, the scopes, a client id and a write-only client secret or a reference
to one. The server list shows the organization connection with **Connect**, **Connect again** and
**Disconnect**, the connected account and who connected it. `NhAssistantAdminApiService` gains
`discoverMcpServer`, `getMcpOrganizationConnection`, `authorizeMcpOrganizationConnection`,
`disconnectMcpOrganizationConnection`, `getMcpConnectionsSummary`, `revokeMcpConnections` and
`completeMcpAuthorization`. `NH_ASSISTANT_MCP_AUTHORIZATION_WINDOW` opens the authorization; the default
`NhAssistantMcpPopupAuthorizationWindow` opens a popup in the click handler and accepts only the message
of that popup for the started flow, `nhAssistantConnectMcp` runs start, window and completion, and
`nhAssistantMcpAuthorizationFromFragment` reads a redirect-mode return. The models `McpConnectionScope`,
`McpConnectionStatus`, `McpOAuthInput`, `McpOAuthSettings`, `McpAuthorization`,
`McpOrganizationConnection`, `McpDiscovery`, `McpAuthorizeRequest`, `McpAuthorizationStart`,
`McpCompleteRequest`, `McpCompletion` and `McpConnectionsSummary` are new, and the new codes, including
the client codes `assistant-mcp-authorization-cancelled` and `assistant-mcp-popup-blocked`, are bundled
in English and Dutch. The mock API discovers `https://auth.<host>`, validates OAuth servers like the
server, plays authorizations through `NhAssistantMockAuthorizationWindow` (`mcpAuthorization` of the
scenario: `approve`, `deny` or `cancel`), connects client credentials on first use and accepts
`organizationConnected`, `mcpDiscovery` and `mcpReturnOrigins`.

| Breaking change | Required action |
|---|---|
| `McpServer` has the required fields `connectionScope`, `oauth`, `authorization` and `organizationConnection`, `McpConnection` has `connectionStatus`, `grantedScopes`, `requiredScopes`, `connectedAt` and `offersBackgroundUse`, and `McpAuthMode` has new values. | Add the fields to hand-built objects and handle the new values in exhaustive switches. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: personal MCP connections

An `oauth` server with `connectionScope` `user` lets each person connect their own account.
`POST mcp-connections/{serverId}/authorize` starts the person's authorization (with
`allowBackgroundUse` to consent to background use when the server's `headlessUse` is `personal`, and
`returnUrl` for redirect mode), `POST mcp-connections/complete` completes it with the person's own
session, and `DELETE mcp-connections/{serverId}` removes it and revokes the tokens; a consent stays
until the person withdraws it. `GET mcp-connections` now also lists these servers, with
`connectionStatus`, `grantedScopes`, `requiredScopes`, `connectedAt` and `offersBackgroundUse`, and
`PUT mcp-connections/{serverId}` refuses consent with `assistant-mcp-headless-not-allowed` for a
server that is not offered in background runs.

A chat turn uses the connection of the person who sent the message, also in a shared conversation.
Without one, the turn goes on without the server's tools, the model is told the service is not
connected, and the turn streams the new `connection.required` event (`serverId`, `displayName`,
`reason`: `not-connected`, `reauthorization-required` or `insufficient-scope`,
`NhAssistantMcpConnectionReasons`), which is also stored as a `connection-required` message part
(`NhAssistantConnectionRequiredPartDto`). A server that refuses the connection's scopes records them,
and connecting again requests them. A background run uses the connection of the person whose rights
it uses only with `personal` headless use and that person's consent; an approved proposal executes
with the connection of the person who asked and fails with `assistant-mcp-connection-required` once
that person disconnected. Connections and tool lists of personal servers are cached per person.
Administrators test and sync such a server with their own connection through
`POST admin/mcp-servers/{id}/connection/authorize`. Personal connections unused for
`ConnectionRetention` are removed and revoked, and `NhAssistantAuditEvent.McpConnectionOwner` names
whose credential a `ToolInvoked` call of an MCP tool carried. The consent of an OAuth server now also
covers its authorization server and scopes.

| Breaking change | Required action |
|---|---|
| Turns stream `connection.required` and conversations can contain `connection-required` parts. | Clients that reject unknown events or part types ignore them, or update `@newheap/platform-ai-chat`. |
| A consent to background use of an OAuth server holds for its authorization server and scopes too. | None; a changed server asks for consent again. |

## @newheap/platform-ai-chat: personal MCP connections in the chat and the preferences

The thread shows `nh-assistant-connection-required` (`NhAssistantConnectionRequiredComponent`) for an
answer that went on without the person's connection: it connects the viewer's own account and offers
to ask again, which the panel sends. `nh-assistant-mcp-connections` lists the servers each person
connects to with their status, scopes, **Connect**, **Connect again** and **Disconnect**, and the
consent switch only where background use is offered. `NhAssistantMcpConnectService` runs
connections in a popup, or with `NhAssistantConfig.mcpConnectMode: 'redirect'` by leaving the page
and completing on return (`mcpConnectReturnUrl`); the administration uses it for organization
connections too. The server list offers **Connect my account** for such servers, shows the number of
personal connections and revokes them all after confirmation. `NhAssistantApiService` gains
`authorizeMcpConnection`, `disconnectMcpConnection` and `completeMcpAuthorization`. The models
`ConnectionRequiredPart`, `ConnectionRequiredReason` and `ConnectionRequiredEvent` and the event
`connection.required` are new. The mock API serves personal connections, plays the new
`connectionRequired` step and accepts `personalConnected` on mock servers.

| Breaking change | Required action |
|---|---|
| `MessagePart` and `NhAssistantSseEventMap` have new members. | Handle `connection-required` and `connection.required` in exhaustive switches. |

## NewHeap.Platform.AspNet.Common and NewHeap.Platform.AI.AspNet.Common: asymmetric token signing

An application can sign its access tokens with `RS256` or `ES256` instead of the shared HS256 key, so
other hosts validate them without being able to mint any. Configure
`NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Signing` (`NhJwtSigningKeys`): `Algorithm`,
`KeyId` and exactly one private key from the secrets file, `PrivateKey` (PEM) or `Pkcs12` (Base64) with
an optional `Pkcs12Password`, both read with `NhConfigurationSecrets.Read`. `PreviousKeys` (`KeyId`
and `PublicKey`, a PEM public key or certificate) keep validating the tokens of a rotated key, and
`AcceptSharedKeyTokens` lets the issuing application accept tokens of its former shared key while it
moves from HS256. `NhAuthenticationService` signs with the key and sets the `kid`, and the platform's
validation parameters (`NewHeapAspNetCommonOptions` and `ConfigureNhJwtBearerValidationOptions`)
accept the current and previous public keys with `ValidAlgorithms` limited to theirs. A configuration
error stops the application with a message that names the setting, never the key.

`app.MapNewHeapJwks()` publishes the public keys as an anonymous JSON Web Key Set at
`/.well-known/jwks.json`; mapping it for an HS256 application fails at startup.
`JwtBearerOptions.UseNewHeapJwks(jwksUri, issuer, audiences)` validates another application's tokens
with that key set and picks up a rotated key. `INhAuthenticationService.CreateDelegatedTokenAsync`
gains an overload with `audience`: with asymmetric signing the token's `aud` is that service, so the
issuing application refuses it; with HS256 it keeps the issuer, as before. The built-in delegated MCP
token issuer of `AddNewHeapPlatformAIMcpDelegatedTokens` uses it with the MCP server's origin, so a
first-party MCP server validates delegated tokens with the published keys and its own origin as
audience. Derived authentication services customize signing with `GetSigningCredentials` and tokens
for another audience with `CreateTokenForAudienceAsync`.

| Breaking change | Required action |
|---|---|
| None for an application without the `Signing` section: it signs and validates HS256 with `Token:Key` as before. | None. To let other hosts validate tokens without the shared key, configure `Signing` with `ES256` or `RS256`, keep the private key in the secrets file, map `MapNewHeapJwks` and let the other hosts use `UseNewHeapJwks`. |
| `INhAuthenticationService` has a new `CreateDelegatedTokenAsync` overload with `audience`, a default method that creates the token of the existing overload. | None for implementations. A test substitute that stubs the delegated MCP token stubs the overload with `audience`. |
| With asymmetric signing, delegated MCP tokens carry the MCP server's origin as `aud` instead of the issuer. | Validate them on the first-party server with `UseNewHeapJwks(jwksUri, issuer, issuer, origin)` next to `Accept(origin, "/mcp")`, and remove the shared key from that server. |

## NewHeap.Platform.AI.Chat.AspNet: connections of people the platform no longer resolves

The MCP maintenance now removes the personal connections of people the platform no longer resolves,
long before `ConnectionRetention`: it asks `INhAiPrincipalFactory` for every owner of a personal token
connection, with the whole application as subject, at most 100 people per maintenance run and a pass
over everyone at most every hour. A person it does not resolve, because the person was removed or may
no longer sign in, loses the tokens of every personal connection; they are revoked at the
authorization server and audited as `McpConnectionDisconnected` with `owner-unresolved`. A consent to
background use stays without tokens, as after retention. A host whose built-in principal factory
cannot resolve anyone, because the NewHeap user manager is missing, is not swept.

| Breaking change | Required action |
|---|---|
| A person who may no longer sign in, also for a while such as during a lock-out, loses their personal MCP connections within about an hour. | None; the person connects again from the chat or the preferences. A host with its own `INhAiPrincipalFactory` makes sure it resolves every person who may use the assistant, also for the whole application as subject. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: a missing personal connection in a background run

A background run that leaves out a server with `personal` headless use because the person's consent to
background use or connection is missing (`assistant-mcp-consent-required`,
`assistant-mcp-connection-required`, `assistant-mcp-reauthorization-required`), or because the run acts
for someone other than the person who asked (`assistant-mcp-initiator-only`), now records the server and
the code on the run: `NhAssistantRunHistoryDto.McpConnectionSkips` (`NhAssistantRunMcpSkipDto`) in the
run history and the run result. The person whose consent or connection is missing is told through the
agent notifications with the new kind `NhAssistantAgentNotificationKind.McpConnectionMissing`
(`McpServerId`, `McpServerDisplayName` and `McpConnectionPersonId` on the context; the default policy
sends it to that person only), at most once per person and server a day, also through
`UseInAppAgentNotifications`. The migrations `McpRunConnectionSkips` add the column
`AssistantRun.McpConnectionSkips` and the table `AssistantMcpConnectionNotice`.

| Breaking change | Required action |
|---|---|
| The schema has the migration `McpRunConnectionSkips` for SQL Server and PostgreSQL. | Apply the assistant's migrations; with `RunMigrations` off, deploy them before the new version. |
| `NhAssistantAgentNotificationKind` has the new value `McpConnectionMissing`. | Handle it in custom `INhAssistantAgentNotificationPolicy` and channel implementations that switch over every kind; send it to `McpConnectionPersonId`. |

## @newheap/platform-ai-chat: personal MCP servers a run left out

`AgentRunHistoryItem.mcpConnectionSkips` (`AgentRunMcpSkip`: `serverId`, `serverDisplayName`, `code`)
names the personal MCP servers a background run left out and why. `nh-assistant-run-result` lists them
with their reason, and the administration's runs view and the flow view's latest runs show how many were
left out. The mock API returns `mcpConnectionSkips` of scenario runs (`NhAssistantMockRun.mcpConnectionSkips`).

| Breaking change | Required action |
|---|---|
| None; the field is optional and missing on older servers. | None. |

## NewHeap.Platform.AI.Chat.AspNet: authentication modes of the application for MCP servers

An application adds its own authentication mode for MCP servers (D1), for example a token exchange
with its identity provider: implement the now public `INhAssistantMcpCredentialProvider` and register
it with `AddMcpCredentialProvider<TProvider>(mode, ownership)` on `NhAssistantBuilder`
(`NhAssistantMcpCredentialProviders`). `NhAssistantMcpCredentialOwnership` says whose credential the
mode sends: the organization's, shared and allowed in background runs with `headlessUse:
organization`, or each person's own, never shared and allowed with `headlessUse: personal` after the
person's consent. `NhAssistantMcpCredential` (with `Bearer`), `NhAssistantMcpCredentialSubject` and
`NhAssistantMcpChallenge` are public with it; `NhAssistantMcpCredentialProviders.Failed` and
`AuthorizePersonalCall` give a provider the built-ins' refusal and call rule. The provider is asked
before every request, renewed once after `401`, and its headers never reach a log. Administrators
choose the modes in the server editor; `GET admin/mcp-auth-modes` (`NhAssistantMcpAuthModeDto`) lists
them, and a server of such a mode takes no secret or OAuth settings.

| Breaking change | Required action |
|---|---|
| None; the types were internal and the endpoint is new. | Register an application's own mode with `AddMcpCredentialProvider<T>(mode, ownership)`; its name must not be a built-in mode. |

## @newheap/platform-ai-chat: authentication modes of the application

The MCP server editor lists the authentication modes the application registered in code after the
built-in modes, named by `nh-assistant.admin.mcp.auth.<mode>` or else by the mode, with a hint whose
credential they send, and offers the background use that fits them. `McpAuthMode` is now
`McpBuiltInAuthMode | string`; `McpBuiltInAuthMode`, `McpCustomAuthMode` and
`NhAssistantAdminApiService.getMcpAuthModes` are new. The mock API serves `GET admin/mcp-auth-modes`
from the scenario's `admin.mcpAuthModes` and validates servers of those modes as the server does.

| Breaking change | Required action |
|---|---|
| `McpAuthMode` admits any string, the application's own modes. | Use `McpBuiltInAuthMode` where only built-in modes fit, and treat other values as the application's own modes in exhaustive switches. |

## NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: one status per error code and error handling after the review

Every assistant endpoint (chat, administration, agent runs, subjects, run histories, boards, MCP
connections and the OAuth callback) now answers a failure with the one HTTP status the assistant gives
its code, and the error body carries the result's field errors for every code. New codes:
`assistant-subject-invalid` (`400`, a malformed subject reference), `assistant-webhook-payload-too-large`
(`413`), `assistant-mcp-connection-not-supported` (`409`), `assistant-mcp-secret-unreadable` (`409`) and
`assistant-mcp-secret-unavailable` (`503`); `NhAssistantPlacementReasons.NoAccountableOwner`
(`no-accountable-owner`) is a new reason of a proposal that is not placed. An invalid run history filter
names its parameters in `errors`. `POST threads/start` and a restart answer `202` for `waiting` (a paused
agent or skill) and `deferred` and a refusal as its code with its status.

Proposals of the whole application are listed in the inbox, counted in the subject summary and decided
only by people who pass the agent's view policy; to others they read as unknown. The status of its
background operations, its notifications and its live updates follow the same rule. The run maintenance
removes board visits older than `NhAssistantLimits.BoardVisitRetention` (default 180 days) and those of
groups that are no longer registered.

MCP: an approved proposal whose request never left the host or that the server refused without a
renewed credential ends `execution-failed`, not `execution-unknown`; a failed renewal after `401` reports
its own code; a chat approval of a tool whose server is left out reports the server's skip code. Test and
sync with an administrator's own credential no longer change a server's connection state. A dynamic
client registration is registered again when its secret expired or the authorization server forgot it.
Retention keeps a person's consent to background use. The OAuth callback reports failures with
`messageKey` (and `nh-mcp-message-key` in redirect mode) and never answers a bare `500`. Exceptions of
secret resolvers, delegated token issuers, revocation, the MCP maintenance and the health check are
logged with the exception at warning level.

| Breaking change | Required action |
|---|---|
| Statuses follow the code everywhere: `assistant-context-unavailable` is `403` on the subject endpoints (was `401`); a refused start by hand or restart answers `assistant-agent-stopped`, `assistant-agent-configured-off`, `assistant-agent-not-configured`, `assistant-skill-stopped` and `assistant-no-accountable-owner` with `409`, `assistant-agent-not-found`, `assistant-subject-not-found` and `assistant-disabled` with `404` and `assistant-trigger-rolled-back` with `503` (all were `400`); `POST conversations` without an agent and a join without a token answer `404`; `assistant-mcp-headless-not-allowed` is `409` and `assistant-mcp-credential-mismatch` `403`. | Map the error `code`, not the status; update clients and tests that expect the old statuses. |
| `NhAssistantTriggerReasons.RateLimited` is `assistant-trigger-rate-limited` (was `rate-limited`). | Compare with the constant; translate the new code. |
| A malformed subject reference answers `400 assistant-subject-invalid` instead of `assistant-subject-not-found`; a webhook body over 64 KiB answers `413 assistant-webhook-payload-too-large` instead of `assistant-webhook-payload-invalid`. | Handle the new codes. |
| `POST threads/open` answers the start's own refusal, for example `403 assistant-agent-forbidden`, instead of always `assistant-subject-access-denied`. | Handle `assistant-agent-forbidden`. |
| Proposals of the whole application need the agent's view policy (by default its required policy) to be listed and decided; an approver without it gets `404 assistant-approval-not-found`. | Give approvers of the whole application the view policy of the agents whose proposals they decide. |
| Saving an MCP server whose `secretReference`, `oauth.clientSecretReference` or `clientCertificateReference` names nothing answers `assistant-validation` with the field error `not-found` instead of `assistant-mcp-secret-reference-invalid` or `assistant-mcp-certificate-reference-invalid`. | Read the field error. |
| A stored secret that no longer decrypts fails with `assistant-mcp-secret-unreadable` (was `assistant-mcp-unauthorized` or `assistant-mcp-token-request-failed`); a secret resolver that throws fails with `503 assistant-mcp-secret-unavailable` (was `assistant-mcp-secret-reference-invalid`). | Enter an unreadable secret again; retry an unavailable one. |
| OAuth connection endpoints of a server without such a connection answer `409 assistant-mcp-connection-not-supported` (was `assistant-validation` with `authMode` or `serverId`, or `assistant-mcp-connection-required`); `GET` and `DELETE admin/mcp-servers/{id}/connection` of a server each person connects to act on the administrator's own connection. | Call the personal connection endpoints only for `oauth` servers with `connectionScope: user`. |
| OAuth callback failures: a callback without `state` is `assistant-mcp-authorization-invalid` (was `expired`), a removed server `assistant-mcp-server-not-found` and a changed issuer `assistant-mcp-issuer-changed` (were `expired`), and a failed discovery or client registration keeps its own code. | Handle the codes. |
| The built-in delegated token issuer throws `InvalidOperationException` without `INhAuthenticationService` and `ArgumentOutOfRangeException` for a lifetime outside (0, 5 minutes]. | Register the NewHeap authentication service or a custom issuer with `UseIssuer`. |
| A placed proposal whose object has nobody to answer for the agent reads `no-accountable-owner` (was `no-access`). | Handle the reason in rule handlers. |

## @newheap/platform-ai-chat: error handling after the review

Errors stay coded everywhere: downloads (`downloadArtifact`, `downloadRunArtifact` and the
administration's exports) reject with an `NhAssistantApiError` taken from the error body; a failed run
catalog, search, lookup, account name lookup or personal connection count shows its coded text with
Retry instead of an empty or "unknown" state; the MCP server editor and the runs view show the server's
field errors at their fields, and the editor's fields carry the server's `maxlength`. The run-now dialog,
the thread view, the subject suggestions and the run timeline show a start's receipt: `waiting` for a
paused agent as a warning with its reason, `deferred` as information and a refused start with its code.
A run's `failureCode`, such as `assistant-trigger-rolled-back`, is translated wherever it shows. The
connection card shows the outcome of a redirect-mode connection, and completing such a return reports
failures through `returned` instead of swallowing them. `getAccounts` asks in parts of 50 ids. The new
server codes have English and Dutch texts. The mock API answers every code with the server's status
from one table, caps account lookups at 50 ids, serves `POST runs/{id}/cancel`, `POST runs/{id}/retry`
and `POST threads/open`, answers a paused agent with `waiting` and a daily limit
(`NhAssistantMockRunNow.dailyLimit`) with `409 assistant-trigger-rate-limited`, and keeps proposals of
the whole application to people who pass the agent's view policy.

| Breaking change | Required action |
|---|---|
| `AgentTriggerReceipt.outcome` (`AgentTriggerOutcome`) no longer has `rejected` and has `waiting` and `deferred`; a refused start rejects with an `NhAssistantApiError`. | Handle `waiting` and `deferred` and catch the error for refusals. |
| Downloads reject with `NhAssistantApiError` instead of `Error` or `TypeError`. | Use `toNhAssistantError` for the text. |
| `downloadFailed` of the run result and artifacts components, `NhAssistantConnectionRequiredComponent.errorCode`, `NhAssistantAdminAgentsComponent.accountNames` and `NhAssistantAgentRunConfigurationComponent.accountName` are read-only computed signals. | Do not write them. |
| The translation key `nh-assistant.agent-runs.thread.outcome.rejected` is removed and new keys are added. | Update host overrides of the assistant's texts. |
| The mock API answers with the server's statuses and field errors (for example `400 assistant-subject-invalid`, `409 assistant-mcp-headless-not-allowed`, `409` for a stopped agent) and `NhAssistantMockRunNow.outcome` no longer accepts `rejected`. | Update host tests that assert the old mock answers. |

## NewHeap.Platform.AI.Chat.AspNet: an overview of an agent

`GET admin/tools` names the object type a tool declares with `[NhAiSubject]`
(`NhAssistantToolCatalogEntryDto.SubjectType`; `null` for tools without one and for MCP tools), so an
administration can say that a run's proposal of the tool waits at that object. `GET
admin/agents/{id}/configuration` names what each trigger in code runs for up front
(`NhAssistantCodeTriggerDto.SubjectType`: an object type, `nh-system` for a schedule for the whole
application, `null` when only the fire knows it) and how an agent in code runs
(`NhAssistantAgentConfigurationDto.CodeRunSettings`, `NhAssistantCodeRunSettingsDto`: whom it answers
to, whether it delegates from the initiator, its approval policy and whether it works by rules;
`null` for an agent created in the administration). All three are additions; existing clients keep
working.

| Breaking change | Required action |
|---|---|
| The JSON of `GET admin/tools` entries, code triggers and the run configuration gains `subjectType` and `codeRunSettings`. | Update tests that compare the whole JSON of these responses. |

## @newheap/platform-ai-chat: the flow view of an agent

The run configuration of `@newheap/platform-ai-chat/admin` (`NhAssistantAgentRunConfigurationComponent`)
opens with the agent's flow: an ordered overview of what it works on, when it runs (each trigger with
what it runs for, and whether and for what it can be run now), how it works (its rules or the model,
its skills and its tools with where a run's proposal of each waits, and whether its MCP servers serve
runs without a person by their background use and whom the agent answers to), whom it answers to,
who approves its proposals, and its results (its groups, who sees them for the whole application and
its latest runs). Problems of the run configuration, tool selectors that no longer match a tool, MCP
servers that runs without a person cannot use and an agent without run settings show as warnings at
their step with the entry point that fixes them. Each step opens the form it summarizes, which stays
below; run now opens the run-now dialog in place. The component has a new `servers` input (the MCP
servers, `null` while unknown) and the outputs `editAgent`, `changeAccount`, `openRuns` and
`openMcpServers` for what belongs to its host; `NhAssistantAdminAgentsComponent` handles them, returns
to the run configuration after the editor or the runs, and emits `openMcpServers`, which
`NhAssistantAdminComponent` answers with its MCP servers tab. The models gain
`ToolCatalogEntry.subjectType`, `CodeTrigger.subjectType` and `AgentRunConfiguration.codeRunSettings`
(`CodeRunSettings`). The mock API serves them from `subjectType` on the scenario's tools and code
triggers and `codeRunSettings` on its agents in code. An agent created in the administration may start
with `runConfiguration` (`NhAssistantMockRunConfigurationPreset` with `NhAssistantMockDataSkill` and
`NhAssistantMockDataTrigger`): the mock saves its run settings, skills and triggers through its own
endpoints, puts those with `shadow: false` live, and fails the scenario at once for a part it refuses,
so the administration shows such an agent's flow instead of the guided setup. The new texts are
bundled in English and Dutch.

| Breaking change | Required action |
|---|---|
| The run configuration shows the flow above its sections, and the list of problems above the skills became one notice; each problem shows at its step of the flow (`[data-flow-warning]`). | Update tests and styles that expect the problem list or select the run configuration's first section by position. |
| "Add trigger", "Add skill" and "Change run settings" appear in the flow as well as in their sections. | Scope tests that find these buttons by text to the section or the flow. |
| The translation key `nh-assistant.admin.run-config.problems.title` is removed and the keys under `nh-assistant.admin.run-config.flow` and `nh-assistant.admin.runs.back-to-run-config` are added. | Update host overrides of the assistant's texts. |
| A host that places `nh-assistant-admin-agents` or `nh-assistant-agent-run-configuration` itself gets entry points that ask it to act. | Handle `openMcpServers` of the agents page, or `editAgent`, `changeAccount`, `openRuns` and `openMcpServers` of the run configuration, and pass `servers`. |

## @newheap/platform-common: modals without sideways scrolling

`nh-modal` sizes the modal by its holder instead of the viewport: the holder's single grid column is
its content box, which excludes its scrollbar, and the modal's maximum width is 95% of it, all of it at
920px and narrower, and all of the narrowed holder at 768px and narrower, instead of `95vw`, `100vw`
and `calc(100vw - 30px)`. A modal taller than the page, such as a large edit form, scrolls vertically
without scrolling sideways by the scrollbar's width.

| Breaking change | Required action |
|---|---|
| The maximum width of `.nh-modal` is a percentage of `.nh-modal-holder` (95%, or 100% at 920px and narrower) instead of `95vw` or `100vw`, so a modal can be up to a scrollbar's width narrower. | Remove host styles that compensated for the sideways scroll; set an own `max-width` on `.nh-modal` only when content must use the viewport's width. |

## @newheap/platform-ai-chat: accounts by name in the administration

The administration shows a person or account by name only. The flow view's account step and the run
settings summary show the technical account's name with its account ID as the name's tooltip and a
button that copies it (the ID shows next to the name only when the browser refuses the clipboard);
sentences that name an account carry its ID as their tooltip. While no name is known (an unknown
account, a lookup in flight or a failed lookup) the ID shows labeled as an account ID. The
`agent-runs` building blocks follow the same rule for who decided or overtook a proposal, who asked a
consultation and the author of feedback. The new texts are bundled in English and Dutch.

| Breaking change | Required action |
|---|---|
| The account ID no longer shows next to a known account name, and an ID without a name reads "Account ID …" or "account ID …" instead of the bare ID. | Update tests that read an account's ID from the text; read the tooltip (`title`) instead. |
| The translation keys `nh-assistant.account-id`, `nh-assistant.account-id-title` and `nh-assistant.admin.account` are added. | Update host overrides of the assistant's texts. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql and NewHeap.Platform.AI.Chat.AspNet: results in the reader's language

A run works in the application's default culture, so a rule handler's summary used to read in that
language for everyone. `NhAssistantRuleResult.Done(Func<CultureInfo, string>)` lets a rule handler
write it per culture: the platform asks once in the run's culture, which the thread and the agent's
memory keep, and once per supported UI culture with that culture current, the rationales of its
proposals included. The run keeps its result per culture in `AssistantRun.ResultsJson`, and `GET
runs/{id}` (`NhAssistantRunResultDto.Result`), `GET runs` and `GET admin/agents/{id}/runs`
(`NhAssistantRunHistoryDto.Result`), `GET subjects/{type}/{id}/agents` (`NhAssistantLatestRunDto.Result`)
and the boards return each reader the result in the request's culture: the exact culture, its parents
or another culture of the same language, and otherwise the run's own culture. A model's answer and a
result written with `Done(string)` read as written.

| Breaking change | Required action |
|---|---|
| Migration `AgentRunLocalizedResults` adds `ResultsJson` to `AssistantRun`. | Apply it for SQL Server or PostgreSQL. |
| The result of a run whose rules write it per culture reads in the request's culture instead of the run's culture. | Send the reader's language as `Accept-Language`; update tests that expected the run's culture. |

## @newheap/platform-ai-chat: results in the reader's language

The run results, run histories, object agents and boards show a result whose rules wrote it per
culture in the language the client sends as `Accept-Language`. The mock API takes such a result as
`results` (per culture) on `NhAssistantMockRun` and `NhAssistantMockRunNow` and answers each request in
its language, with `result` for other languages.

| Breaking change | Required action |
|---|---|
| `NhAssistantMockRun` and `NhAssistantMockRunNow` gain the optional `results`; the mock answers runs, histories, object agents and boards in the request's language when a run has them. | None; scenarios without `results` read as before. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: live updates for the approvers of the whole application

The whole application lists no members, so the runs and proposals of its agents reached their
approvers and readers only when a screen reloaded. A client that shows that work now watches it on the
assistant hub: `NhAssistantHub.WatchAgentWork()` joins the connection to the work of every agent for
the whole application, per agent and scope (`nh-system/global` or a division), whose thread there the
person reads, that is the read role of the whole application and the agent's view policy, by default
its required policy, at most 200, and answers `NhAssistantLiveWatchDto` (`Watched`,
`RefreshAfterSeconds`, 300). Work the person may no longer read is left whenever the client renews the
watch; `UnwatchAgentWork()` stops it. A person bound to a tenant watches nothing. Watchers receive
`AgentWorkChanged`, which carries ids only; every read checks access again.

| Breaking change | Required action |
|---|---|
| The assistant hub accepts the client calls `WatchAgentWork` and `UnwatchAgentWork`; until now clients sent nothing to it. | None; allow them if a gateway filters hub invocations. |
| An agent's work for the whole application reaches the connections that watch it as well as the people of its thread. | None; a client that handles `AgentWorkChanged` reloads as before. |

## @newheap/platform-ai-chat: live updates for the approvers of the whole application

`NhAssistantLiveService.watchAgentWork()` watches the work of agents for the whole application until
the function it returns is called: it asks the hub when connected, renews the watch when the server
says and after every reconnect, and stops with the last screen that watches. `nh-assistant-agent-inbox`
and `nh-assistant-agent-board` watch while they are shown, so a proposal of a run for the whole
application reaches its approvers' inbox and a board without a reload. The model `LiveAgentWorkWatch`
describes the hub's answer. The mock API delivers agent work through `NhAssistantMockLiveService`
(`watchAgentWork`, `watching`), announces runs by hand and decisions, and offers
`NhAssistantMockBackend.publishAgentWork(change, agentId)` for work a background run would change;
work of the whole application reaches the caller only while watched and when the caller passes the
agent's view policy.

| Breaking change | Required action |
|---|---|
| `NhAssistantLiveService` gains `watchAgentWork`, and the mock live service delivers `agentWork$` instead of never emitting. | Add `watchAgentWork` to own replacements of the live service; update tests that expected no agent work from the mock. |
| `NhAssistantMockLiveMessage` gains the `agent-work` kind. | Handle or ignore it in own consumers of `NhAssistantMockBackend.live$`. |
