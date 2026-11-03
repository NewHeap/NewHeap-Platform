# Rule Scripts and Agent Builder Plan

A rule that runs without a model is C# today (`UseRuleHandler<T>`). Only a developer can write one. The script
tool (`run_script`, `NewHeap.Platform.AI.Scripting`) lets a model chain tools inside one turn, in a sandbox,
with every call governed. But the script is never kept, and a model always surrounds it. Alerts (FB87) showed
where this hurts. A check such as "no permit three days before the start" is deterministic and should run
every day without a model. Today it needs a developer, or a model agent that is slower, costs a model call per
subject and can be misled by what it reads.

This plan adds two things.
- **Rule scripts:** a script stored as data replaces a code rule, runs from the existing triggers without a
  model, and may write where the code allows it.
- **An agent builder:** an assistant agent in the administration that helps an administrator build a whole
  agent, rule scripts included.

It is a plan, not usage documentation. Once it is built, the guidance rules, the release notes and the sample
cases are authoritative.

## Status

Decided by Lars on 2026-11-03, one by one.

As advised:
- where a script lives;
- writes per tool within the code's ceiling;
- dry runs that are available and shown when a version goes live;
- the scope per subject, plus small application-wide scripts;
- the builder in the administration;
- one train with three lanes.

Not as advised:
- a new version may go live without a mandatory shadow period;
- the builder may also set things live, after an approval in the chat.

Lars asked for writes and for the builder's help with whole agents from the start, instead of later. Not built
yet.

## Decisions

| # | Decision |
|---|---|
| RB-1 | **Rule scripts.** A script stored as data takes the place of a code rule in the run settings of an agent or a skill. It runs from the existing triggers without a model and ends the run as a rule does: a summary, proposals, alerts raised and resolved (`ResolveWhenNotRaised` works), or a hand-over to the model. |
| RB-2 | **A script lives on the agent or skill.** It is versioned as data skills are: live, a candidate that may run in shadow, and the history. It travels in agent packages. Code sets the ceiling, as `UseCompositionsAsData` does. |
| RB-3 | **Going live.** A new version may go live at once, or run as a candidate in shadow first. Shadow is available, not mandatory. Going live needs the administration policy. The application may require four eyes in code. |
| RB-4 | **Writes are opt-in per tool, within the code's ceiling, from the start.** Code sets per agent which writing tools a script may call and how many writes a run may make. The administrator chooses per script version which of those. Each write keeps its own decision: approval policy, guards and budget. A tool that needs approval records a proposal; one that does not runs. A destructive tool needs `AllowDestructive`. |
| RB-5 | **Dry runs.** The administrator and the builder can run a version on chosen subjects without effects. The dry run records the calls, writes, proposals and alerts it would make. Going live shows the version's latest dry run, or says it has none. A dry run is not required. |
| RB-6 | **Scope.** A script runs per subject, through the triggers and the subject sets registered in code (`AddSubjectSet`). A script for the whole application is allowed within the same limits, for small sets; it never resolves alerts by omission. |
| RB-7 | **The agent builder** is an assistant agent the application switches on with `AddAgentBuilder()` and a model profile of its choice. Only people with the administration policy see it. |
| RB-8 | **What the builder may do.** It reads the catalog: tools with their script signatures, alert kinds, trigger events, subject sets, existing agents and their settings. It creates and changes agents, instructions, skills, triggers, run settings and rule scripts, runs dry runs, and sets versions live. Every save and every step live is a governed call that the administrator approves in the chat. It acts with the administrator's own rights and the administration policy. It never changes grants, technical accounts, MCP credentials or what code allows, and never deletes. |
| RB-9 | **Where the builder works:** the chat on the agent page (Build tab), and "New agent with help" in the agent list. |
| RB-10 | **One train (36) with three lanes**: RS first, then RU and BA in parallel. |

## Rules for all of it

