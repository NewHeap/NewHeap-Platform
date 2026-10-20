# Agents Working on Application Objects Plan

This document plans how agents of `NewHeap.Platform.AI.Chat` and `@newheap/platform-ai-chat` work on
the objects of an application (quotes, assignments, orders, projects) and on the application as a
whole, how people start them by hand, where their proposals and results appear, and how an
application groups them on its own pages. It was written on 2026-10-19 during the review of the
[run configuration UX](ai-agent-run-configuration-ux-plan.md) and checked against
`feature/run-configuration-ux`.

## Status

Accepted on 2026-10-19: Lars took decisions K1 to K16 below. W1 to W5 are implemented and not
released; W6 and W7 are designed and not yet implemented; the flow view (U6 of the run configuration
UX plan) follows on top of them. The
[MCP connections plan](ai-assistant-mcp-connections-plan.md) uses SPM-299 and SPM-300; this plan
uses SPM-301 onwards.

| Phase | Status |
| --- | --- |
| W1 | Implemented, not released: object types with names, entity binding, search and links. |
| W2 | Implemented, not released: what an agent works on. |
| W3 | Implemented, not released: the whole application as a scope. |
| W4 | Implemented, not released: run now. |
| W5 | Implemented, not released: proposals placed at their object. |
| W6 | Designed: results and history. |
| W7 | Designed: groups and boards. |
| U6 | Designed in outline: the flow view of an agent. |

## Problem

An application wants agents that help with its own objects without code for every case:

- **An agent belongs to objects.** "This agent works on quotes and orders" cannot be said. Object
  types have no name for people, no link to the entity they stand for and cannot be searched.
- **Nothing runs for the application as a whole.** A weekly statistics agent has no place: every run
  and thread needs an object.
- **Nobody can give an agent a push.** People can start an agent for one object only where an
  application embeds the thread view; the administration and system-wide runs have no start at all.
- **A proposal cannot be placed at another object.** A weekly run that finds three projects to
  complete cannot leave "complete project X?" at project X.
- **Results disappear.** A run keeps a 600-character summary in its thread; there is no list of an
  agent's runs, no latest result and no history outside one object's thread.
- **Agents cannot be grouped.** An application cannot build a page that shows its agents of one
  area with their latest results, history and a start button.
- **Divisions are assumed too easily.** Many applications run app-wide; divisions must stay an option
  that an application turns on, not a precondition.

The target example: an agent that counts the open projects every Monday and, when there are more
than three, proposes which ones can be completed. People see the count on a page of the
application, find each proposal at its project and in their inbox, approve it there, and can run the
agent again by hand.

## What exists

