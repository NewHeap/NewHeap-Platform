# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: client tools

`AddClientTool<TInput>` declares a tool that runs in the person's browser, such as opening a page or
the application's own create form prefilled; the turn waits for that browser's report, which
`POST conversations/{id}/client-tools/{invocationId}/result` sends.

| Breaking change | Required action |
|---|---|
| `NhAssistantAuditEventKind` gains `ClientToolRequested`, `ClientToolCompleted` and `ClientToolExpired`, and `NhAssistantToolCallStatuses` gains `awaiting-client`. | Let audit sinks and clients that map every kind or call status accept the new values. |

## @newheap/platform-ai-chat: client tools

`provideNhAssistantClientTools` registers the browser's handlers of client tools, with the built-in
`nhAssistantNavigateTool` and `nhAssistantModalTool` in `@newheap/platform-ai-chat/client-tools`.

| Breaking change | Required action |
|---|---|
| `ToolCallStatus`, `NhAssistantToolCallGroupState` and `NhAssistantSseEventMap` gain `awaiting-client` and `client-tool.requested`. | Code that switches over them exhaustively handles the new values. |
| New translation keys `nh-assistant.tool-call.status.awaiting-client`, `nh-assistant.tool-group.awaiting-client` and `nh-assistant.errors.assistant-client-tool-*`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: agents keep a visible plan

| Breaking change | Required action |
|---|---|
| The `AgentRunSettingsKeepsPlan` migrations add the column `KeepsPlan` (not null, default `false`) to `AssistantAgentRunConfiguration` on SQL Server and PostgreSQL. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. |

## @newheap/platform-ai-chat: agents keep a visible plan

| Breaking change | Required action |
|---|---|
| `MessagePart` gains `PlanPart` (`type: 'plan'`) and `NhAssistantSseEventMap` gains `plan.updated`. | Code that switches over them exhaustively handles the new values. |
| New translation keys `nh-assistant.plan.*`, `nh-assistant.errors.assistant-plan-invalid`, `nh-assistant.errors.assistant-plan-update-limit-reached` and `nh-assistant.admin.run-config.plan.*`. | An application that replaces the bundled translations adds the keys in each of its languages. |
