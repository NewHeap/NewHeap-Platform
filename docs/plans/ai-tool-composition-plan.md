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
| A | SW: switch and status | TC8, the activity part of TC1 | none | SPM-371 |
| A | CO: compositions | TC2, TC5, TC7 (compositions) | none | SPM-372 |
| A | SC: script tool | TC3, TC4, TC5, TC6, TC7 (script) | possibly one, for the stored script text | SPM-373–374 |
| later | LL: learning | TC9 | to be decided | SPM-375 |

SW goes first or defines the seam's contract at the start of the train, because CO and SC both check it.
