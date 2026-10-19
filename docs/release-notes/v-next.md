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
