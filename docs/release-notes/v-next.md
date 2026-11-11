# v-next

## @newheap/platform-ai-chat: client tools

`provideNhAssistantClientTools` registers the browser's handlers of client tools, with the built-in
`nhAssistantNavigateTool` and `nhAssistantModalTool` in `@newheap/platform-ai-chat/client-tools`.

| Breaking change | Required action |
|---|---|
| `ToolCallStatus`, `NhAssistantToolCallGroupState` and `NhAssistantSseEventMap` gain `awaiting-client` and `client-tool.requested`. | Code that switches over them exhaustively handles the new values. |
| New translation keys `nh-assistant.tool-call.status.awaiting-client`, `nh-assistant.tool-group.awaiting-client` and `nh-assistant.errors.assistant-client-tool-*`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## @newheap/platform-ai-chat: agents keep a visible plan

| Breaking change | Required action |
|---|---|
| `MessagePart` gains `PlanPart` (`type: 'plan'`) and `NhAssistantSseEventMap` gains `plan.updated`. | Code that switches over them exhaustively handles the new values. |
| New translation keys `nh-assistant.plan.*`, `nh-assistant.errors.assistant-plan-invalid`, `nh-assistant.errors.assistant-plan-update-limit-reached` and `nh-assistant.admin.run-config.plan.*`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## @newheap/platform-ai-chat: MCP elicitation wait time

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.admin.server-editor.elicitation-wait`, `nh-assistant.admin.server-editor.elicitation-wait-hint` and `nh-assistant.admin.server-editor.elicitation-wait-invalid`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## @newheap/platform-ai-chat: theme defaults once per application

| Breaking change | Required action |
|---|---|
| The components take their light and dark theme defaults from one stylesheet that the first assistant component adds to the document and that stays while the application runs, instead of each from its own styles; every component element and the panel's drawer carry the attribute `data-nh-assistant-tokens`. | None: `--nh-assistant-*` values on `:root`, an ancestor or a component element still win. A test that compares a component element's exact attributes or counts style elements expects the attribute and the one stylesheet. |
