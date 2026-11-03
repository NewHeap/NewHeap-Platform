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

## @newheap/platform-ai-chat: rule scripts in the administration

- The agent's Build tab of `nh-assistant-admin` has a "Rule script" section for the agent's own rules and each
  skill, shown only when the code's ceiling allows the agent rule scripts. Administrators write a version in a
  plain text editor next to what the script sees (tools with their signatures, alert kinds, platform functions
  and limits), check it with the line and column of a problem, choose its writes within the ceiling and save it
  as the candidate, in shadow or straight live (not under four eyes). Every version shows its author, reason and
  times, and opens a dry run on up to twenty chosen subjects, a comparison with another version, and for the
  candidate shadow on or off, its shadow runs, discarding and going live after its latest dry run is shown.
- `NhAssistantAdminApiService` gains `getRuleScript`, `getRuleScriptCatalog`, `checkRuleScript`,
  `saveRuleScriptVersion`, `dryRunRuleScriptVersion`, `getRuleScriptDryRun` (`null` without one),
  `getRuleScriptShadowRuns`, `setRuleScriptVersionLive`, `setRuleScriptVersionShadow`, `discardRuleScriptVersion`
  and `compareRuleScriptVersions`, each with a `skillId` that is `null` for the agent's own rules, and the
  `RuleScript*` models. `NH_ASSISTANT_SERVER_ERROR_CODES` lists the `assistant-rule-script*` codes with English and
  Dutch texts.
- The mock API of `@newheap/platform-ai-chat/testing` serves the same routes from a scenario's `admin.ruleScripts`
  (`NhAssistantMockRuleScriptCeiling`), with versions that exist before it starts, dry runs from the tools and
  alert kinds a script names, shadow runs, four eyes and the server's refusals.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: the agent builder

- `AddAgentBuilder(builder => builder.ProfileName = ...)` (`NhAssistantAgentBuilderExtensions`,
  `NhAssistantAgentBuilderOptions`) switches on the agent builder, `nh-agent-builder`: an assistant agent that helps
  an administrator build a whole agent in a chat, with the policy `NhAssistantAgentBuilderIds.Policy`, which requires
  the access and administration policies. It reads the catalog, the agents and what a rule script may call, dry-runs
  rule scripts without effects, and creates and changes agents, run settings, data skills, data triggers and rule
  scripts and sets versions live or in shadow, each save and step live after the administrator's approval on an
  inline card. It works with the administrator's own rights through `INhAssistantAgentConfiguration`,
  `INhAssistantAgentDefinitions`, `INhAssistantAgentControl` and `INhAssistantRuleScripts`, so their checks, ceilings
  and four eyes apply, and it has no tool for grants, technical accounts, MCP servers, the code's ceilings or
  deletions. Without the call nothing changes.
- Its tools, `assistant.agent-builder.*` (`NhAssistantAgentBuilderIds.ToolIdPrefix`), are offered to the builder
  alone, whatever another agent's selectors say, never to MCP clients, and `GET admin/tools` leaves them out. An
  application whose discovery policy allow-lists tools lets the prefix through.

| Breaking change | Required action |
|---|---|
| The administration refuses an agent with the id `nh-agent-builder`, which the platform reserves for the agent builder, as `id: not-allowed`, also when the builder is not switched on. | Rename an agent created in the administration with that id, for example through an agent package, before you change it. |

## @newheap/platform-ai-chat: the agent builder in the administration

- The Build tab of an agent's page in `nh-assistant-admin` has "Build with help", the assistant's chat panel in page
  mode with the agent builder about that agent, and the list of agents has "New agent with help", which continues on
  the page of the agent the builder created with the same chat. Both show only when the person may chat with the
  builder, never on the builder's own page. The texts are `nh-assistant.agent-builder.*` and
  `nh-assistant.admin.agent-builder.*` in English and Dutch.
- `NhAssistantStore.setPageContextOverride(context)` gives the page context of the messages a component sends in
  place of the config's `getPageContext`, as the builder's chat does with the agent it works on; `null` gives it back.
- The builder's chat is headed "Agent builder" and offers the builder only. Once it closes, or its page is left, the
  assistant's chat elsewhere in the application shows the chat or the agent it showed before.
- `nh-assistant-panel` takes `title`, the heading the host translated, and `agents`, the ids of the only agents its
  agent picker offers; the picker hides when one remains. Unset, they keep the assistant's title and every agent the
  person may choose.
