# v-next

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer, NewHeap.Platform.AI.Chat.PostgreSql, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Scripting and NewHeap.Platform.AI.Common: rule scripts

- `NhAssistantBuilder.UseRuleScriptsAsData` lets administrators write the rules of an agent, or of one of its
  skills, as a rule script stored as data: the body of an async JavaScript function that takes the place of the
  code rule in its rule turns, runs from the existing triggers and subject sets without a model and ends the run as
  a rule does. The ceiling names the agents (`ForAgent`, `ForAgentsCreatedInAdministration`), the writes a version
  may opt in to (`AllowWrites`, `MaxWritesPerRun`, destructive tools only with `AllowDestructive`) and optional four
  eyes (`RequireFourEyes`). Without it nothing changes.
- A script reads `subject`, `inputs` and `run`, calls the tools of its run through the step runtime with the run's
  grants and accountability (every read, and only the writes its version opted in to, each with the decision it gets
  on its own), raises and resolves alerts with `alerts.raise` and `alerts.resolve` (`ResolveWhenNotRaised` works,
  never on a run on the whole application), proposes with `propose` and ends with `done`, `handOver` or `useSkill`.
  It runs on the engine of `NewHeap.Platform.AI.Scripting` (`INhAssistantRuleScriptEngine`), whose limits
  `NhAiScriptingBuilder.ConfigureRuleScripts` sets; a stopped script fails the run with its script code. Its calls
  carry `NhAiToolCallOrigin.RuleScriptVersion`.
- `INhAssistantRuleScripts` administers versions for the administration and the agent builder: save as candidate or
  straight live, check, catalog, dry run on chosen subjects without effects (kept as the version's latest), set live
  with that dry run, shadow next to the live rules, discard and compare, each change expecting the rule script's
  revision. The endpoints are `admin/agents/{id}/rule-script` and `admin/agents/{id}/skills/{skillId}/rule-script`
  behind the administration policy, with the codes of `NhAssistantRuleScriptCodes`. Agent packages carry the live
  versions as `NhAssistantPackageAgent.RuleScripts` (part `ruleScript` or `ruleScript:{skillId}`) with their locks.

| Breaking change | Required action |
|---|---|
| The `RuleScripts` migrations add the tables `AssistantRuleScript`, `AssistantRuleScriptVersion` and `AssistantRuleScriptRunRecord` on SQL Server and PostgreSQL, which a host with `UseRuleScriptsAsData` reads in rule turns. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. |
| An audit sink that switches over every `NhAssistantAuditEventKind` sees `AdminRuleScriptSaved` (133), `AdminRuleScriptLive` (134), `AdminRuleScriptShadowChanged` (135), `AdminRuleScriptDiscarded` (136) and `AdminRuleScriptDryRun` (137), with `NhAssistantAuditEvent.RuleScriptVersion` and `RuleScriptHash`. | Handle or ignore the new kinds. |
| The administration answers `assistant-rule-scripts-not-allowed`, `assistant-rule-script-write-not-allowed`, `assistant-rule-script-version-not-found`, `assistant-rule-script-not-candidate`, `assistant-rule-script-four-eyes` and `assistant-rule-script-subjects-invalid`, and a package part can be `ruleScript`. | Code that switches over the admin codes or package part kinds handles or ignores them. |
| `UseRuleScriptsAsData` without `AddNewHeapPlatformAIScripting` fails at startup. | Add the scripting package and call `AddNewHeapPlatformAIScripting()`. |
