---
id: nh-ai-rule-scripts
title: "Let administrators write an agent's rules as a script stored as data"
area: backend
reference: ai-scripting
summary: "Set the ceiling of rule scripts in code, let a live version replace a code rule without a model, opt writes in per version within the ceiling, dry-run versions and go live with the administration policy and optional four eyes; administrators write, check, dry-run, compare and set versions live in the rule script section of the agent's Build tab in nh-assistant-admin."
sample-cases: ["SPM-393", "SPM-395"]
public-symbols: ["UseRuleScriptsAsData", "NhAssistantRuleScriptCeiling", "NhAssistantRuleScriptAgentCeiling", "NhAssistantRuleScriptCeilingBuilder", "NhAssistantRuleScriptAgentCeilingBuilder", "NhAssistantRuleScriptLimits", "NhAssistantRuleScriptStatuses", "NhAssistantRuleScriptAuthor", "INhAssistantRuleScriptEngine", "NhAssistantRuleScriptMode", "NhAssistantRuleScriptRun", "NhAssistantRuleScriptProblem", "NhAssistantRuleScriptEndKind", "NhAssistantRuleScriptEnd", "NhAssistantRuleScriptCall", "NhAssistantRuleScriptOutcome", "NhAssistantRuleScriptDeclarations", "NhAssistantRuleScriptToolSignature", "NhAssistantRuleScriptTool", "NhAssistantRuleScriptCallInput", "INhAssistantRuleScripts", "NhAssistantRuleScriptCodes", "NhAssistantRuleScriptWouldOutcomes", "NhAssistantRuleScriptDto", "NhAssistantRuleScriptCeilingDto", "NhAssistantRuleScriptVersionDto", "NhAssistantRuleScriptSaveRequest", "NhAssistantRuleScriptChangeRequest", "NhAssistantRuleScriptShadowRequest", "NhAssistantRuleScriptDryRunRequest", "NhAssistantRuleScriptSubjectDto", "NhAssistantRuleScriptLiveDto", "NhAssistantRuleScriptCheckDto", "NhAssistantRuleScriptCatalogDto", "NhAssistantRuleScriptToolDto", "NhAssistantRuleScriptRunRecordDto", "NhAssistantRuleScriptSubjectRunDto", "NhAssistantRuleScriptEndDto", "NhAssistantRuleScriptCallDto", "NhAssistantRuleScriptWriteDto", "NhAssistantRuleScriptProposalDto", "NhAssistantRuleScriptAlertDto", "NhAssistantRuleScriptAlertKeyDto", "NhAssistantRuleScriptComparisonDto", "NhAssistantRuleScriptLineDto", "NhAssistantRuleScriptCheckRequest", "AssistantRuleScript", "AssistantRuleScriptVersion", "AssistantRuleScriptRunRecord", "NhAssistantRuleScriptRunKinds", "NhAssistantPackageRuleScript", "ConfigureRuleScripts", "RuleScriptLimitsOf", "getRuleScript", "getRuleScriptCatalog", "checkRuleScript", "saveRuleScriptVersion", "dryRunRuleScriptVersion", "getRuleScriptDryRun", "getRuleScriptShadowRuns", "setRuleScriptVersionLive", "setRuleScriptVersionShadow", "discardRuleScriptVersion", "compareRuleScriptVersions", "RuleScript", "RuleScriptStatus", "RuleScriptCeiling", "RuleScriptVersion", "RuleScriptSaveRequest", "RuleScriptChangeRequest", "RuleScriptShadowRequest", "RuleScriptSubject", "RuleScriptDryRunRequest", "RuleScriptCheck", "RuleScriptTool", "RuleScriptCatalog", "RuleScriptRunKind", "RuleScriptSubjectRunStatus", "RuleScriptEnd", "RuleScriptCall", "RuleScriptWouldOutcome", "RuleScriptWrite", "RuleScriptProposal", "RuleScriptAlert", "RuleScriptAlertKey", "RuleScriptSubjectRun", "RuleScriptRunRecord", "RuleScriptLive", "RuleScriptLine", "RuleScriptComparison", "NH_ASSISTANT_RULE_SCRIPT_MAX_DRY_RUN_SUBJECTS", "NH_ASSISTANT_RULE_SCRIPT_MAX_LENGTH", "NH_ASSISTANT_RULE_SCRIPT_MAX_REASON", "NhAssistantMockRuleScriptCeiling", "NhAssistantMockRuleScriptAgent", "NhAssistantMockRuleScriptTool", "NhAssistantMockRuleScriptVersion"]
skills: ["newheap-backend-development"]
providers: ["provider-neutral"]
risk: high
---
## Preferred approach

