# AI Tool Composition Plan

Each tool call costs a round trip to the model. When one step depends on another (find a project, then its risks,
then their owners), the model reads every intermediate result and chooses the next call. That makes a turn slow, and
the context fills with data the answer does not need. Models also skip items in long "for each" chains and count
from long lists by reading them. This is not specific to MCP, but MCP clients feel it most, because they call
fine-grained tools of applications they do not know.

This plan lets a known chain run as one governed tool, and lets the model write a small script that runs a chain on
the server. Only the result returns to the model. It is a plan, not usage documentation: the guidance rules, the
release notes and the sample cases are authoritative once it is built.

## Status

Drafted on 2026-10-28 from a discussion with Lars. Lars gave three directions:
- No measurement before building (TC1).
- A mutation inside a composition follows the consuming application's own settings, exactly as the same call would
  on its own (TC5).
- The application decides at run time, from its own database, whether its assistant uses compositions and scripts,
  so a person can switch them on and off in their environment. This holds whether or not the application installs the
  scripting package (TC8).

On the same day Lars took the advice (a) on TC2 to TC8, Jint and Acornima as new dependencies of the scripting
package included. The learning loop (TC9) is recorded as an idea, not for building yet.

## What exists

- Tools are governed: `NhAiToolEffect` (`ReadOnly`, `IdempotentMutation`, `Mutation`, `ExternalSideEffect`,
  `Destructive`), `NhAiApprovalRequirement` (`PolicyControlled`, `Required`, `NotRequired`, `ConsumerAuthoritative`,
  `Issuer`), approval policies with `NhAiApprovalMode.Inline` or `Deferred`, proposal guards, budgets, presenters and
  an audit record per call. The model can already call independent tools in parallel.
- The runs and the activity record every tool call of a turn in order, and the budget ledger records what a turn cost.
- `WithNewHeapPlatformAITools` exposes governed tools over MCP, and lane MS of train 26 adds sign-in for external
  MCP clients (FB42 of `platform-feedback-23-plan.md`).
- Agents, skills and hand-offs can be data and can be tried in shadow before an administrator promotes them.

## Decisions

Decided by Lars on 2026-10-28. TC1, TC5 and TC8 are his directions; the others are the advice (a).

| # | Decision |
|---|---|
| TC1 | **No measurement before building.** The composition and script tools show in the activity what they ran and how many model rounds that saved, so their effect is visible once they are used. |
| TC2 | **A composition builder for governed tools**: steps through the governed invoker, values from earlier results, a declared result, the strongest step's effect, audit per step, stop at the first failure. |
| TC3 | **The script tool on Jint**, in the opt-in package `NewHeap.Platform.AI.Scripting`, with Jint and Acornima as new dependencies of that package only. |
| TC4 | **Typed signatures generated from the tools' JSON schemas**, and the exact signature with the field error when a call fails. |
| TC5 | **A call inside a composition gets exactly the decision the same call gets on its own**: the consuming application's effect, approval requirement, approval policy, guards and budget apply per call. A composition is never a way around them and adds no stricter rule of its own, apart from the limits in TC6. A `Deferred` call returns the proposal result and the script continues; an `Inline` call stops the script and the turn waits as today. |
| TC6 | **Hard limits per script**: statements, time, memory, recursion, tool calls, `MaxMutationsPerScript`, and result size. A fresh engine per script, `Destructive` only when the tool opts in, the script's hash on the guard and invocation context, and the script text stored with the run. |
| TC7 | **Compositions and the script tool are exposable over MCP**, opt-in per exposure, under the same governance and limits. |
| TC8 | **The application switches compositions and scripts at run time**, from its own data and per environment, agent and channel, through a seam in the assistant's core package. NewHeap supplies the default, the enforcement and the visible effective state. |

## Rules for all of it

- Every change is opt-in or compatible. A breaking change gets a `Breaking change | Required action` row in
  `docs/release-notes/v-next.md`.
- Every change follows the repository rules: SQL Server and PostgreSQL where persistence changes, tests with
  authorization negatives, a release-notes section per release unit, guidance and consumer-guide updates, an
  executable sample case, and English and Dutch texts for Angular parts.
