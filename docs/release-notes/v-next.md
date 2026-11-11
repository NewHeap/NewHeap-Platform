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

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet, NewHeap.Platform.AI.Chat.SqlServer and NewHeap.Platform.AI.Chat.PostgreSql: MCP elicitation wait time

An MCP server's request for input waits the server's own wait time for the person of a chat turn
(`elicitationWaitMinutes`, `NhAssistantMcpServerRequest.ElicitationWaitMinutes`, 1 to 10 minutes) while
the clock of the tool's own timeout stands still; the turn's own timeout keeps running.

| Breaking change | Required action |
|---|---|
| The `McpServerElicitationWait` migrations add the nullable column `ElicitationWaitMinutes` to `AssistantMcpServer` on SQL Server and PostgreSQL. | Apply the assistant migrations for your provider, or keep `RunMigrations` on. |
| A request for input now waits up to five minutes by default instead of ending at the tool's timeout (30 seconds for an imported tool), and the call keeps its tool's concurrency slot meanwhile. | Set a shorter `elicitationWaitMinutes` for a server whose tools many people call at once; raise `NhAssistantLimits.TurnTimeout` where people should get the full wait. |

## @newheap/platform-ai-chat: MCP elicitation wait time

| Breaking change | Required action |
|---|---|
| New translation keys `nh-assistant.admin.server-editor.elicitation-wait`, `nh-assistant.admin.server-editor.elicitation-wait-hint` and `nh-assistant.admin.server-editor.elicitation-wait-invalid`. | An application that replaces the bundled translations adds the keys in each of its languages. |

## NewHeap.Platform.AspNet.Common: background operation hub session check

| Breaking change | Required action |
|---|---|
| `NhBackgroundOperationHub` now validates every open connection's session each `NhBackgroundOperationsOptions.HubSessionCheckInterval` (one minute, on the application's clock) with the registered `INhAuthenticationSessionValidator` and closes the connection of a deleted user or one whose security stamp changed, instead of sending it operation changes until its token expires (`HubSessionCheckEnabled`, on by default). | None with NewHeap Identity; expect one validator call per open connection per interval. Set `HubSessionCheckEnabled = false` to keep the previous behavior, or tune `HubSessionCheckInterval` (positive, at most a day). |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: hub session check clock

| Breaking change | Required action |
|---|---|
| The assistant hub's one-minute session check now runs on the application's clock (a registered `TimeProvider`), as the background operation hub's does. | None with the system clock; a host that registers a test clock moves it to trigger the check. |
