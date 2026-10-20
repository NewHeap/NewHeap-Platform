# Agents Working on Application Objects Plan

This document plans how agents of `NewHeap.Platform.AI.Chat` and `@newheap/platform-ai-chat` work on
the objects of an application (quotes, assignments, orders, projects) and on the application as a
whole, how people start them by hand, where their proposals and results appear, and how an
application groups them on its own pages. It was written on 2026-10-19 during the review of the
[run configuration UX](ai-agent-run-configuration-ux-plan.md) and checked against
`feature/run-configuration-ux`.

## Status

Accepted on 2026-10-19: Lars took decisions K1 to K16 below. W1 to W3 are implemented and not
released; W4 to W7 are designed and not yet implemented; the flow view (U6 of the run configuration
UX plan) follows on top of them. The
[MCP connections plan](ai-assistant-mcp-connections-plan.md) uses SPM-299 and SPM-300; this plan
uses SPM-301 onwards.

| Phase | Status |
| --- | --- |
| W1 | Implemented, not released: object types with names, entity binding, search and links. |
| W2 | Implemented, not released: what an agent works on. |
| W3 | Implemented, not released: the whole application as a scope. |
| W4 | Designed: run now. |
| W5 | Designed: proposals placed at their object. |
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
| W3 | The sample reads the whole application with `app.project.view` and keeps the administration policy for approvers; the follow-up's Monday schedule (`project-follow-up-weekly`, 07:00 Europe/Amsterdam) is seeded live without a skill, app-wide. Per division is not shown in the sample beyond the directory and `divisions: true`. | The sample's access policy needs an active division; the counting skill and placed proposals belong to W5; per-division adds no value for the follow-up. | For Lars to review. |
| W3 | The trigger form and the guided setup ask with a radio choice what a schedule runs for ("The whole application" or "Every object in a set"), defaulting to the whole application for an agent that works on it; a server `subjectSet` refusal while the whole application is chosen shows at the choice as `application-not-worked-on`; a stored schedule without a set of an agent that no longer works on the application asks for a set. | Explicit and accessible, the Monday example needs no set, and a bare "Required" would not explain a statement another administrator narrowed. | For Lars to review. |
| W3 | The subject link hides the type name when it equals the label, so the whole application app-wide reads once and a division reads "The whole application North". | No special case for `nh-system` in the component. | For Lars to review. |
| W3 | The mock API also marks typed events and schedules that no longer fit what an agent works on as `not-worked-on`, answers `nh-system/global` subject work and refuses its search; it has no division names. | The same rule as the server; the playground has no divisions to describe. | For Lars to review. |

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
| Migrations `AgentWorkOnObjects`, `AgentRunResults`, `AgentBoards`. | Schema changes. | Apply them for SQL Server or PostgreSQL. |
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
