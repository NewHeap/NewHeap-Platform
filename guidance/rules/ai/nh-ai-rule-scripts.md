---
id: nh-ai-rule-scripts
title: "Let administrators write an agent's rules as a script stored as data"
area: backend
reference: ai-scripting
summary: "Set the ceiling of rule scripts in code, let a live version replace a code rule without a model, opt writes in per version within the ceiling, dry-run versions and go live with the administration policy and optional four eyes."
sample-cases: ["SPM-393"]
public-symbols: ["UseRuleScriptsAsData", "NhAssistantRuleScriptCeiling", "NhAssistantRuleScriptAgentCeiling", "NhAssistantRuleScriptCeilingBuilder", "NhAssistantRuleScriptAgentCeilingBuilder", "NhAssistantRuleScriptLimits", "NhAssistantRuleScriptStatuses", "NhAssistantRuleScriptAuthor", "INhAssistantRuleScriptEngine", "NhAssistantRuleScriptMode", "NhAssistantRuleScriptRun", "NhAssistantRuleScriptProblem", "NhAssistantRuleScriptEndKind", "NhAssistantRuleScriptEnd", "NhAssistantRuleScriptCall", "NhAssistantRuleScriptOutcome", "NhAssistantRuleScriptDeclarations", "NhAssistantRuleScriptToolSignature", "NhAssistantRuleScriptTool", "NhAssistantRuleScriptCallInput", "INhAssistantRuleScripts", "NhAssistantRuleScriptCodes", "NhAssistantRuleScriptWouldOutcomes", "NhAssistantRuleScriptDto", "NhAssistantRuleScriptCeilingDto", "NhAssistantRuleScriptVersionDto", "NhAssistantRuleScriptSaveRequest", "NhAssistantRuleScriptChangeRequest", "NhAssistantRuleScriptShadowRequest", "NhAssistantRuleScriptDryRunRequest", "NhAssistantRuleScriptSubjectDto", "NhAssistantRuleScriptLiveDto", "NhAssistantRuleScriptCheckDto", "NhAssistantRuleScriptCatalogDto", "NhAssistantRuleScriptToolDto", "NhAssistantRuleScriptRunRecordDto", "NhAssistantRuleScriptSubjectRunDto", "NhAssistantRuleScriptEndDto", "NhAssistantRuleScriptCallDto", "NhAssistantRuleScriptWriteDto", "NhAssistantRuleScriptProposalDto", "NhAssistantRuleScriptAlertDto", "NhAssistantRuleScriptAlertKeyDto", "NhAssistantRuleScriptComparisonDto", "NhAssistantRuleScriptLineDto", "NhAssistantRuleScriptCheckRequest", "AssistantRuleScript", "AssistantRuleScriptVersion", "AssistantRuleScriptRunRecord", "NhAssistantRuleScriptRunKinds", "NhAssistantPackageRuleScript", "ConfigureRuleScripts", "RuleScriptLimitsOf"]
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
  `alerts.resolve({ kind, key, subject })` for the kinds the agent lists with `Alerts(...)`; a kind that
  asks for it resolves by omission when the run's script raised it no more, never on a run on the whole
  application;
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

## Avoid

- Granting rule scripts to every agent instead of the agents whose rules administrators own.
- Allowing writes in the ceiling that people should decide one by one, or raising `MaxWritesPerRun` to loop over changes.
- Treating a write that became a proposal as done, or a dry run's `would-run` as a promise.
- Letting an application-wide script stand in for per-subject runs over large sets.
- Writing a script that relies on a code rule running next to it: a live script replaces the code rule.

## Verification

Prove the engine without a database: the run's values are frozen, alerts of a listed kind are raised and
resolved, an unlisted kind is a `TypeError`, the run ends once, a tool the run does not offer, a write not
opted in, one write more than the ceiling and a destructive write without `AllowDestructive` are refused
before they reach a tool. On SQL Server and PostgreSQL: a live script runs instead of the code rule, its
write without approval runs and its write that needs approval records a proposal; a dry run changes no row,
records no proposal and delivers nothing; a candidate in shadow runs next to the code rule without
effects; four eyes refuses the author; an application-wide script resolves nothing by omission; a
version without the administration policy cannot be saved or set live; a live script round-trips an agent
package with its lock. SPM-393 is the executable reference.