A rule that runs without a model is a code rule (`UseRuleHandler<T>`) or, where the application allows
it, a rule script: the body of an async JavaScript function stored as data on the agent, or on one of its
skills, that an administrator writes, dry-runs and sets live in the administration, or the agent builder
for them. A live version takes the place of the code rule in every rule turn of its agent or skill; it
runs from the existing triggers and subject sets, calls no model and ends the run as a rule does. A live
script and a code rule are alternatives: the live script wins, and the administration shows which code
rule it replaces (`codeRule`).

Set the ceiling in code with `UseRuleScriptsAsData(scripts => ...)`: `ForAgent(agentId, ...)` for an
agent in code and `ForAgentsCreatedInAdministration(...)` for agents created in the administration;
per agent the writing tools a version may opt in to (`AllowWrites`), the most writes one run makes
(`MaxWritesPerRun`, one by default) and the destructive tools among them that may run in a script at all
(`AllowDestructive`); and `RequireFourEyes()` when another administrator than a version's author sets it
live. Without the call no agent has rule scripts and nothing changes. Rule scripts run on the engine of
`NewHeap.Platform.AI.Scripting`: call `AddNewHeapPlatformAIScripting`, whose `ConfigureRuleScripts` sets
their `NhAiScriptLimits` (statements, time, memory, recursion, tool calls and result size); the writes per
run follow the ceiling. The application fails at startup when the engine is missing.

A script reads its run through frozen values and works through platform functions:

- `subject` (`type`, `id`, `displayName`, `wholeApplication`), `inputs` (the trigger's events, ticks and
  messages with their payload) and `run` (agent, skill, version, trigger, run, `now` on the application's
  clock and `mode`: `live`, `shadow` or `dry-run`);
- `tools.<function name>(input)` for every tool its run may use: every read, and only the writes its
  version opted in to within the ceiling; a write that is not opted in stops the script with
  `ai-script-tool-refused`, a tool the run may not use with `ai-script-unknown-tool`, and one write more
  than the ceiling allows with `ai-script-mutation-limit`;
- `alerts.raise({ kind, key, urgency, parameters, recipients, expiresAt, subject })` and
  `alerts.resolve({ kind, key, subject })` for the kinds the agent lists, with `Alerts(...)` in code or in
  the run settings of an agent created in the administration (SPM-395); another kind stops the script with
  `ai-script-runtime-error`, and a kind that asks for it resolves by omission when the run's script
  raised it no more, never on a run on the whole application;
- `await propose({ tool, input, rationale })`, the rules' path of `NhAssistantRuleContext.ProposeAsync`;
- `done(summary)`, `handOver(reason)` or `useSkill(skillId, reason)` once; text the script returns is
  its summary otherwise.

Every tool call runs through the step runtime as a step of the rule script's call in the run, with the
run's grants and accountability and the decision the same call gets on its own: a write that needs
approval records a deferred proposal and the script goes on, one that needs none runs, and an agent that
only proposes, answers for itself or runs in a shadow thread proposes every write. Each call's origin
names `NhAssistantRuleScriptTool.ToolId`, the version's hash and `NhAiToolCallOrigin.RuleScriptVersion`. The
script's end becomes the rule result of today, so alerts, proposals, revalidation, the run history and
the run's result work unchanged; a stopped script fails the run with its script code.