| Area | Today |
| --- | --- |
| Object types | `NhAssistantBuilder.AddSubjectType<TResolver>()` registers an `INhAiSubjectAccessResolver` per type: roles per actor, `DescribeAsync` (display name, scope key, division, responsible actor) and members. No type name for people, no entity binding, no search. Subject types are dash-case (`NhAiSubjectRef.IsValidType`). |
| Threads | One thread per agent, subject and scope key (`AssistantThread`, unique index on scope key, agent, subject type, subject id and shadow). The scope key is `tenant:{id}`, `division:{id}` or `global` (`NhAiSubjectInfo.CreateScopeKey`). |
| Manual start | `POST threads/start` for one subject (participant role, the agent's required policy, shadow only for administrators), used by `nh-assistant-agent-thread-view`. Not in the administration; never without a subject. |
| Schedules | Cron triggers over a subject set: one run per subject per tick (`NhAssistantScheduledTriggerJob`). No run without a subject. Subject types of triggers in code are only known at run time (`Subject(Func<TEvent, NhAiSubjectRef>)`). |
| Proposals | Belong to the thread of the run that made them; their targets are the invocation's execution scopes (`NhAssistantToolCallInterceptor`). Hand-off to another agent is the only path to another subject. Inbox: `GET approvals/inbox`, `nh-assistant-agent-inbox`. |
| Results | `AssistantRun.Summary` (600 characters, `NhAssistantRunSummaries`) per thread through `GET threads/{id}/runs`; artifacts per thread. No runs per agent, no runs in the administration. |
| Divisions | `INhDivisionAccess.HasAccessAsync` only; nothing lists divisions. |
| Grouping | None. |

## Principles

- **Divisions are optional.** Everything works app-wide by default. An application that uses
  divisions can make an agent work per division; screens offer division choices only when the
  application lists its divisions.
- **One way to say what an agent works on**, in code and in the administration, used by triggers,
  manual starts, proposals and pages alike.
- **The whole application is an object too.** A built-in object type carries system runs, so
  threads, memory, artifacts, run timelines, retention and budgets work unchanged.
- **Proposals stay governed.** Placing a proposal at an object checks that object for the agent's
  account, and the object's approvers decide, as for every proposal today.
- **Existing agents keep working** without changes.

## Decision record

| # | Decision | Reason |
| --- | --- | --- |
| K1 | **Object type names are texts per language in code** with a fallback language: `.Named(new() { ["en"] = "Quote", ["nl"] = "Offerte" }, fallbackLanguage: "en")`, optionally a plural the same way. The server answers in the request's language. | Lars's choice: the names live where the object type is configured. |
| K2 | **Optional entity binding**: `.ForEntity<Quote>()` records the short and full type name, as `NhLog` does with `ObjectType` and `ObjectTypeFull`. | Logs, audit and the assistant point at the same object without making the binding mandatory. |
| K3 | **Objects are searched through their resolver**: an optional `INhAiSubjectSearch` on the same class. Without it the screen asks for the id and shows the name `DescribeAsync` gives. | One place per object type for access, description and search. |
| K4 | **An optional link template per object type on the server** (`.OpenAt("/quotes/{id}")`), usable in notifications; the Angular configuration may override it with a router function. | Links work in every screen and later in e-mail. |
| K5 | **"Works on" is explicit and enforced**: object types, all object types and/or the whole application, in code and in the agent editor. Triggers, manual starts, proposals and pages offer only what it lists. | One statement drives every place where an agent can start or act. |
| K6 | **Existing agents keep working.** "All object types" is a valid choice of its own; an agent that does not say what it works on works on all object types and not on the application. The administration shows the types it is known to work on (hand-offs, data triggers, existing threads) as a hint. | Subject types of triggers in code are only known at run time, so an exact derivation at startup is not possible; "all object types" is what such an agent does today. Lars: "all is allowed" must be a valid choice. |
| K7 | **App-wide by default.** An agent can be set to work per division when the application lists its divisions; its system runs, threads and pages then exist per division. | Divisions are an option, not a precondition (Lars). |
| K8 | **The whole application is the built-in object type `nh-system`.** The prefix `nh-` is reserved for the platform: an application cannot register its own `nh-` type. People see it as "The whole application". | No change to the thread model and no clash with an application's own types such as `application`. |
| K9 | **Running by hand**: for an object the existing rule (participant of the object and the agent's required policy); for the whole application a run policy per agent, by default the assistant's administration policy. | Objects keep their own access; system runs are an administrative act unless an application decides otherwise. |
| K10 | **Run now in three places**: the administration (per agent, with an object picker or "the whole application"), a board tile and a component for an object's page. | People start agents where they work. |
| K11 | **A proposal for another object goes into the thread of the same agent at that object**, created when needed, with a link to the run that made it. The object's approvers decide; it appears in their inbox and on the object's page. A run places at most 20 such proposals (configurable); duplicates are refused as today. | The proposal is where people look for the object, with the object's history and approvers. |
| K12 | **A tool declares which argument names which object type** (`[NhAiSubject("project")]` on the input property); the platform checks the object before placing the proposal. Tools without it, MCP tools included, propose in the run's own thread. | The platform does not trust the model to say which object a call concerns. |
| K13 | **A run keeps its full answer** as Markdown up to 20,000 characters next to the summary that feeds the agent's memory; files stay artifacts. | Results are worth reading; the summary keeps memory small. |
| K14 | **Who sees results**: runs on an object, whoever may read the object; runs for the whole application, whoever passes the agent's view policy (by default its required policy); administrators see every run of an agent in a runs view with filters. | Results follow the access of what they are about. |
| K15 | **Groups in code with names per language (K1)**; agents join a group in code or in the administration; `nh-assistant-agent-board` shows one group anywhere in the application. | Applications build their own pages without a dashboard designer. |
| K16 | **No notifications yet**: a board marks what is new since the person's last visit. Notifications follow later on the notification module. | Enough for a first version; notifications need their own rules. |

## Implementation decisions

Choices the plan left open, taken during implementation for Lars to review.

| Phase | Decision | Reason | Review |
| --- | --- | --- | --- |
| W1 | The sample links a project to `/management/intake-reviews?project={id}`, and that page now selects the project of the link. | The management portal has no project detail route; the intake reviews page is where it shows one project's agent work. | For Lars to review. |
| W1 | "A scope the caller may use" in the search is the caller's tenant, as for threads and proposals; divisions follow `ResolveAsync`. | `INhDivisionAccess` denies every division without an implementation, so applications without divisions would find nothing. | For Lars to review. |
| W1 | The search reads at most four results per result it may show and then stops; `limit` is clamped to 1 to 50 like the account search instead of refused. | Bounds the resolver calls per keystroke when an application's search ignores the actor; matches `GET admin/accounts`. | For Lars to review. |
| W1 | `SubjectTypeName` in the thread, proposal, work and conversation DTOs is `null` for a type without `Named`; the catalog's `Name` falls back to the id. | Screens of applications that never named their types stay as they were; the administration always needs a label. | For Lars to review. |
| W1 | A configuration belongs to its resolver class, because the type id comes from the resolver instance. A repeated parameterless registration keeps the configuration; two different configurations of one class fail at registration; "a configuration for a type without a resolver" is a configured class that is no longer registered as `INhAiSubjectAccessResolver`. | `AddSubjectType<TResolver>(configure)` has no type id until a resolver exists. | For Lars to review. |
| W1 | Startup checks (`nh-` prefix, invalid type, two resolver classes for one type, configuration without resolver) run in the ASP.NET startup validator; a host without it keeps the old "last resolver wins" at runtime. | The core registers no hosted services; all agent-run hosts use the ASP.NET integration. | For Lars to review. |
| W1 | Names are looked up by exact culture, parent cultures, another culture of the same language, then the fallback language; culture names are checked by syntax and compared case-insensitively. | A request for `nl` finds a name registered as `nl-NL`, as the presentation cultures do. | For Lars to review. |
| W1 | `OpenAt` accepts `{id}` only in the path or query of an `https` URL, never in its host, and a relative link must start with exactly one `/`; no other braces. Clients link only within the application or over `https`. | A subject id must never choose the host a link leads to. | For Lars to review. |
| W1 | The search answers `{ subjectType, items }` and adds each item's `link`. | Room for paging later; the picker and later e-mail need the link. | For Lars to review. |
| W1 | Without search, the internal subject picker looks an id up through the existing `GET subjects/{type}/{id}/work`, which gives readers `DescribeAsync`'s name; the mock API serves a minimal version of it for its scenario subjects. | No new endpoint for one name; the access rule is the same. | For Lars to review. |
| W1 | Names and links show in the proposal cards (inbox and thread view) and the thread view's heading through an internal link component; the feedback report and run DTOs are unchanged (a run's subject is its thread's). | Where subjects are listed today; runs carry no subject of their own. | For Lars to review. |
| W1 | `provideNhAssistant({ subjectLinks })` takes a builder per subject type that returns router commands, a URL, `null` (no link) or `undefined` (the server's link) and runs in the assistant's injection context. | One override per type keeps the server's links for the rest. | For Lars to review. |
| W1 | The sample's project search is provider-neutral `ToLower` and `Contains`; it runs on PostgreSQL in the sample tests and is checked as translated SQL for SQL Server. | The sample test project has no SQL Server container; the library's search filtering runs on both providers. | For Lars to review. |
| W1 | W1 changes no schema, so it has no migrations. | Names, entities and links live in code. | For Lars to review. |
| W2 | `NhAiSubjectRef.SystemType` (`nh-system`) is added to AI.Common now; W3 registers its resolver. | The one rule needs the type to decide whether an agent works on the whole application. | For Lars to review. |
| W2 | The column of the object types is `WorksOnSubjectTypesJson`, like `ToolSelectorsJson`; `WorksOnAllSubjects` defaults to `true` in the database with an EF sentinel, so existing rows work on all object types. | Repository naming of JSON columns; the migration must give existing agents "all object types". | For Lars to review. |
| W2 | Decided by the implementer on 2026-10-20, for Lars to review: after merging the [MCP connections](ai-assistant-mcp-connections-plan.md) phases M1 to M4, whose `McpConnectionPolicy`, `McpServerCredentials` and `McpOAuthConnections` migrations have earlier timestamps, `AgentWorkOnObjects` was regenerated with the EF CLI on top of `McpOAuthConnections` for both providers, with the same name and new timestamps (`20261020003103` for SQL Server, `20261020003111` for PostgreSQL). Its `Up` and `Down` are unchanged. | The old designer models lacked the MCP tables and columns; regenerating keeps every migration's target model correct. None of these migrations was released. | For Lars to review. |
| W2 | What a data agent works on belongs to its definition (`PUT admin/agents/{id}`), not to its run settings; the run configuration snapshot carries it into the agent's run options, so the rule reads memory only. An agent without run settings cannot run and reads "all object types" at runtime. | One statement for chat and runs; triggers and starts check without a database read. | For Lars to review. |
| W2 | The six fields travel together: all omitted keeps the statement (a new agent works on all object types); otherwise omitted ones read as not chosen, and without types, all objects or the application the agent works on all object types. | Older clients keep working; a request that only sets a policy does not make the agent work on nothing. | For Lars to review. |
| W2 | Field errors: `worksOn` `required` (nothing) and `invalid` (types next to all objects); `worksOnSubjectTypes` `invalid`, `not-found` (unknown or `nh-`) and `too-long` (over 20); `perDivision` `invalid` without divisions; `runPolicy` and `viewPolicy` `not-found` when the application does not list them. Agents in code may name any registered policy. | Errors at the editor's fields; data names only what code offers. | For Lars to review. |
| W2 | The rule is checked in the one place every fire passes (`FireCoreAsync`), and `PublishAsync` refuses at once instead of deferring a refused fire; follow-ups and resumes are refused too, and waiting inputs of a type no longer worked on are dropped like those of a deleted subject. | Every start follows one rule; no background operation for work that will be refused. | For Lars to review. |
| W2 | `POST threads/start` and `POST threads/open` (new thread) answer `409 assistant-agent-does-not-work-on`; `canStart` is `false` in the thread lookup and the subject work; existing threads stay readable. | It is the agent's configuration, not the caller's rights. | For Lars to review. |
| W2 | Startup fails for `AcceptHandOffs`, `AllowConsultation` and typed triggers on a type the agent does not work on (core check), and for unknown `WorksOn` types and unregistered run, view or listed policies (ASP.NET check, before the background-operation check). | Contradictions in code surface at deployment, as K5 asks. | For Lars to review. |
| W2 | A data trigger on an event that names its type is refused for an agent that does not work on it (`event: invalid`); one that no longer fits after the statement narrowed stays stored but is out of effect with the problem `not-worked-on`. | The pattern for data that no longer fits; nothing is deleted behind the administrator's back. | For Lars to review. |
| W2 | `PerDivision()` in code is accepted without a division directory; data refuses it until the run catalog reports `divisions`, which stays `false` until W3. | K7 offers per division only with listed divisions; W3 adds the directory and its startup check. | For Lars to review. |
| W2 | Known types come from the run configuration in effect (hand-offs, consultations, typed triggers in code and as data) and the distinct thread types of the agents (one query, at most 2,000 pairs), for every agent in `GET admin/agents`. | The hint of K6 without a query per agent. | For Lars to review. |
| W2 | The admin DTO carries the statement as flat fields like the input, with `WorksOnDeclared` (false only for an agent in code that says nothing) and `KnownSubjectTypes`. | The Angular `AdminAgent` extends `AdminAgentInput`; data agents always declared what they work on. | For Lars to review. |
| W2 | The trigger forms offer an event that names its type only to agents that work on it; the editor refuses "chosen kinds" without any kind or the application before calling the server; cards and check boxes use the plural names. | K5: forms offer only what the agent works on. | For Lars to review. |
| W2 | SPM-302 is registered as `implemented` for what W2 builds, with a title and outcome scoped to it; W3 to W7 extend its title, surface and outcome. The dev seeder creates the project follow-up as data (projects and the whole application, run and view policies, run settings answering to the operations account); the starter composition lists the agent policies, because the other sample hosts do not register those authorization policies. The refused start is shown by narrowing the follow-up to the whole application, since the sample has one application object type. | The guidance generator accepts only implemented cases in consumer guidance (`partial` is refused there), and the plan's target agent starts in W2 with what it works on. | For Lars to review. |
| W3 | The `nh-system` resolver lives in `NewHeap.Platform.AI.Chat.AspNet` and `AddNewHeapAssistant` registers it; only a resolver with the platform's internal marker may take a reserved type, so an application's `nh-` resolver is still refused at startup. | It needs `INhAiPrincipalFactory` and ASP.NET authorization; every agent-run host uses the ASP.NET integration (W1). | For Lars to review. |
| W3 | `UseSystemSubject(system => system.ReadPolicy(...).ApprovePolicy(...))`: readers by the read policy (default the access policy), participants and approvers by the approve policy (default the administration policy). `ReadPolicy` is an addition to the plan. | An access policy that needs an active division, like the sample's, can never be met app-wide, so nobody could read the whole application. | For Lars to review. |
| W3 | Roles are evaluated for the principal `INhAiPrincipalFactory` rebuilds, against `NhAiSubjectAuthorizationResource` of the subject (no division app-wide, the division otherwise); a division subject also needs `INhDivisionAccess`; the resolver keeps roles 10 seconds per scope (at most 256). | The brief asks for the headless principal, not the request; a division policy must not grant app-wide access; listings ask once per person. | For Lars to review. |
| W3 | `ListMembersAsync` of `nh-system` returns nothing. | Who passes a policy cannot be listed without the user store; the inbox and notifications therefore show system proposals to their initiator only, and the screens of W4, W6 and W7 show them in place. | For Lars to review. |
| W3 | Threads of `nh-system/global` store the English name; the thread, proposal, work and conversation-thread DTOs name it in the reader's language. Division threads store the division's name. | A schedule's culture must not decide the stored name; readers see their own language. | For Lars to review. |
| W3 | Public `NhAiSystemSubjects` in AI.Common: `Global`, `ForDivision(id)`, `IsSystem`, `IsGlobal` and `TryGetDivision`, which accepts only the canonical lowercase `N` id. | One division has one subject and one thread; W4 and applications need the ids. | For Lars to review. |
| W3 | The one rule also checks the scope: `NhAssistantAgentWorksOn.Covers(NhAiSubjectRef)` lets an app-wide agent work only on `nh-system/global` and a per-division agent only on a division; fires, manual starts and hand-offs use it. | K7: system runs exist app-wide or per division, never both. | For Lars to review. |
| W3 | A schedule without subjects names `nh-system` as its type up front, so the startup check of typed triggers covers it, with its own message; a data schedule without a subject set for an agent that does not work on the application answers `subjectSet: required`, and one that no longer fits is out of effect with `not-worked-on`. | One check for every trigger; the field the administrator must fill. | For Lars to review. |
| W3 | A per-division tick takes the first `MaxSubjectsPerTick` divisions by name and logs the limit (it reads one more); it does not rotate. | The same as subject sets; per-division agents with more than 100 divisions raise the limit. | For Lars to review. |
| W3 | `INhDivisionDirectory` has `ListAsync(limit)` (1 to 1,000, by name then id) and `GetAsync(ids)`, and no generic default: no directory means no listed divisions (`IServiceProviderIsService`). NewHeap Identity's reads `Name` through `EF.Property`, so `AddIdentityActorDefaults<TUser, TDivision>` needs only `IdDbEntity`; a division without a name shows its id. | Divisions are optional (K7); the identity entity types are deeply generic. | For Lars to review. |
| W3 | A system run's invocation context has no subject execution scope; a per-division run gets the division through the existing active-division mapping (`AddActiveDivisionScope`), and none without it. | The plan: no object, and the division as a person's active division. | For Lars to review. |
| W3 | A manual start on `nh-system` (`POST threads/start`, `POST threads/open`, `canStart`) also needs the agent's `RunPolicy` when it names one, next to the participant role. | A narrower run policy is never bypassed before W4 replaces the participant role by the run policy (K9). | For Lars to review. |
| W3 | W3 changes no schema, so it has no migrations. | Scope keys, subject columns, `PerDivision` and nullable subject sets exist. | For Lars to review. |
| W4 | Decided by the implementer on 2026-10-20, for Lars to review: on `nh-system` a start by hand needs the agent's run policy (by default the administration policy) and the reader role of the read policy; the approve policy gives no start. This replaces W3's participant role plus run policy. | K9; a person who could start but not read would get a receipt to a thread they cannot open, and the read role carries the division access check. | For Lars to review. |
| W4 | The run policy is evaluated like the roles of the whole application: for the principal `INhAiPrincipalFactory` rebuilds, against the subject's `NhAiSubjectAuthorizationResource`, with division access in a division, not for the request's principal as in W3. | One way to evaluate every policy of the whole application; a division policy never grants an app-wide start. | For Lars to review. |
| W4 | The agent's required policy still applies to starts on the whole application. | K9 adds the run policy; who may use an agent is never widened. | For Lars to review. |
| W4 | One internal service, `NhAssistantManualStarts`, holds the rule for `POST threads/start`, a new thread of `POST threads/open` and `canStart` of `GET threads`, the subject work and the agents of a subject. A refused new thread keeps answering `403 assistant-subject-access-denied` (`409` for what the agent works on). | One rule, checked through `NhAssistantWorksOnRule` with its scope, in every place people start or see a start; existing clients see the same codes. | For Lars to review. |
| W4 | `GET subjects/{type}/{id}/agents` lists agents with run settings that work on the subject (scope included), are neither stopped nor disabled in code, whose required policy the caller passes, and shadow variants for administrators only, ordered by id. Agents whose old threads no longer fit what they work on are not listed. | The plan's "work on that object and that the caller may use"; their threads stay readable through the subject work and the thread lookup. | For Lars to review. |
| W4 | The latest run is the newest run, by creation, in the agent's visible threads of the subject's scope (shadow threads for administrators), with status, time (ended, started or queued), failure code and the first 300 characters of the run's summary, never cutting a character in two. `NhAssistantRunResults.PreviewOf` is the seam W6 changes to read the stored result first. Open proposals are pending and unexpired. Two queries serve all agents of a subject. | The full result arrives in W6; one place changes then. | For Lars to review. |
| W4 | The listing is an internal service behind the endpoint, not a new member of the public `INhAssistantSubjectWork`. | A new interface member breaks implementations and test doubles; W7's board can use the service in process. | For Lars to review. |
| W4 | The system resolver searches the divisions (`INhAiSubjectSearch`), offered only with a division directory (the internal `OffersSearch` of platform resolvers), so the run catalog's `nh-system` reports `Searchable` with listed divisions. It filters the directory's first 1,000 divisions by name, case-insensitively, and the search service keeps the ones the person reads; a caller bound to a tenant finds none, because the whole application has no tenant. | The run-now dialog chooses a division for a per-division agent through the existing search and picker. | For Lars to review. |
| W4 | MCP: a run started by hand (trigger `manual`) uses a personal connection only of its initiator, with the initiator's consent (Q3). When the person whose rights the run uses is someone else, such as the account an agent with `ActWithAccountableRights` answers to, servers with personal headless use are left out with the new code `assistant-mcp-initiator-only`; organization credentials are unaffected. Runs from agent requests and queued messages are not covered. | Never someone else's personal account at a moment another person chose; the whole application has no members to fall back to. Recorded in the MCP plan as I18. | For Lars to review; extending the rule to agent requests and queued messages is a follow-up. |
| W4 | The run-now dialog is an internal building block of the `agent-runs` entry point (`ɵNhAssistantRunNowDialogComponent`), a native modal `dialog`: focus stays inside, Escape closes it and focus returns to its opener. The subject picker moved with it from the root entry point (`ɵNhAssistantSubjectPickerComponent`), and the `admin` entry point now imports both from `agent-runs`. The administration, `nh-assistant-object-agents` and W7's board tile use it. | Reusable without new public API; the platform's modal behavior without a focus trap of our own. In the root entry point the dialog and picker pushed the sample's initial bundle 10.77 kB over its 1.70 MB budget; `agent-runs` loads with the pages that need it. | For Lars to review. |
| W4 | The receipt opens the agent's conversation in the assistant panel (the administration calls `NhAssistantPanelService.open`, the object agents component emits `openConversation`) and names the subject with its link. | There is no page of one run before W6. | For Lars to review. |
| W4 | The internal subject picker takes the platform's type: app-wide it is "The whole application" itself, an `output` that the field's label names, chosen as soon as the type is; per division a combobox that searches the divisions. The dialog offers the whole application first, then the agent's kinds of objects, all registered kinds for an agent that works on all objects. | The brief's whole-application option without a second picker. | For Lars to review. |
| W4 | The dialog accepts an instruction of at most 4,000 characters, like the thread view's start; the server accepts 8,000. | The same limit wherever people start an agent. | For Lars to review. |
| W4 | `AssistantWebApplication` takes a provider, so the start authorization and the agents of a subject are tested over HTTP on SQL Server and PostgreSQL. | The rules live in the endpoints; PostgreSQL alone was not enough evidence. | For Lars to review. |
| W4 | The mock API runs no agents: `POST threads/start` checks who may run the agent as the server does and records a run that ends at once as the scenario's `runNow` says; `callerPolicies` defaults to every policy; the thread's proposals, memory, artifacts and consultations are empty. The mock administration was split per area first (agents, MCP, accounts; the run configuration was already apart), with the account search moved out of the run configuration. | The playground and the specs exercise run now and the thread view without a back-end; both lanes edit smaller files. | For Lars to review. |
| W4 | The shared `_forms.scss` sets `box-sizing: border-box` on fields, buttons and messages of the assistant's components; a spec checks a form at 375px on a page without a global rule. | The W3 finding: fields overflowed 22 to 48px on a bare page. | For Lars to review. |
| W4 | Sample: `nh-assistant-object-agents` sits in the project editor below the agent suggestions, deferred; opening a conversation closes the editor first. The playground adds the follow-up as a data agent with run-now results. SPM-303 covers the administration and the project page; the board follows in W7. | The editor is where the portal shows one project; the panel is a drawer below the modal's layer. | For Lars to review. |
| W4 | The panel opens an agent's thread conversation also for an agent kept out of the chat (`HideFromChat`, `offeredInChat: false`), and then leaves the agent picker as it was; a chat conversation of such an agent still does not open. | The receipt of a run by hand, as for the project follow-up, and the thread view's "Open conversation" led to an empty panel; the server already checks the reader's role on the thread. | For Lars to review. |
| W4 | W4 changes no schema, so it has no migrations. | Runs, threads and proposals hold everything the listing shows. | For Lars to review. |
| W3 | The sample reads the whole application with `app.project.view` and keeps the administration policy for approvers; the follow-up's Monday schedule (`project-follow-up-weekly`, 07:00 Europe/Amsterdam) is seeded live without a skill, app-wide. Per division is not shown in the sample beyond the directory and `divisions: true`. | The sample's access policy needs an active division; the counting skill and placed proposals belong to W5; per-division adds no value for the follow-up. | For Lars to review. |
| W3 | The trigger form and the guided setup ask with a radio choice what a schedule runs for ("The whole application" or "Every object in a set"), defaulting to the whole application for an agent that works on it; a server `subjectSet` refusal while the whole application is chosen shows at the choice as `application-not-worked-on`; a stored schedule without a set of an agent that no longer works on the application asks for a set. | Explicit and accessible, the Monday example needs no set, and a bare "Required" would not explain a statement another administrator narrowed. | For Lars to review. |
| W3 | The subject link hides the type name when it equals the label, so the whole application app-wide reads once and a division reads "The whole application North". | No special case for `nh-system` in the component. | For Lars to review. |
| W3 | The mock API also marks typed events and schedules that no longer fit what an agent works on as `not-worked-on`, answers `nh-system/global` subject work and refuses its search; it has no division names. | The same rule as the server; the playground has no divisions to describe. | For Lars to review. |
| W5 | Placing applies to runs only (a run id, not a person's turn in the thread, not a consultation) and to tools with an effect; a read with a declared subject runs in the run's scope as before. | K11 concerns what runs propose; a person's own turn decides inline in its conversation and a consultation proposes for the consulted subject; a read proposes nothing. | For Lars to review. |
| W5 | A placed call runs in the object's agent invocation scope with `RequireApprovalForEffects`: the gate authorizes the tool against the object and its division, and every effect becomes a proposal, also one a policy would let run directly. | The proposal's authorization, execution scope and targets must be the object's; "proposals stay governed". The sample's status tool needs the project's division, which a run for the whole application has not. | For Lars to review. |
| W5 | The object is read before the call by binding the call's input with the tool's serializer (deserialized into the input type and serialized again) and confirmed against the input the shared invoker evaluated; arguments that do not bind or name no object stay in the run's own thread and fail on their own binding. | A Guid or a number reads as the tool sees it, so one object has one thread; the platform never trusts the model's spelling. | For Lars to review. |
| W5 | A placed proposal always waits deferred, whatever the policy's mode, and executes from its own copy of the call in the thread at the object (outside its message history); the run's own call ends `not-executed` with `assistant-proposal-placed` and a result that names the object. | A run never waits in another thread (`ResumeAgentTurnAsync` works in the approval's own conversation); execution replays the stored call of the approval's conversation; the run's thread shows what happened. | For Lars to review. |
| W5 | Rule (3) checks the run's `AccountableOwnerId`: the agent's own actor for an agent that answers to itself, otherwise the responsible person or the technical account; never the initiator. | The plan's "the agent's account (or the accountable person)"; an application's resolver decides whether it knows agent actors. | For Lars to review. |
| W5 | Reasons beyond the plan: a run bound to a tenant places only in that tenant (`not-found`, so another tenant's object stays unknown), a per-division agent outside its division reads `does-not-work-on`, an object where nobody answers for the agent (no responsible person for `SubjectResponsible`) reads `no-access`, and an evaluated object that differs from the one read reads `not-found`. The limit counts every proposal the run placed, in any status; 0 places none, at most 200. | Four reason codes stay readable for the model; tenants are an isolation boundary even for app-wide agents. | For Lars to review. |
| W5 | Migration `AgentPlacedProposals` for SQL Server and PostgreSQL adds `AssistantApproval.PlacedFromConversationId` (the run's thread); `RunId` stays the run that made it, so the proposal set, feedback, evaluation and the evidence (bound to the run's turn) keep working. | The inbox, the approver check, the summary and the notifications must tell a placed proposal apart without a join to runs, which chat and consultation proposals lack; the plan named no column, so this is an addition. | For Lars to review. |
| W5 | A placed proposal keeps the run's initiator for four-eyes, but the initiator neither decides it through the initiator approver rule nor sees it in the inbox, unless an approver of the object; the inbox's initiator clause and the subject summary leave placed proposals out. | K11: the object's approvers decide; the initiator may not read the object at all. | For Lars to review. |
| W5 | `PlacedBy` (`NhAssistantProposalOriginDto`) carries the run's thread, the run and its subject type; the subject's id and name only for the whole application, the kind's name (`SubjectTypeName`) otherwise. | A reader of the object may not read the other object; "for the whole application" is safe for everyone. | For Lars to review. |
| W5 | Approvers of the whole application (handover of W3): the inbox asks the caller's live role on the subjects of the whole application with pending proposals, at most 100 by id, with the resolver's 10-second cache, and lists their proposals for approvers; a caller bound to a tenant decides none (as `GET approvals/{id}` hides threads without the caller's tenant). `POST subjects/nh-system/summary` counts with the caller's live roles on at most 100 requested ids. | The whole application lists no members; evaluating at query time keeps W3's decision and stays bounded. | For Lars to review. |
| W5 | Notifications about proposals of the whole application reach the people of its thread (its conversation's participants, then the people who decided its proposals, at most 50), each with the role the resolver gives them now; the notification policy picks the approvers as for any subject. Live updates are unchanged. | Everyone who passes a policy cannot be listed; the people who opened or decided in the thread are the ones who work with it, and a live check keeps it governed. | For Lars to review. |
| W5 | A run's placed proposals notify the approvers of each object once per object (`RunEndedWithProposals` with the object's thread, without the initiator), when the run completes and also after it failed; the run's summary counts placed proposals per tool and status; cancelling a run withdraws its placed proposals as well. | The object's approvers are who must act; a failed run's placed proposals still wait; the proposals belong to the run's proposal set. | For Lars to review. |
| W5 | Audit: `ApprovalRequested` and the new `AgentProposalPlaced` are recorded about the object (its subject and thread, the run's thread as `ObjectId`), `AgentProposalNotPlaced` with the reason and the named object; rule handlers read `NhAssistantRuleProposal.PlacedAt` and `PlacementReason`. | Content-free and about the object people look at; rules need the reason to summarize their run. | For Lars to review. |
| W5 | Generator: the attribute is accepted on a property or on the parameter of a positional record (matched by name); its JSON name follows the serializer contract; `NHAI015` refuses an invalid or `nh-` type, `NHAI016` another property type, an ignored property or a declaration on the whole input, `NHAI017` a second declaration. A read-only tool may declare one; it is not placed. | Positional records are the common input shape; the JSON name must match what the model sends. | For Lars to review. |
| W5 | MCP: MCP tools never declare a subject, and placing also skips every `mcp.` tool id; their proposals stay in the run's thread under Q10. A placed proposal is always a governed tool and executes through the governed function, never through the MCP re-checks. Recorded in the MCP plan as I19. | K12; the brief asks to keep Q10 untouched. | For Lars to review. |
| W5 | Angular: the proposal cards say "Proposed by a run for the whole application" (in a division with its name) or "for another object" with its kind and offer "Open the run's conversation"; `nh-assistant-agent-proposals`, `nh-assistant-agent-inbox` and `nh-assistant-agent-thread-view` emit `openConversation`, `nh-assistant-subject-suggestions` opens the panel itself and emits `runConversationOpened`. The mock API serves `proposals` of the scenario in the inbox, by id, by lookup, in a thread and on a subject's work, and decides them with the server's validation; an approved one executes at once. | Hosts keep control of the panel as for the thread view; the suggestions already own the panel. | For Lars to review. |
| W5 | Sample: the follow-up's skill `complete-projects` works by fixed rules (`AddRuleHandler`, like `project-kickoff`) instead of a scripted deterministic model, and the agent's routing rules (run settings `RuleHandler`) send every run for the whole application, the tick and a run now, to it; a run on a project goes to the model. The schedule names the skill; `MaxRunsPerSubjectPerDay` of the follow-up rose from 2 to 10. A development database with the follow-up of W2 to W4 keeps it; remove it in the administration to seed this one. | Deterministic, model-free and shows the platform's placement with `ProposeAsync`; run now places as well; the tick, run now and tests must fit in a day. | For Lars to review. |
| W5 | Sample: `ProjectSubjectAccessResolver` gives people with the application-wide `app.project.view` permission the reader role on every project, so the development operations account (project lead role) reads the projects the follow-up proposes for. The sample test makes the test host's operations account a reader of its own projects only, so the shared host's other finished projects are refused (`no-access`) instead of polluted; ticks in the tests wait for a fresh minute, because a schedule ticks once per minute. | Rule (3) needs the account to read the project; the application's own pages already let app-wide viewers read every project. | For Lars to review. |
| W5 | A placed proposal's approval policy is the one the agent's configuration gives the tool for the run's trigger and skill, as for its own proposals; its approver rules, the subject version precondition and the guards are evaluated on the object, and the listing roles of the whole application are kept ten seconds per person across requests (`NhAssistantSystemRoleCache`, at most 1,024). | Policies belong to agents and tools, not to objects; "those of the object" means who decides and what is checked. The resolver of the whole application caches per request only, and the inbox reloads on every live update. | For Lars to review. |
| W5 | Sample SPM-281: the partner portal names its projects with the subject type `project` (it registers only its own resolver for it), because its agent reuses the sample's status tool, whose input now declares `[NhAiSubject("project")]`; with `partner-project` its calls named another object and no proposal was made. The release notes list this as a breaking change for applications that reuse a tool under another subject type. | The portal's narrative already speaks of the project's approver; a separate portal tool would need the generator in the test project. | For Lars to review. |
| W5 | `NhAssistantRunResults.PreviewOf` is unchanged; W6 reads placed proposals of a run through `RunId`. | The handover keeps the seam for W6. | For Lars to review. |

## Design

### W1: object types with names, entity binding, search and links

**Implementation notes.** Implemented in `NhAssistantSubjectTypeBuilder` (AI.Chat), `INhAiSubjectSearch`
and `NhAiSubjectSearchRequest` (AI.Common), `NhAssistantSubjectSearchService` and the subject
access service's type descriptors, the startup validator, `GET subjects/{type}/search`, `SubjectTypes`
in the run catalog and `SubjectTypeName`/`SubjectLink` on the thread, proposal, work and
conversation-thread DTOs. Angular: `searchSubjects`, `subjectLinks`, the internal
`nh-assistant-subject-link` and `nh-assistant-subject-picker`, names and links in the inbox, the
proposal cards and the thread view, and the mock's `subjectTypes`. Sample case SPM-301. No migration.

**Registration.** `AddSubjectType<TResolver>()` gains an optional configuration:

```csharp
assistant.AddSubjectType<ProjectSubjectAccessResolver>(type => type
    .Named(new() { ["en"] = "Project", ["nl"] = "Project" }, fallbackLanguage: "en")
    .NamedPlural(new() { ["en"] = "Projects", ["nl"] = "Projecten" })
    .ForEntity<Project>()
    .OpenAt("/management/projects/{id}"));
```

- Names: at most 100 characters, languages as culture names; the fallback language must have a
  name. Without `Named` the type id is shown.
- `ForEntity<T>()` stores `typeof(T).Name` and `typeof(T).FullName` as `EntityType` and
  `EntityTypeFull`.
- `OpenAt` takes a relative path or an absolute `https` URL with exactly one `{id}`; the id is
  URL-encoded.
- Startup fails for a type starting with `nh-` (K8), a duplicate type, or a configuration for a type
  without a resolver.

**Search (K3).** An optional interface in `NewHeap.Platform.AI.Common`:

```csharp
public interface INhAiSubjectSearch
{
    IAsyncEnumerable<NhAiSubjectInfo> SearchAsync(
        NhAiSubjectSearchRequest request, CancellationToken cancellationToken = default);
}

public sealed record NhAiSubjectSearchRequest(string SubjectType, string Query, string ActorId, int Limit = 20);
```

The resolver class implements it next to `INhAiSubjectAccessResolver`; the platform keeps only
results whose `ResolveAsync` for the actor is at least `Reader` and that are in a scope the caller
may use.

**API.**
- The run catalog gains `SubjectTypes`: per type its id, name and plural in the request's language,
  `EntityType`, `EntityTypeFull`, `Link`, `Searchable` and `IsSystem`.
- `GET subjects/{type}/search?query=&limit=` (access policy; query 2 to 100 characters; limit at
  most 50) returns `{ type, id, displayName, scopeKey, divisionId }` for people.
- Thread, inbox, run and proposal DTOs that carry a subject gain `subjectTypeName` and `subjectLink`.

**Angular.** Models and `NhAssistantAgentRunApiService.searchSubjects`; `provideNhAssistant({
subjectLinks })` may override links with a router function; the internal picker searches subjects.

### W2: what an agent works on

**Implementation notes.** Implemented in `NhAssistantAgentWorksOn` (statement, `Covers`), the builder
methods `WorksOn`, `WorksOnAllObjects`, `WorksOnApplication`, `PerDivision`, `RunPolicy` and
`ViewPolicy`, `AddAgentPolicy`, the typed `Subject(type, id)` overloads of triggers and trigger
events, the internal `NhAssistantWorksOnRule` (the one rule, for W3 and W5 to call) and
`NhAssistantAgentKnownSubjectTypes` (hints). `AssistantAgent` gained the statement with the
migration `AgentWorkOnObjects` for SQL Server and PostgreSQL; the run configuration snapshot carries
it to data agents' run options. Enforcement: every trigger fire, `POST threads/start`, `POST
threads/open`, hand-offs and consultations (`assistant-agent-does-not-work-on`), data trigger
validation (`not-worked-on`) and startup checks. Angular: the editor's works-on section, the cards'
works-on line, typed events in the trigger forms, models, texts and the mock API's validation. Sample
case SPM-302, for its W2 part.

**Code.**

```csharp
assistant.ConfigureAgent("project-follow-up", run => run
    .WorksOn("project")            // or .WorksOnAllObjects()
    .WorksOnApplication()          // the nh-system object
    .PerDivision()                 // optional, K7
    .RunPolicy("app.project.manage")
    .ViewPolicy("app.project.view"));
```

Not calling `WorksOn*` means all object types and not the application (K6).

**Data.** `AssistantAgent` gains `WorksOnSubjectTypes` (JSON array, at most 20 types),
`WorksOnAllSubjects`, `WorksOnApplication`, `PerDivision`, `RunPolicy` and `ViewPolicy`;
`NhAssistantAgentInput`, `NhAssistantAgentRequest` and the admin DTOs carry them. Run and view
policies of a data agent are chosen from the names the application lists with
`AddAgentPolicy(name, description)`, shown in the run catalog as `AgentPolicies`. Migration
`AgentWorkOnObjects` for SQL Server and PostgreSQL; existing rows get all object types, no
application, app-wide, no policies.

**Enforcement.**
- Data triggers: an event's subject type and a schedule's subject set must fit the agent when
  they fire (the type is known only then); a refused start is recorded with
  `assistant-agent-does-not-work-on`.
- Manual starts (W4), placed proposals (W5), hand-offs and consultations check the same rule.
- Triggers in code may declare their type up front with the new overload
  `Subject("project", e => e.ProjectId.ToString())`; startup then validates them against
  `WorksOn`.

**Screens.** The agent editor asks "What does this agent work on?" with the object types by name,
"All objects" and "The whole application", and, when the application lists divisions, "Per
division". The agent card shows it; an agent in code shows its code setting read only, and an
agent that does not say shows "All objects" with the hint of known types.

### W3: the whole application as a scope

**Implementation notes.** Implemented in `NhAssistantSystemSubjectResolver` (AI.Chat.AspNet, registered by
`AddNewHeapAssistant`, roles through `INhAiPrincipalFactory` and `UseSystemSubject` with `ReadPolicy` and
`ApprovePolicy`), `NhAiSystemSubjects` (AI.Common), the scope check in `NhAssistantAgentWorksOn.Covers(NhAiSubjectRef)`
and the rule, schedules without subjects in code (`NhAssistantScheduleBuilder`) and as data (no subject set), whose
ticks run in `NhAssistantScheduledTriggerJob` on `nh-system/global` or once per listed division, the startup checks
(platform resolver, per division without a directory, subjectless schedules, system policies), the localized name in
the DTOs and invocation contexts without a subject scope. `INhDivisionDirectory` and `NhDivisionInfo` in
`NewHeap.Platform.Common.Actors` with `AddNewHeapDivisionDirectory<T>()` and NewHeap Identity's
`NhIdentityDivisionDirectory<TDivision>`; `DivisionsListed` and the run catalog's `Divisions` follow it. Angular: the trigger form and the guided setup ask whether a schedule runs for the whole
application (`subjectSet: null`) or a set, the trigger list names it, the editor and cards leave `nh-system`
out of the kinds, the subject link names the whole application once, and the mock API lists `nh-system` and
validates schedules as the server does.
Sample: the follow-up's Monday schedule without a subject set and the read policy of the whole application (SPM-302).
No migration.

- The platform registers the resolver of `nh-system`. Its object ids are `global` for app-wide
  agents and `division-{id:N}` for per-division agents; `DescribeAsync` returns the matching scope
  key, the division and the name "The whole application" (or the division's name).
- Roles on `nh-system`: `Reader` for people who pass the assistant's access policy, `Participant`
  and `Approver` for people who pass the system policy, by default the administration policy
  (`UseSystemSubject(s => s.ApprovePolicy("…"))`). Endpoints add the agent's own view and run
  policies (K9, K14) on top.
- **Divisions (K7).** `NewHeap.Platform.Common.Actors` gains an optional `INhDivisionDirectory`
  (`ListAsync`, names included); NewHeap Identity registers one over its divisions, an application
  registers its own with `AddNewHeapDivisionDirectory<T>()`. Without it nothing is offered per
  division.
- **Schedules.** A schedule in code may omit `Subjects()`; a data schedule may omit its subject set.
  Such a schedule runs once per tick on `nh-system/global`, or once per listed division for a
  per-division agent (bounded by `MaxSubjectsPerTick`).
- The invocation scope of a system run has no object; a per-division run adds the division as its
  execution scope, as an active division does for a person today.

### W4: run now

**Implementation notes.** Implemented in `NhAssistantManualStarts` (the one rule of starts by hand, K9),
`NhAssistantSystemSubjectResolver.MayRunAsync` and its division search, `NhAssistantObjectAgents` with
`GET subjects/{type}/{id}/agents` (`NhAssistantObjectAgentsDto`, `NhAssistantObjectAgentDto`,
`NhAssistantLatestRunDto`), the run store's latest runs and open proposal counts per thread,
`NhAssistantRunResults` (the result seam for W6) and the MCP rule for runs by hand
(`assistant-mcp-initiator-only`). Angular: the internal run-now dialog, "Run now" on the agent cards,
`nh-assistant-object-agents` in `agent-runs`, `getObjectAgents` with its models and constants, the
subject picker's whole application and divisions, `box-sizing` in the shared form styles, texts, and the
mock API's runs by hand, threads, agents of a subject and divisions, after splitting the mock
administration per area. Tests: `AssistantRunNowTests` and the MCP test in `AssistantMcpHeadlessTests`
on SQL Server and PostgreSQL, the run-now and picker specs. Sample case SPM-303: the follow-up run now
for the whole application, the project editor's agents and the playground. No migration.

- `POST threads/start` accepts `nh-system` like any subject: `{ agentId, subjectType: "nh-system",
  subjectId: "global" | "division-…", message? }`. `MayStartAsync` checks `WorksOn`, and for
  `nh-system` the agent's run policy (K9); for other objects the participant role stays.
- `GET subjects/{type}/{id}/agents` lists the agents that work on that object and that the caller
  may use, with `canStart`, the latest run (status, time, the first 300 characters of its result)
  and the number of open proposals.
- **Screens (K10).**
  - Administration: "Run now" on an agent card opens a dialog: the whole application (and the
    division, for a per-division agent) or an object of one of its types through the object picker,
    plus an optional message; the receipt links to the run.
  - `nh-assistant-object-agents` (`subjectType`, `subjectId`) for an object's page: the agents,
    their latest result, open proposals, run now and the thread view on demand.
  - The board tile (W7) runs the agent for the whole application.

### W5: proposals placed at their object

**Implementation notes.** Implemented in `NhAiSubjectAttribute` and `NhAiToolSubject` (AI.Common) with
`NhAiToolDescriptor.Subject`, the generator's subject declaration, contract hash and diagnostics
`NHAI015` to `NHAI017` (AI.Generators), the internal `NhAssistantProposalPlacement` (the rules, the
binding of the object and the thread at the object), the tool-call interceptor's placing (the call in
the object's scope, the deferred proposal at the object with its own copy of the call, the run's call
as placed or not placed), `NhAssistantLimits.MaxPlacedProposalsPerRun`, `NhAssistantPlacementReasons`,
the error codes `assistant-proposal-placed` and `assistant-proposal-not-placed`, the audit kinds
`AgentProposalPlaced` and `AgentProposalNotPlaced`, `NhAssistantRuleProposal.PlacedAt` and
`PlacementReason`, `PlacedBy` (`NhAssistantProposalOriginDto`) on proposal DTOs, the inbox and approver
rules for placed proposals, notifications per object and the run summary, and the approvers of the whole
application (`NhAssistantSystemApprovers` for the inbox and the subject summary, the people of its thread
for notifications). Migration `AgentPlacedProposals` for SQL Server and PostgreSQL. Angular:
`AgentProposalOrigin`, the origin line and its conversation on the proposal cards, `openConversation` of
the inbox and the proposals list, `runConversationOpened` of the subject suggestions, texts, error codes
and the mock API's proposals, inbox and decisions. Tests: `AssistantPlacedProposalTests` on SQL Server and
PostgreSQL, the generator tests and `nh-assistant-placed-proposals.spec.ts`. Sample case SPM-302: the
status tool's input declares its project, the follow-up's `complete-projects` skill and routing rules,
the weekly tick and a run now that place proposals at finished projects whose approver approves one.

- **Declaration (K12).** `[NhAiSubject("project")]` on a property of a tool's input record (a
  `Guid`, `string` or integer). The source generator emits `NhAiToolDescriptor.Subject` (type and
  JSON argument name), which becomes part of the contract hash only when present; hand-built
  catalogs set it themselves. At most one per tool.
- **Placing (K11).** When a tool with a declared subject is proposed and the argument names another
  object than the run's own:
  1. the type must be one the agent works on, and the object must exist (`DescribeAsync`);
  2. an app-wide agent may place in any scope, a per-division agent only in its division;
  3. the agent's account (or the accountable person) must be at least `Reader` on the object;
  4. the run may place at most `MaxPlacedProposalsPerRun` (default 20);
  5. the proposal is stored in the agent's thread at that object (created when needed, shadow
     follows the run) with the run id of the run that made it; duplicates are refused as today.
- The proposal's approval policy, approvers, preconditions and subject version are those of the
  object; it executes in the object's scope with the agent's account.
- A refused placement does not fail the run: the tool call returns
  `assistant-proposal-not-placed` with a reason code the model can read
  (`does-not-work-on`, `not-found`, `no-access`, `limit`).
- Thread views and the inbox show "Proposed by a run for the whole application" (or for another
  object) with a link to that run.

### W6: results and history

- `AssistantRun` gains `Result` (Markdown, at most 20,000 characters) and `ResultTruncated`;
  `NhAssistantRunSummaries` keeps writing the summary for memory. Migration `AgentRunResults` for
  both providers. Results are model output: the screens render them with the existing safe Markdown
  renderer and never as HTML. Retention follows the thread's.
- `GET runs?agentId=&subjectType=&subjectId=&status=&from=&to=&page=` for people returns the runs
  they may see (K14): runs on objects they may read, and system runs of agents whose view policy
  they pass. `GET runs/{id}` returns one run with its result, proposals and artifacts.
- `GET admin/agents/{id}/runs` with the same filters returns every run of the agent for
  administrators.
- **Screens.**
  - Administration: a "Runs" tab per agent with filters (object type, object, the whole application,
    status, period) and per run its trigger, object, result, proposals, usage and duration. This is
    option C of the walk-through.
  - `nh-assistant-run-result` shows one run's result, proposals and artifacts; the thread view's run
    timeline opens it.

### W7: groups and boards

**Code.**

```csharp
assistant.AddAgentGroup("project-management", group => group
    .Named(new() { ["en"] = "Project management", ["nl"] = "Projectbeheer" }, fallbackLanguage: "en")
    .Describe(new() { ["en"] = "Agents that keep the projects moving.", ["nl"] = "…" }));

assistant.ConfigureAgent("project-follow-up", run => run.InGroup("project-management"));
```

- Administrators add any agent to a group or remove it in the agent editor; their choice is stored
  in `AssistantAgentGroupMember` and wins over code. Groups themselves exist only in code.
- `GET boards` lists the groups with at least one agent the caller may see; `GET boards/{group}`
  returns per agent its name, what it works on, `canStart` for the whole application, its latest
  system result, open proposals and whether there is something new since the caller's last visit;
  `POST boards/{group}/visit` records the visit in `AssistantBoardVisit` (per person, group and
  scope). Migration `AgentBoards` for both providers.
- `nh-assistant-agent-board` (`group`) shows tiles with the latest result, "New", run now, open
  proposals (linking to the inbox) and the history (W6), and records the visit when it is shown.

### U6: the flow view

On top of W1 to W7 the run configuration shows an agent as one flow: **works on** (object types,
the whole application, per division) → **when** (event and schedule triggers, run now) → **skills**
(way of working, tools) → **approval** (policy sentence, approvers) → **results** (board groups,
latest runs). Each step opens the form it summarizes; the flow is an overview, not an editor.

## Security and limits

- Object search and every listing check `ResolveAsync` per object; a search never returns objects
  outside the caller's scopes.
- `nh-system` runs never get a person's rights implicitly: they act with the agent's account like
  every trigger run, and only people who pass the run policy start them.
- Placed proposals are bounded per run, checked against the object and the agent's account, refused
  across divisions for per-division agents, and keep the existing duplicate and precondition checks.
- Results, names and descriptions are bounded; results are untrusted text.
- Board visits store only a person, a group, a scope and a time.

## Compatibility

| Change | Effect | Required action |
| --- | --- | --- |
| `nh-` subject types are reserved. | Startup fails for an application type starting with `nh-`. | Rename such a type. |
| `NhAiToolDescriptor.Subject`. | Contract hashes change only for tools that declare a subject; their pending approvals become invalid. | None, or re-propose. |
| Migrations `AgentWorkOnObjects`, `AgentPlacedProposals`, `AgentRunResults`, `AgentBoards`. | Schema changes. | Apply them for SQL Server or PostgreSQL. |
| New interface members (`INhAssistantAgentConfiguration` and admin DTOs gain the new fields). | Source compatibility of own implementations and test doubles. | Implement the members; recompile. |
| Existing agents. | Work on all object types, not on the application, app-wide. | Optionally declare `WorksOn`. |

## Sample

| Case | Shows |
| --- | --- |
| SPM-301 | The project object type with English and Dutch names, `ForEntity<Project>()`, search and a link; the run catalog and the object picker. |
| SPM-302 | The "Project follow-up" agent created as data: works on projects and the whole application, a schedule every Monday without a subject set, a skill that counts open projects and proposes completions; its proposals land at each project and in the approvers' inbox. |
| SPM-303 | Run now in the administration, on the board and with `nh-assistant-object-agents` in the project editor. |
| SPM-304 | The runs view in the administration and a "Project management" board page in the portal with the latest result, "New" and the history. |

The playground gets the same through the mock API. With the optional real model of the sample
(walk-through item 3B) the follow-up agent can be tried with free instructions; the deterministic
model scripts the same flow for the tests.

## Delivery order

| Phase | Verification |
| --- | --- |
| W1 | Registration validation; search filtered by access on SQL Server and PostgreSQL; catalog names per language with fallback; UI spec of the picker searching objects. |
| W2 | Migration on both providers; enforcement at triggers, starts, hand-offs and consultations; editor spec. |
| W3 | System thread per scope; schedules without subjects app-wide and per division; division directory with and without NewHeap Identity. |
| W4 | Start authorization for objects and `nh-system`; the object agents endpoint; dialog and component specs. |
| W5 | Generator output and contract hash; placement rules (type, scope, access, limit, duplicates); execution in the object's scope; inbox and thread specs. |
| W6 | Result storage and bounds; people's and administrators' run listings filtered by access; runs view spec. |
| W7 | Groups, administrator membership over code, visits and "New"; board spec at desktop and 375px. |
| U6 | Flow view spec with entry points to every form. |

Each phase keeps the mock API, the sample cases, guidance rules, the consumer guide and the release
notes in sync and runs the validators, as `AGENTS.md` requires.

## Out of scope

A free node editor for agents (the flow view stays an overview with entry points to the forms),
dashboards composed in the administration (K15), notifications about results (K16), results shared
outside the application, and analytics over results.