- Every call in a composition goes through the governed invoker with the person's or agent's own rights. A
  composition can never see or do more than its caller.
- Nothing is specific to one model or one MCP client, and nothing names a consuming application.
- A new package dependency is Lars's explicit choice (TC3).

## Choices

"(advice)" marks the recommendation.

### TC1. Measure first

Decided by Lars: no measurement before building.

- **(a) Show it in the activity (advice).** Each composition or script call records:
  - the tool calls it ran, in order;
  - the model rounds that saved (calls minus one);
  - the size of the result that went back to the model.

  The agent's activity can then show per agent how often scripts and compositions are used and what they saved.
  It is no separate report, and it is the data the learning loop (TC9) would start from.
- **(b) Nothing extra**: the tool calls are recorded anyway, but nobody can see the gain.

### TC2. Known chains as one tool

- **(a) A composition builder for governed tools (advice).** An application declares a known chain as one tool:
  its steps call registered tools through the governed invoker, later steps take values from earlier results, and
  the composition declares what returns to the model. Its effect is the strongest effect of its steps, so a
  composition that only reads runs at once and appears as read-only over MCP. Each step is audited under the
  composition, and a failing step stops the chain and returns what succeeded and the failing step's code.
- **(b) Leave it to applications**: a hand-written `[NhAiTool]` that calls services directly already works, but it
  loses per-step governance, audit and budget.

### TC3. Chains the model writes: the script tool

- **(a) Jint, in an opt-in package `NewHeap.Platform.AI.Scripting` (advice).** One tool, for example
  `run_script`, takes a short JavaScript program in which each available tool is an async function. The program runs
  on the server, and only its return value goes back to the model. Each agent and each MCP exposure opts in.
  The tool is offered wherever an agent works:
  - a person's chat;
  - an agent run that a trigger or a schedule starts;
  - a consultation or a hand-off;
  - over MCP.

  The script's `tools` are exactly the tools that agent is offered in that turn. A run's or consultation's own tool
  ceiling therefore bounds its scripts too. Compositions (TC2) are tools like any other, and are offered wherever
  their agent is.
  Jint 4.17.0 (BSD-2-Clause, actively maintained, pure .NET, built-in limits) and its parser Acornima (BSD-3-Clause)
  would be new dependencies, kept out of every other package.
- **(b) A JSON step plan**: calls with references to earlier results, no code. It is the safest option, but
  without loops or conditions it soon falls short.
- **(c) Lua (MoonSharp or NLua)**: built for embedding, but MoonSharp has hardly been maintained for years, NLua
  needs a native library, and models write Lua less well than JavaScript.
- **(d) C# scripting with Roslyn**: rejected. Scripts run in the process with full trust, .NET has no sandbox
  for them, and a tight loop cannot be stopped.

### TC4. What the model sees of the tools

- **(a) Typed signatures generated from the tools' JSON schemas (advice).** The script tool's description lists
  the available tools as TypeScript-style declarations. A failing call returns the exact signature and the field
  error, so the model can correct the script in one step.
- **(b) The tool list as JSON only**: no extra generation, but more mistakes in field names.

### TC5. Mutations in a composition

The decision above holds. What remains open is what happens at a call that waits for a person.

- **(a) Follow the approval mode (advice).**
  - **`Deferred`:** the call returns the same "proposal recorded" result the model gets today, and the script
    continues. Later steps cannot depend on that call's outcome.
  - **`Inline`:** the script stops at that call and returns its result so far, plus the pending approval. The turn
    then waits as it does today, and after the decision the model can continue, with a new script if needed.
- **(b) Pause the script until the decision**: the engine would stay in memory for minutes or hours, and would be
  lost on a restart.

There is no transaction across calls: the result says, in order, what was done, what was proposed and where the
script stopped. Shadow runs never execute a mutation, inside a script or not.

### TC6. Limits

