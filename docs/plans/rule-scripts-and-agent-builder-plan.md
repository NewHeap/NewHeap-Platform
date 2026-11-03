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

| # | Choice | Status |
|---|---|---|
| RS1 | A live rule script and a code rule are alternatives: the live script wins in the rule turn and the code rule does not run; `GET admin/agents/{id}/rule-script` names the code rule it replaces (`codeRule`). | For Lars to review |
| RS2 | The ceiling (`UseRuleScriptsAsData`) lives in AI.Chat and names agents, writes, writes per run, destructive tools and four eyes; the engine limits are `NhAiScriptLimits` set with `AddNewHeapPlatformAIScripting(s => s.ConfigureRuleScripts(...))`, for all agents or per agent, and the ceiling's `MaxWritesPerRun` replaces their `MaxMutationsPerScript`. `AllowWrites` alone allows one write per run. | For Lars to review |
| RS3 | Destructive writes need `AllowDestructive` on the agent's ceiling and must be in `AllowWrites`; the scripting package's global `AllowDestructive` does not apply to rule scripts. | For Lars to review |
| RS4 | A write of a live script gets the decision of the agent's own run: an Execute agent follows the tool's approval requirement and policy (deferred in a rule turn); an agent that only proposes, answers for itself (accountability `Agent`) or runs in a shadow thread proposes every write. `propose()` always records a proposal, also for a write that is not opted in. | For Lars to review |
| RS5 | A candidate in shadow runs without effects inside the live run (after the live rules, on the same subject and inputs), or before the model when the agent or skill has no rules; it is kept as a shadow run (the newest 20 per version), not as a run in the shadow thread. | For Lars to review |
| RS6 | In shadow and dry runs a write returns `assistant-rule-script-dry-run` to the script and is recorded as `would-run`, `would-propose` or `would-decide`; `propose()` returns the outcome `dry-run`. Reads run with the agent's grants and accountability on the subject, never with the administrator's rights. | For Lars to review |
| RS7 | The rule script's call is a call of the run (tool id `assistant.rule-script`) with its version and hash, so the thread shows it and its tool calls are its steps; no model is offered it and the composition activity does not list it. | For Lars to review |
| RS8 | One candidate at a time: saving any new version discards the waiting candidate, and saving straight live also supersedes the live version. Every change expects the rule script's revision (`expectedRevision`), one counter per agent or skill. | For Lars to review |
| RS9 | A script that ends without `done`, `handOver` or `useSkill` ends with the text it returned, otherwise with "The rule script ran."; ending twice is a `TypeError`. Errors of platform functions (an unlisted alert kind, bad arguments) are `TypeError`s the script may catch, otherwise `ai-script-runtime-error`. | For Lars to review |
| RS10 | An agent package's import adds its rule script as the next version, live, or as the candidate when the host requires four eyes; the link locks `script` (by its hash) and `writes`. Under four eyes nobody saves straight live. | For Lars to review |
| RS11 | SPM-393: the sample's projects have no start date, so the document check uses the deadline (due within three days, the schedule's subject set) and the project's documents (`project-documents.read`) as what it misses; its one write is `projects.request-review`, which needs approval and becomes a proposal. | For Lars to review |
| RU1 | The rule script is a section of its own on Build, between Tools and Skills (one placement line in the agent page, inside its run configuration), with a switch between the agent's own rules and each skill; one scope is read at a time. It is not in the Build index, because whether it shows is only known after its `GET`. | For Lars to review |
| RU2 | Without the ceiling (`allowed: false`), or on a server without the endpoint (404), the section shows nothing, so an application that registers nothing new sees no change; any other read failure shows the section with "Try again". | For Lars to review |
| RU3 | The editor is a plain text area: Tab inserts two spaces or indents the selected lines, Shift+Tab outdents, and Escape then Tab leaves the field, so it is no keyboard trap. Helpers insert a tool's call or an alert's `raise` at the caret. The parser's message of a check and the script's message of a stopped run show as a monospaced detail under the translated code, with the line and column and "Go to line". | For Lars to review |
| RU4 | A dry run chooses its subjects with the subject picker of "Run now" (search, or an id for a type without search, and the whole application for an app-wide agent) among the agent's own types, collected in a list of at most twenty. | For Lars to review |
| RU5 | Only the ceiling's writes are offered, with its writes per run; a destructive write the ceiling does not let run in a script is shown disabled. The catalog is read again for the chosen writes (`writes=` with an empty value for none). A new version starts from the live version, or the version it is made from, with those of its writes the ceiling still allows. | For Lars to review |
| RU6 | "Save and set live" is disabled under four eyes. Going live shows the candidate's latest dry run or says it has none, offers a dry run in place that replaces what it shows, and explains four eyes; the client does not know the signed-in person's id, so the author's refusal is the server's `assistant-rule-script-four-eyes` with a text that says who sets it live. | For Lars to review |
| RU7 | Shadow on and off apply at once, as they are reversible; going live and discarding confirm first with an optional note. After every change the section reads the rule script again for its revision and statuses. A stale revision in the editor offers to read the rule script again and keeps the text and choices. | For Lars to review |
| RU8 | The section and its parts stay internal to the admin entry point, as the alert model and running without approval do; the public surface is the `NhAssistantAdminApiService` methods, the `RuleScript*` models and constants, and the mock's scenario types. | For Lars to review |
| RU9 | A write in a dry run or shadow run, a call that `failed` with `assistant-rule-script-dry-run`, reads "Recorded, not run" next to what it would become. That code joins `NH_ASSISTANT_SERVER_ERROR_CODES`, because the server's status table lists it. | For Lars to review |
| RU10 | The mock does not run scripts: its check balances brackets and quotes, and its dry run reads the tools and alert kinds a script names, stopping at a write the version did not opt in to. The assistant lab seeds the document check with a live version and the project lead's candidate in shadow; the sample's real administration needs no change, as the API sets the ceiling. | For Lars to review |