Administer versions through `INhAssistantRuleScripts`, the one service the administration's endpoints
(`admin/agents/{id}/rule-script` and `admin/agents/{id}/skills/{skillId}/rule-script`) and the agent
builder's tools call, behind the administration policy. Versions are append-only: save a version as a
candidate, which may run in shadow next to the live rules and records what it would do, or straight live;
set a candidate live, discard it or compare two versions. Every change expects the rule script's revision
and is audited content-free (`AdminRuleScriptSaved` to `AdminRuleScriptDryRun`). A dry run runs a version
on up to twenty chosen subjects without effects: reads run with the rights of the agent's runs, writes,
proposals and alerts are recorded with what they would become and never happen. Going live returns the
version's latest dry run, or says there is none; a dry run is not required. A version's live script
travels in agent packages (part `ruleScript` or `ruleScript:{skillId}`) with the package's locks.

In `@newheap/platform-ai-chat` the agent's Build tab of `nh-assistant-admin` holds a "Rule script" section
for the agent's own rules and, one at a time, for each of its skills. It shows only when the ceiling
allows the agent rule scripts (`allowed`), so an application without `UseRuleScriptsAsData` sees no
change. It names the version in effect, the candidate and whether it runs in shadow, the code rule a live
version takes the place of and what the ceiling allows. The editor is a plain text area (Tab indents,
Escape and then Tab leaves it) with the caret's line and column, next to what the script sees from
`GET .../catalog?writes=` for the writes chosen now: the tools with their signatures, writes and destructive
tools marked and the reason a tool cannot be called, the alert kinds, the platform functions and the limits,
each with a helper that inserts its call. "Check" (`POST .../check`) names the line and column of a problem
and moves the caret there. The writes are chosen only within the ceiling, with its writes per run; a version
is saved as the candidate, as the candidate in shadow or, unless four eyes applies, straight live, with an
optional reason and the rule script's revision. Every version lists with its author (a person, or an agent
such as the builder for a person), reason and times, and opens its script, a dry run on up to twenty
subjects chosen with the subject picker of "Run now", a comparison with another version (line diff, writes
added and removed and both latest dry runs) and, for the candidate, shadow on or off, its shadow runs,
discarding and going live. Going live first shows the version's latest dry run or says it has none, and
explains four eyes; a dry run there replaces what it shows. Each record shows per subject its status, end,
calls in order, writes with what they would become and their input, proposals, alerts raised and resolved
and its log. The calls are `NhAssistantAdminApiService` methods (`getRuleScript` to `compareRuleScriptVersions`)
with the `RuleScript*` models; the mock API of `@newheap/platform-ai-chat/testing` serves the same routes from
a scenario's `admin.ruleScripts`.

## Avoid

- Granting rule scripts to every agent instead of the agents whose rules administrators own.
- Allowing writes in the ceiling that people should decide one by one, or raising `MaxWritesPerRun` to loop over changes.
- Treating a write that became a proposal as done, or a dry run's `would-run` as a promise.
- Letting an application-wide script stand in for per-subject runs over large sets.
- Writing a script that relies on a code rule running next to it: a live script replaces the code rule.
- Hiding a dry run's stopped subjects or showing a write's `would-run` as something that happened; the
  administration shows every subject and says that nothing of a record happened.

## Verification

Prove the engine without a database: the run's values are frozen, alerts of a listed kind are raised and
resolved, an unlisted kind is a `TypeError`, the run ends once, a tool the run does not offer, a write not
opted in, one write more than the ceiling and a destructive write without `AllowDestructive` are refused
before they reach a tool. On SQL Server and PostgreSQL: a live script runs instead of the code rule, its
write without approval runs and its write that needs approval records a proposal; a dry run changes no row,
records no proposal and delivers nothing; a candidate in shadow runs next to the code rule without
effects; four eyes refuses the author; an application-wide script resolves nothing by omission; a
version without the administration policy cannot be saved or set live; a live script round-trips an agent
package with its lock. In the administration, an Angular spec on the mock API proves that the section
shows only within the ceiling, the editor's catalog, Tab, the check's line and column and the writes within
the ceiling, saving as candidate and in shadow, a dry run per subject with its calls, writes and alerts, a
dry run stopped at a write not opted in, the latest dry run before going live, four eyes refusing the author,
shadow runs, comparing, discarding and a stale revision that keeps the editor's text. SPM-393 is the
executable reference.
