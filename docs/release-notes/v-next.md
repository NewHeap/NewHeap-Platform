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
subject with its division; a division subject also needs `INhDivisionAccess`. A manual start for the
whole application also needs the agent's `RunPolicy` when it has one. The whole application lists no
members, so the approval inbox shows its proposals to their initiator only.

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
| A manual start (`POST threads/start`, `POST threads/open`) for the whole application also checks the agent's `RunPolicy`. | Give the people who start such agents the run policy as well. |

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