- **Compatibility:** everything is opt-in; an application that registers nothing new sees no change. A breaking
  change gets a `Breaking change | Required action` row.
- **Repository rules:**
  - code and migrations for SQL Server and PostgreSQL;
  - tests with authorization negatives;
  - release notes;
  - guidance and consumer guide;
  - executable sample cases (SPM-393 for rule scripts, SPM-394 for the builder);
  - the public API snapshot;
  - English and Dutch texts and the Angular lifecycle extension point rules;
  - no new packages without asking (the script editor uses no new editor package).
- **The engine is the existing one.** The same Jint sandbox, strict mode, no .NET access, modules, `eval`,
  `Function` or timers, with the same limits (`NhAiScriptLimits`), signatures (`NhAiScriptSignatures`), codes
  and call origin. Every call a script makes gets the decision the same call gets on its own.
- **Audit:** content-free, the kinds reserved from 133 upward.
- Nothing names a consuming application.

## Lanes

### RS: the rule script runtime

1. **Storage.** A rule script version per agent or skill, as data and append-only: live, candidate (shadow
   optional), superseded and discarded, with its author (a person, or the builder on behalf of a person), the
   reason, the writing tools it may call, and the hash of its text. Migration on both providers. Included in
   agent packages.
2. **The code ceiling.** `UseRuleScriptsAsData(...)` sets which agents may have rule scripts, the limits, and
   for writes the tools allowed and the maximum writes per run (RB-4). Without it nothing changes.
3. **Running.**
   - A rule turn of an agent or skill with a live script runs it instead of a code rule.
   - The script gets the run's subject, its events or tick, and its message.
   - Tools are called through the step runtime with the run's grants and accountability, as a script call
     does today.
   - Platform functions inside the script raise and resolve alerts, propose, and end the run with a summary or a
     hand-over to the model.
   - A shadow candidate runs next to the live version and records what it would do.
   - Failures stop the script with the existing codes, and the run reports them.
4. **The outcome** is the rule result of today, so alerts, proposals, revalidation and the run history work
   unchanged.
5. **Dry runs** (RB-5) through a service the administration and the builder call. It runs on chosen subjects
   without effects, records what would happen, and keeps the latest dry run per version.
6. **Admin API** for versions, saving, dry runs, setting live, discarding and the history, with content-free
   audit.
7. **Sample SPM-393:** a deadline rule written as a script that warns when a project starts within three days
   and misses a document, with a dry run, a version going live and a write opted in.

### RU: the administration

- On the agent page, under Build, the rule script of the agent and of each skill:
  - an editor (a plain text area, no new package) that lists the signatures of the tools the script may call,
    the alert kinds and the platform functions;
  - the writes it may make, chosen within the code's ceiling;
  - the versions, with their history and a comparison;
  - "dry run" on chosen subjects, showing what it would do;
  - "set live", showing the latest dry run;
  - "try in shadow" and "discard".
- English and Dutch texts, mocks and specs, and the sample's administration.

### BA: the agent builder

- **`AddAgentBuilder(options)`:** a platform agent with its own instructions and the model profile the
  application names, offered only to people with the administration policy.
- **Its tools are governed platform tools:**
  - reading the catalog (RB-8);
  - drafting and saving agents, instructions, skills, triggers, run settings and rule scripts;
  - dry runs;
  - setting a version live.

  Every save and every step live needs the administrator's approval inline in the chat, and runs with the
  administrator's own rights through the same administration services the screens use. It never touches
  grants, technical accounts, MCP credentials or the code's ceilings, and never deletes.
- **In the administration:** the chat on the agent page (Build tab) works on that agent, and "New agent with
  help" in the agent list starts a new one. The builder explains in plain language what it changes and why.
- **Sample SPM-394:** the builder turns "warn me when a project has no permit three days before its start" into
  a rule script, runs a dry run and sets it live after the approval. This is proven with the scripted chat
  client.

## Lane choices

None yet.
