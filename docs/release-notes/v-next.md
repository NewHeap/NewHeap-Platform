# v-next

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
A run started by hand uses only the starter's own personal MCP connection, with their consent.

| Breaking change | Required action |
|---|---|
| Starting an agent by hand on the whole application (`POST threads/start`, a new thread of `POST threads/open`, `canStart`) follows the agent's `RunPolicy` (by default the administration policy) instead of the approve policy of the whole application. | Give the people who run such agents the run policy, or name a run policy with `RunPolicy` that they pass. |
| `canStart` of `GET subjects/{type}/{id}/work` also checks the scope of the whole application and the run policy, as the start does. | None; a start that the server refused before now reads `canStart: false`. |
| The run catalog reports `Searchable: true` for `nh-system` when the application lists its divisions, and its search finds divisions. | Do not treat a searchable `IsSystem` type as one with objects to search. |
| A run started by hand that acts for someone else, such as the account an agent with `ActWithAccountableRights` answers to, no longer uses that person's personal MCP connection; the server is left out with `assistant-mcp-initiator-only`. | Use `DelegateFromInitiator()` when runs started by hand should use the starter's own connection, or an organization credential. |

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
`runNow` (`NhAssistantMockDivision`, `NhAssistantMockRunNow`). New texts, among them the error
`assistant-mcp-initiator-only`, are bundled in English and Dutch.

| Breaking change | Required action |
|---|---|
| The mock API answers `POST threads/start`, `GET threads`, `GET threads/{id}/runs`, `proposals`, `memory`, `artifacts` and `consultations`, and `GET subjects/{type}/{id}/agents`, instead of `404`. | Update tests that expected `404` from these paths; script results with `runNow`. |
| The agent cards of the administration show "Run now" for agents that run on their own. | Update tests and styles that select card buttons by position. |
| Fields and buttons of the assistant's forms use `box-sizing: border-box`. | Remove host styles that compensated for the overflow. |
| The mock's `nh-system` type is `searchable` when the scenario lists `divisions`. | Leave `divisions` out of scenarios that expect no division search. |
| `NhAssistantStore.openConversation` and `NhAssistantPanelService.open` open an agent's thread also for an agent kept out of the chat, without choosing that agent in the picker; before, the panel stayed empty. | None; hosts that hid such threads on purpose check `kind` and the agent themselves. |

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
