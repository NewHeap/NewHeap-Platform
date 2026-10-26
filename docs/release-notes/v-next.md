# v-next

## @newheap/platform-ai-chat: the agent editor assigns MCP servers one by one

The agent editor of `@newheap/platform-ai-chat/admin` still creates a new agent with its MCP servers, but
saves an existing agent without `mcpServerIds` and then calls the new
`NhAssistantAdminApiService.unassignMcpServer` and `assignMcpServer` only for the servers the
administrator changed, removals first. A server assigned since the editor opened stays. When one of the
calls fails, the definition stays saved, the editor shows that not every change went through
(`nh-assistant.admin.agent-editor.mcp-servers-failed`, English and Dutch) and its next save continues on
the saved version with the changes that are left. The tools list of a server shows a tool whose settings
were reviewed before any sync saw it (`McpTool.awaitingSync`) as not seen yet
(`nh-assistant.admin.tools.status.awaiting-sync`, `nh-assistant.admin.tools.awaiting-sync-warning`); its
settings change there and the first sync makes it available with them. The mock API follows the server.

| Breaking change | Required action |
|---|---|
| `AdminAgentInput.mcpServerIds` is optional; `AdminAgent.mcpServerIds` stays required. | Code that reads `mcpServerIds` from an `AdminAgentInput` handles `undefined`; a host form that saves an agent omits the list to keep the assignments and sends `[]` only to remove them all. |
| A host that translates every text itself (`translations: 'host'`) misses three new keys. | Add `nh-assistant.admin.agent-editor.mcp-servers-failed`, `nh-assistant.admin.tools.status.awaiting-sync` and `nh-assistant.admin.tools.awaiting-sync-warning`. |

## @newheap/platform-ai-chat: shadow proposals that a newer one replaced

`AgentProposalStatus` and `ApprovalStatus` gain `shadow-superseded`, the status of a shadow proposal that
a newer one for the same objects replaced; the proposal names the newer one in `supersededByApprovalId`,
and the agent-run proposals view shows it as "Shadow, replaced" ("Schaduw, vervangen") with the way to its
successor. The mock API's `NhAssistantMockProposal` takes `supersededByApprovalId` to seed such a
proposal.

| Breaking change | Required action |
|---|---|
| `AgentProposalStatus` and `ApprovalStatus` gain `shadow-superseded`. | A `switch` or `Record` over the statuses that must be exhaustive handles `shadow-superseded`, for example like `shadow`. |

## @newheap/platform-ai-chat: chat-only agents and promotion as the package's next version

The agents page shows an agent in code that works in people's chats only (`AdminAgent.chatOnly`) with
a "Chat only" badge and a hint instead of what it works on, without "Run now", runs, run state changes
or shadow mode. The shadow variants of an agent name the package a data variant is linked to
(`ShadowVariant.packageId`, `packageVersion`), and the promotion's confirmation offers "As the
package's next version" for such a variant, which sends `asPackageVersion` (`ShadowVariantPromotionRequest`)
and reads `ShadowVariantPromotion.package`. English and Dutch texts. The mock API promotes a linked
variant as an import of its package version, names a variant's package, and lets a scenario declare a
chat-only code agent with `chatOnly: true`, whose starts and shadow mode it refuses.

| Breaking change | Required action |
|---|---|
| The mock API's shadow variants carry `packageId` and `packageVersion` (`null` without a package), as the server's do. | None, unless a test compares a variant with `toEqual`; add both properties. |

## @newheap/platform-ai-chat: a message after an expired wait

`NhAssistantStore.submit` sends a message at once in a chat whose waiting question or approval reads as
expired by the browser's clock, instead of queueing it, and a message that waited in the queue goes out
when the wait ends; the server closes the expired wait and starts the message's turn. The new
`NhAssistantStore.waitExpired` signal says so, the panel's composer then no longer says that the chat
waits, and the store reads the conversation again after that turn. A refusal by the server's own
clock (`assistant-conversation-busy`) is handled as before. The mock API closes an expired question or
approval on the next message as the server does.

| Breaking change | Required action |
|---|---|
| A message in a chat whose question or approval passed its `expiresAt` is sent instead of queued until the chat is idle. | None. A host that renders its own composer status reads `waitExpired` to stop saying that the chat waits. |

## @newheap/platform-ai-chat: the administration's texts load with the administration

The English and Dutch texts of the administration (`nh-assistant.admin.`) moved from the root entry
point to `@newheap/platform-ai-chat/admin`, so a host no longer loads them with the panel in its
initial bundle (about 170 kB of minified JavaScript for both languages, which grew with every
administration feature). `NH_ASSISTANT_ADMIN_TRANSLATIONS` holds them, the package publishes them as
`admin/i18n/en.json` and `nl.json`, and `NhAssistantAdminTranslationMerger` merges them as
`NhAssistantTranslationMerger` merges the other texts. Every component of the admin entry point
starts it when it is created, unless the assistant uses `translations: 'host'`; keys the host defines
under `nh-assistant.admin.` still win.

| Breaking change | Required action |
|---|---|
| `NH_ASSISTANT_TRANSLATIONS` and `i18n/en.json`/`nl.json` no longer contain the `nh-assistant.admin.` texts. | A host that provides every text itself (`translations: 'host'`) or copies the bundles adds `NH_ASSISTANT_ADMIN_TRANSLATIONS` from `@newheap/platform-ai-chat/admin`, or `@newheap/platform-ai-chat/admin/i18n/en.json` and `nl.json`. |
| With bundled texts, the `nh-assistant.admin.` texts are merged once an administration component is created. | None, unless a host page shows `nh-assistant.admin.` keys without one; it calls `inject(NhAssistantAdminTranslationMerger).start()`. |
