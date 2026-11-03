# v-next

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
