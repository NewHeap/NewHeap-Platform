# v-next

## NewHeap.Platform.AI.Chat.AspNet: a warning when a delivery skips a person the principal factory cannot rebuild (FB89)

- A live update, Web Push or in-app notification, agent notification or alert that skips a person because the
  application's `INhAiPrincipalFactory` rebuilds nobody for them now logs one content-free warning per person per
  hour, without the actor id. It names `INhAiPrincipalFactory`: when people who do sign in are skipped, the
  application's actor ids are its own and need a principal factory of its own. Who receives what does not change.

## @newheap/platform-common: regional languages fall back to their base language (FB88)

- `NhTranslateBrowserLoader` loads `nl.json` when the file of a regional language such as `nl-NL` or `nl_BE`
  does not exist (`404`), and `en.json` for `en-GB`, so the application's texts follow the libraries' own texts,
  which already did. A regional file that exists is used as it is; other errors propagate as before. An
  application that relied on a regional language showing no texts of its own now shows those of its base language.

## @newheap/nh-toastr: a development warning for a toast without a container (FB90)

- In development mode `NhToastrService` warns once in the console when a toast is shown while no
  `NhToastrContainerComponent` is rendered, and names the fix: one `<nh-toastr-container>` in the root template and
  `provideNhToastr(...)` in the root providers. Production builds log nothing, and the toasts behave as before.

## newheap-platform: upgrades across several versions (FB91)

- The consumer skill's package-sources reference asks to act on every breaking change between the installed and the
  target version, and names `npm run release:breaking`, which lists every `Breaking change | Required action` row of
  the release notes between two versions per release unit, grouped by version and package.

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: idempotent SQL scripts of the assistant schema (FB92)

- Every release now carries `newheap-assistant-schema-sqlserver-<version>.sql` and
  `newheap-assistant-schema-postgresql-<version>.sql`, the idempotent scripts of the assistant migrations for the
  default `nhai` schema, for applications that apply the schema themselves instead of `RunMigrations`.
- The provider projects' design-time factories now generate SQL as a host does. A script written with
  `dotnet ef migrations script` used to keep the schema placeholder of the `McpReaders` migration, which no database
  accepts; `-- --schema <name>` now writes the script for another schema.

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