- **(a) Hard limits per script (advice).** A cap on each of these:
  - statements, time, memory and recursion (Jint's own options);
  - the number of tool calls;
  - the number of mutation calls (`MaxMutationsPerScript`, where 0 makes scripts read-only);
  - the size of the result.

  Each script gets a fresh engine, with no modules, no `require` and no access to .NET. `Destructive` calls run in a
  script only when the tool opts in. The proposal guard context and the invocation context say that a call came from
  a script, with the script's hash, so an application's guard can treat it differently. The script text is stored
  with the run and shown in the activity.
- **(b) Budgets only**: simpler, but then a loop of allowed mutations is bounded only by money.

### TC7. Over MCP

- **(a) Compositions and the script tool are exposable like any governed tool (advice)**, opt-in per exposure,
  with the same governance and limits. An external client then gets the same gain from a single call.
- **(b) In-app only**: smaller, but the clients that feel the round trips most would not benefit.

### TC8. A switch the application owns

An application wants to offer the feature as a setting of its own. One example is a checkbox in an environment's
settings, stored in the application's own database. The application must be able to check that setting at run time,
without a restart or a configuration change. This holds independently of the scripting package: the setting and its
check must compile without Jint.

- **(a) A seam in the assistant's core package, consulted per turn (advice).**
  - **The seam:** an interface in `NewHeap.Platform.AI.Chat`, for example `INhAssistantCompositionSwitch`, that
    answers per turn, per run and per MCP request whether compositions and scripts are available. Its context names
    the feature (compositions or scripts), the tenant, the agent, the channel (chat, run or MCP) and the person.
  - **The default:** reads `NewHeap:AI:Assistant:Compositions` and `...:Scripts` from configuration, so a host
    without the seam keeps working.
  - **The application's implementation:** reads its own table, with its own cache.
  - **When it is off:**
    - the model is not offered the script tool or the composition tools for that turn;
    - a call that still arrives, for example over MCP, is refused with `assistant-scripting-off` or
      `assistant-composition-off`;
    - the individual tools stay available as before;
    - a value that changes during a turn takes effect from the next turn, so a running script never flips halfway.
  - **The effective state:** NewHeap shows it, with its source ("off by the application", "scripting package not
    installed", "agent does not allow it"):
    - on the administration's agent page;
    - in an `INhAssistantCompositionStatus` read;
    - through an Angular method, so the application's own settings screen can show what the assistant actually does
      next to its checkbox.
  - **The sample:** an application-owned toggle, stored in its own table on its settings page, per environment and
    per agent, with English and Dutch texts and a sample case.
- **(b) NewHeap stores the switch**: a table and a toggle in the administration. That is less work for an application,
  but the setting cannot live in the application's own settings and database.
- **(c) Configuration only**: changing it needs a configuration change or a restart, so a person cannot switch it in
  their environment.

The code ceiling still applies on top of the switch. An agent in code must allow scripts or compositions, and an MCP
exposure must opt in, before the application's switch can turn them on.

Agents created in the administration have no code of their own. On 2026-10-28 Lars decided that they get compositions
and scripts within a ceiling in code, just as hand-offs, shadow variants and consultations as data do. For such an
agent, the turn offers a marked tool only when all three of these allow it:
1. The host allows it in code for agents as data.
2. An administrator switches it on for that agent.
3. The application's switch is on at run time.

### TC9. Learning which chains to keep (idea)

Not for building yet. It follows the shadow-and-promote pattern of agents and skills:

1. Observe the scripts and tool sequences that the runs already record.
2. Find patterns: scripts that recur with other values, with their literals replaced by parameters.
3. Propose a recipe as a candidate in shadow, with a name, parameters and a description.
4. In shadow, compare its answers, time and tokens with the ad-hoc path.
5. An administrator promotes it. As data it runs in the script sandbox, so promoting it needs no deploy.
6. Recipes that are no longer used expire.

Two lighter forms could come earlier, with TC4: successful scripts as examples in an agent's instructions, and the
precise errors that let a script correct itself.

## Trains

Proposed, after the trains of `platform-feedback-23-plan.md`:

| Train | Lane | Choices | Migrations | Case ids |
|---|---|---|---|---|
| A | SW: switch and status | TC8 | in the sample only, for its own setting | SPM-371 |
| A | CO: compositions and their runtime | TC1 (activity), TC2, TC5, TC6 (context markers), TC7 (compositions) | none | SPM-372 |
| A | SC: script tool | TC3, TC4, TC5, TC6, TC7 (script) | none expected: the script is the tool call's argument | SPM-373–374 |
| later | LL: learning | TC9 | to be decided | SPM-375 |

Lars started train A on 2026-10-28, right after train 26 was pushed. The three lanes start together and divide the
shared parts as follows:

- **SW owns the switch.** That is the seam, the marker by which a tool says it is a composition or the script
  tool, and the enforcement: offering a tool or not per turn, and refusing a call that still arrives. SW commits
  that contract first and reports it.
- **CO owns the runtime that runs a step.** It runs the step through the governed invoker with the caller's
  context, aggregates effects, records each step for the activity, and puts the composition or script marker and the
  hash on the invocation and guard context. CO makes the runtime callable from the scripting package, commits that
  contract first and reports it.
- **SC builds against its own small adapter** for calling a tool. Once the coordinator passes on SW's and CO's
  contract commits, SC merges them into its branch and wires the adapter to them.

The lanes are merged in the order SW, CO, SC.

## Lane choices

Recorded by each lane under its own heading while it builds; ids are the lane's code and a number (SW1, SW2, ...).

### Lane SW

| # | Choice |
|---|---|
| SW1 | Built by lane SW (2026-10-28): **the marker is `NhAiToolDescriptor.CompositionFeature` (`NhAiCompositionFeature`: `None`, `Compositions`, `Scripts`) in `NewHeap.Platform.AI.Common`, and the same enum names the feature in the switch's context**, because the turn runner (AI.Chat) and the MCP export (AI.AspNet.Common, which does not reference AI.Chat) both read it, and one enum needs no mapping. Only tools of a registered catalog follow it, not extra functions of a turn; for Lars to review. |
| SW2 | Built by lane SW (2026-10-28): **the codes `assistant-composition-off` and `assistant-scripting-off` live in AI.Common (`NhAiCompositionFeatures`) and are aliased by `NhAssistantErrorCodes`**, because an MCP export without the assistant refuses with the same codes; the plan's `assistant-` names are kept; for Lars to review. |
| SW3 | Built by lane SW (2026-10-28): **five channels, `Chat`, `Run`, `Consultation`, `HandOff` and `Mcp`**, because an application may treat a hand-off apart from a consultation. A message a person types in an agent's thread is `Chat`, a person's start of a run is `Run`. The person is the person who chats, a run's initiator (none for a trigger), the person who asked a consultation, nobody for a hand-off and the MCP caller; the agent is `null` for MCP; for Lars to review. |
| SW4 | Built by lane SW (2026-10-28): **a turn decides each feature once, when it first selects its tools, and keeps it through skill switches and its closing answer; the switch is not asked where code does not allow the feature or no tool of it is registered; a switch that throws counts as off**, because a running script must never flip and a failing setting must not open a feature. An approved inline or deferred execution of a marked tool is a new turn and asks again, so it is refused once the switch is off; for Lars to review. |
| SW5 | Built by lane SW (2026-10-28): **the ceiling of an agent in code is `ConfigureAgentContext(...).AllowCompositions()` and `.AllowScripts()`; compositions are selected by the agent's tool selectors like any tool, the script tool needs no selector, also not in a consultation's own selection**, because `ConfigureAgentContext` already covers chats, runs and consultations, the ceiling is the script tool's opt-in and a script only calls the turn's tools. Effect and autonomy rules still apply, so the scripting lane chooses the script tool's effect knowingly; for Lars to review. |
| SW6 | Built by lane SW (2026-10-28): **the MCP exposure opts in with `NhAiMcpExposureBuilder.AllowCompositions()` and `AllowScripts()`; its gate is internal to AI.AspNet.Common, reads `NewHeap:AI:Assistant:Compositions` and `:Scripts` without the assistant, and asks `INhAssistantCompositionSwitch` for the `Mcp` channel with it**, because the seam belongs in AI.Chat and an MCP-only host should not get a second public seam. A feature the exposure does not allow is not part of it (`ai-tool-not-authorized`); an MCP-only host has no run-time switch of its own; for Lars to review. |
| SW7 | Built by lane SW (2026-10-28): **`INhAssistantCompositionStatus` and `GET admin/agents/{id}/compositions` report per feature and per agent channel (chat, run, consultation, hand-off, not MCP, which no agent serves) with the reasons `on`, `not-installed`, `agent-not-allowed`, `not-allowed-for-data-agents`, `off-for-agent` and `off-by-application`, in that order of precedence, asking the switch for the reading administrator and tenant**. Installed means a registered agent tool with the marker; the agent's selectors are not read, and the in-process read checks no rights while the endpoint needs the administration policy; for Lars to review. |
| SW8 | Lars chose (b) on 2026-10-28 for agents created in the administration. Built by lane SW (2026-10-28): **`UseCompositionsAsData(c => c.AllowCompositions().AllowScripts())` is the host's ceiling, and each such agent has its own `compositions` and `scripts`, stored in two new columns `UsesCompositions` and `UsesScripts` of `AssistantAgent` (library migration `AgentCompositions` for SQL Server and PostgreSQL)**, because no existing field of the definition fits: agent metadata is the application's own and never read by NewHeap. The setting is versioned and audited with the definition, switching on outside the ceiling answers `compositions: not-allowed` or `scripts: not-allowed`, a setting the agent already has stays accepted, and agents in code ignore it; for Lars to review. |
| SW9 | Built by lane SW (2026-10-28): **agent packages carry `compositions` and `scripts` only when on, both lockable; left out is off; an import meets the importing host's ceiling; a promoted shadow variant gives its setting to a live agent created in the administration, while a live agent in code keeps its code**, because writing only what is on keeps the hash of every linked agent without the setting, so no agent reads as changed locally after the upgrade; for Lars to review. |
| SW10 | Built by lane SW (2026-10-28): **the administration's agent page shows the effective state on its overview, and the agent editor offers the two settings in the tools of an agent created in the administration while the run catalog reports `compositionCeiling` (or the agent already uses one), sending them only when they change**, so an unrelated save never meets the ceiling; for Lars to review. |
| SW11 | Built by lane SW (2026-10-28): **in the sample, an environment is the assistant's tenant, empty for the sample, which serves its divisions without tenants; its table `AssistantCompositionSetting` is keyed by environment and agent, the MCP exposure's setting is stored under `mcp`, and a 30-second cache is cleared on save on that host**. The sample's DAL is PostgreSQL only, so its migration is PostgreSQL only while the library migration covers both providers. The sample registers no composition or script tool, which would fake the other lanes, so its effective state reads `not-installed` until they land; for Lars to review. |

### Lane CO

| # | Choice |
|---|---|
| CO1 | Built by lane CO (2026-10-28): **the step runtime is a session the chain tool opens from inside its own governed function** (`INhAssistantToolSteps.BeginAsync` in AI.Chat, `RunAsync` per step, `CompleteAsync` once); the call of the composition or script itself is authenticated through the invocation gate and audited by the runtime, but is no governed invocation of its own, so it has no effect decision, approval or budget reservation, because running it through the invoker would ask approval for a whole mutating chain and count the budget twice, stricter than the same calls on their own (TC5); for Lars to review. |
| CO2 | Built by lane CO (2026-10-28): **each step is a tool invocation row of the turn without a message part, found by its call id** (`nh-composition:{parent}:{index}`, `nh-script:{parent}:{index}`), and the activity reads those rows, because the brief prefers existing records and columns and this lane adds no migration; the chat shows one card per composition call and the step rows serve approvals, the execution of approved steps and the activity; for Lars to review. |
| CO3 | Built by lane CO (2026-10-28): **a chain call counts as the tool calls it ran, and at least one**: its first step uses the call's own count of the turn's tool calls and of the message, every further step counts as a call, and only the steps reserve budget, because TC5 counts the budget per step and a composition must not be cheaper or dearer than the same calls; a call refused before any step still counts once, so a loop of refused calls stays bounded; for Lars to review. |
| CO4 | Built by lane CO (2026-10-28): **at an inline approval the chain call ends as succeeded and the turn waits on the step's own proposal**; after the decision the model reads its own call with the result that says where the chain stopped, and then the decided step with its result, because the decision belongs to the step and the model must see what ran before it; for Lars to review. |
| CO5 | Built by lane CO (2026-10-28): **a step that became a proposal (deferred, shadow, placed, duplicate) or was not needed lets the chain continue; a failed or refused step and an inline approval stop it**, and a later step that reads the result of a step without data stops the chain with `assistant-composition-step-result-unavailable`, because later steps cannot depend on a proposal's outcome (TC5) and a composition must not guess; for Lars to review. |
| CO6 | Built by lane CO (2026-10-28): **what a step may call**: in a turn exactly the tools the agent is offered there (also MCP tools of the turn, for scripts), outside a turn the tools the caller may discover through the same exposure (agent, MCP or local); never a composition, the script tool, a consultation or request tool, or the caller itself. A composition's steps are moreover exactly one tool of a catalog that does not change at runtime, not destructive and exposed everywhere the composition is, checked when the host starts, because a composition's descriptor is built from them once and a destructive tool needs its own verifier and call; for Lars to review. |
| CO7 | Built by lane CO (2026-10-28): **a turn offers a composition only with every tool its steps call**, filtered after the agent's selectors and SW's switch, because a composition without its steps could only fail at that step; for Lars to review. |
| CO8 | Built by lane CO (2026-10-28): **a composition declares no policies of its own and allows agent grants**, so every right is checked per step with the caller's rights; its descriptor declares `Approval = Required` for a `Mutation` or `ExternalSideEffect` and `Idempotency = Required` for every non-read effect only to pass the host's checks of side-effect descriptors, its classification is the highest of its steps, and its steps keep their own approval and idempotency, because a composition grants nothing and must never be a way around a step's rule; for Lars to review. |
| CO9 | Built by lane CO (2026-10-28): **a step's files are not opened and a step reports no live tool events**; only what `Returns` declares reaches the model, because the composition decides what the model reads and the conversation shows its call, not its steps; for Lars to review. |
| CO10 | Built by lane CO (2026-10-28): **outside a turn, such as over MCP, a step that needs approval fails with `ai-tool-approval-required`** and stops a composition, because the same call fails that way there and there is no proposal outside a turn; for Lars to review. |
| CO11 | Built by lane CO (2026-10-28): **the activity is a Compositions view in the agent's Activity** (`GET admin/agents/{id}/compositions/activity`, next to SW's effective state at `.../compositions`), also for an agent that works in the chat only, over the newest 500 calls of a period of at most 366 days (default 30), with per call the steps in order, rounds saved (tool calls minus one) and result characters, and per tool the totals over those calls with `total` and `truncated`; calls inside a consultation store nothing and MCP calls are audited only, because the activity reads stored tool invocations and no new record is added; for Lars to review. |
| CO12 | Built by lane CO (2026-10-28): **the marker is `NhAiToolCallOrigin`** (kind, composition's or script tool's id, the script's hash or the composition's contract hash, the parent invocation and the step index) on the invocation context, the audit record and the proposal guard context; the invoker takes it once, so a call the step's tool makes internally carries none, because a guard must see exactly the step that proposes; for Lars to review. |
| CO13 | Built by lane CO (2026-10-28): **default limits of a session are 16 steps (at most 64) and 4 mutation steps, passed in by the caller**; a composition passes its own number of steps for both, because its chain is declared in code and the script tool sets its own limits (TC6); for Lars to review. |
| CO14 | Built by lane CO (2026-10-28): **the sample's overview reads the project, its open tasks and its members** through three new read tools of the `projects` tool set, because the sample has no risks; the overview's test switches compositions on in the portal's own setting (SPM-371), as the running sample does; for Lars to review. |

### Lane SC

| # | Choice |
|---|---|
| SC1 | Built by lane SC (2026-10-28): **the engine drives Jint's event loop itself, one call at a time**: a tool call queues its promise, the runner runs the queued calls through the host in the order the program made them (also inside `Promise.all`) and resumes the program in a synchronous segment per call; statements and allocated memory are counted over the whole script by constraints of the package, with Jint's own memory limit per segment and an operation deadline for the wall clock, because Jint resets its limits at every entry and counts memory per thread, so a limit would not hold after the first call otherwise; for Lars to review. |
| SC2 | Built by lane SC (2026-10-28): **a call inside a script returns the tool's shared result (`{ success, data }` or `{ success: false, code, message }`), never throws for a tool's failure**, so a deferred proposal is an ordinary result and the script goes on; what the program cannot fix itself (an unknown or refused tool, a call that does not match its signature, a limit, an inline approval, a refusal of the turn) stops the script with a host error the program cannot catch, because that keeps every stop precise and the result honest about what ran; for Lars to review. |
| SC3 | Built by lane SC (2026-10-28): **the script tool's own descriptor is read-only** (`scripts.run`, `run_script`), so it is offered to every agent that allows scripts and its tools follow the turn's own effect and autonomy filters; its MCP hints say its calls may change data unless the MCP limits are read-only, it requires an authenticated caller without a policy of its own, its result is classified confidential, and it allows agent grants so a run without a person can be granted it, because every call inside it is authorized and decided on its own; for Lars to review. |
| SC4 | Built by lane SC (2026-10-28): **the agent's opt-in is lane SW's `ConfigureAgentContext(...).AllowScripts()` and the exposure's `mcp.AllowScripts()`**; the package's builder only sets limits (`ConfigureAgent`, `ConfigureMcp`, `ConfigureDefaults`) and the destructive opt-in, because one ceiling in code is easier to reason about; for Lars to review. |
| SC5 | Built by lane SC (2026-10-28): **a destructive tool opts in to scripts by its id in the registration (`AllowDestructive(toolId)`)**, passed to the step session's tool filter, not with a tool attribute, because that needs no change to the generator or to any contract hash; for Lars to review. |
| SC6 | Built by lane SC (2026-10-28): **limits default to read-only scripts (`MaxMutationsPerScript = 0`) for agents and MCP, 20 tool calls, 100,000 statements, 16 MiB, 30 seconds and 16 KiB of result**; the tool-call cap is at most 64 and maps onto the step session's `MaxSteps`, and `MaxMutationsPerScript` onto `MaxMutationSteps`, because the runtime then enforces the same caps a second time; for Lars to review. |
| SC7 | Built by lane SC (2026-10-28): **a script has at most 10,000 characters (8,000 by default)**, so the call's stored arguments of at most 64 KB of JSON hold the whole script even when every character is escaped and the activity always shows it: no migration; for Lars to review. |
| SC8 | Built by lane SC (2026-10-28): **over MCP the script tool is added per request through a new seam of `NewHeap.Platform.AI.Mcp`, `INhAiMcpToolContributor`**, which receives exactly the governed tools the export lists for that caller, because the tool's description lists the caller's own tools; it carries the script tool's descriptor, so lane SW's exposure ceiling and switch list or refuse it, and it runs with the MCP budget purpose, so lane CO's runtime runs its calls through the same exposure; for Lars to review. |
| SC9 | Built by lane SC (2026-10-28): **the description reads the turn's tools from `NhAssistantToolStepTurn`, a small public read added to `NewHeap.Platform.AI.Chat`**, set when the interceptor registers the offered tools, because the model reads the description before the call opens its step session; the session stays the authority on what a step may call; for Lars to review. |
| SC10 | Built by lane SC (2026-10-28): **the package references `NewHeap.Platform.AI.Chat` and runs every script on the assistant's step runtime, also over MCP**, so a host needs the assistant to offer scripts; the fake host of the engine tests stays in the tests; for Lars to review. |
| SC11 | Built by lane SC (2026-10-28): **the description lists full signatures as long as they fit `MaxDescriptionLength`, then argument names, then names**, and `console.log` output (at most 20 lines) returns with the result, because both help the model correct a script in one step; for Lars to review. |
| SC12 | Built by lane SC (2026-10-28): **Acornima is pinned explicitly at 1.7.0, the version Jint 4.17.0 is built against**, because the package reads Acornima's parse errors for the line and column of a syntax error and its syntax tree to refuse a program that closes its own function; for Lars to review. |
| SC13 | Built by lane SC (2026-10-28): **the sample switches scripts on in its test host with `NewHeap:AI:Assistant:Scripts`** and its discovery policy lets `scripts.run` through within the division scope, because the sample's own toggle (SPM-371) is lane SW's; merged in the train, SPM-373 and SPM-374 should switch scripts on through that toggle instead; for Lars to review. |
| SC14 | Built by lane SC (2026-10-28), at the merge of main: **SPM-373 and SPM-374 switch scripts on through the portal's own setting** (`SampleAssistantCompositionSettings.SaveAsync`, scripts on and compositions off for the project assistant) on a host with `AddSampleAssistantCompositions`, as SPM-372 does for compositions, instead of `NewHeap:AI:Assistant:Scripts`; this replaces SC13's configuration, because the running sample answers from that table; for Lars to review. |
| SC15 | Built by lane SC (2026-10-28), at the merge of main: **`NhAssistantToolStepTurn` stays public next to lane CO's runtime, and the turn scope's `OfferedTools` is the only list of the turn's tools**: the interceptor's internal `OfferedTools`, which CO's `BeginAsync` reads, now returns the turn scope's list, and both the session and `NhAssistantToolStepTurn.Current` select their tools with one helper (`NhAssistantToolChains.StepToolsOf`), because CO has no read before the call (the session's `Tools` exist only once the call opened it) and two lists could drift; for Lars to review. |
| SC16 | Built by lane SC (2026-10-28), at the merge of main: **SPM-371's effective-state test composes the host as `Program.cs` does, with the project overview**, and now expects `not-allowed-for-data-agents` for compositions and `off-by-application` for the script tool of the scripting desk instead of `not-installed` for both, because the sample registers the script tool and a composition since this train; for Lars to review. |

### Lane FX

| # | Choice |
|---|---|
| FX1 | Built by lane FX (2026-10-28): **a rule turn selects its own tools (`SelectRuleToolsAsync`) instead of taking the model's allow-list**: the same discovery, selectors, composition gate (SW4) and step filter (CO7), capped as the model's, and never the agent's selectors in their place, because the rules of a `Propose` agent only got their tools from that allow-list's fallback to the agent's selectors when it selected nothing. SW's guard turns the fallback off when the turn withholds a composition or the script tool, and SC's script tool, which needs no selector (SW5), is withheld for every agent that is not allowed scripts, so the sample's project starter and project follow-up had every proposal refused (`assistant-proposal-refused`). The model's allow-list, its fallback and SW's guard are unchanged; for Lars to review. |
| FX2 | Built by lane FX (2026-10-28): **a rule turn offers tools that change state from `Propose` autonomy on (`OffersEffectsToRules`)**, because its agent scope requires approval for every effect, so every rule effect is a proposal, as the adapter offers them to a `Propose` agent with `EffectsAsProposals`. The rules of a `Propose` or `Simulate` agent now also propose when it has read tools, compositions or the script tool, which they could not before, and the rules of an `Observe` or `Explain` agent are refused even when their selectors name only tools that change state, which proposed through the fallback before. The guidance said rules offer them only with `Execute`, which the sample's `Propose` rule agents (SPM-298, SPM-302, since SPM-311 moved the starter back to `Propose`) contradicted; the guidance now follows the sample, and v-next records the `Observe`/`Explain` change as breaking for `NewHeap.Platform.AI.Chat`; for Lars to review. |
| FX3 | Built by lane FX (2026-10-28): **the regression tests register the script tool next to rule agents**: `AssistantAgentRuleRunTests` with a `Propose` agent that names only the status tool and is not allowed scripts, a `Propose` agent over every project tool with scripts on, and an `Observe` agent, and `AssistantAgentRunConfigurationTests` with a `Propose` agent created in the administration, which the application does not allow scripts, on both providers. Each fails on the runner before FX1, because a test of rules without the script tool, or with an `Execute` agent, as every library rule test had, never reached the fallback; for Lars to review. |
