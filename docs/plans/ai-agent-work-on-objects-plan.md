# Agents Working on Application Objects Plan

This document plans how agents of `NewHeap.Platform.AI.Chat` and `@newheap/platform-ai-chat` work on
the objects of an application (quotes, assignments, orders, projects) and on the application as a
whole, how people start them by hand, where their proposals and results appear, and how an
application groups them on its own pages. It was written on 2026-10-19 during the review of the
[run configuration UX](ai-agent-run-configuration-ux-plan.md) and checked against
`feature/run-configuration-ux`.

## Status

Accepted on 2026-10-19: Lars took decisions K1 to K16 below. Phases W1 to W7 are designed and not
yet implemented; the flow view (U6 of the run configuration UX plan) follows on top of them. The
[MCP connections plan](ai-assistant-mcp-connections-plan.md) uses SPM-299 and SPM-300; this plan
uses SPM-301 onwards.

| Phase | Status |
| --- | --- |
| W1 | Designed: object types with names, entity binding, search and links. |
| W2 | Designed: what an agent works on. |
| W3 | Designed: the whole application as a scope. |
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

## Design

### W1: object types with names, entity binding, search and links

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
