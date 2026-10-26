# v-next

## NewHeap.Platform.AI.Chat.AspNet: request delegates generated at build time

The library builds with ASP.NET Core's Request Delegate Generator (`EnableRequestDelegateGenerator`), so
the request delegates of the assistant's 139 minimal API endpoints are written when the package is built.
A host that calls `MapNewHeapAssistant` no longer compiles them with `RequestDelegateFactory` while it
starts. Applications change nothing.

Measured with `AssistantRequestDelegateTests.Measure_mapping_and_building_the_assistant_endpoints`
(`NH_MEASURE_ENDPOINTS=1`, Release, Windows 11, Intel Core Ultra 9 275HX, .NET 10.0.12): the time of
`MapNewHeapAssistant` plus the first read of every endpoint, for 20 hosts in one process after three
warm-up hosts, and for the first host of the process. The ranges cover four runs before and five after;
the first host was recorded in two and four of them.

| Per host | Before | After |
|---|---:|---:|
| Wall-clock time, median | 225–231 ms | 6.5–7.0 ms |
| Process CPU time, mean | 248–270 ms | 6–11 ms |
| First host of the process, wall-clock time | 270–271 ms | 91–95 ms |

Routes, endpoint names, summaries, `Produces` metadata, tags, filters, authorization, the binding of
route and query values (a malformed value still answers 400) and the 404 of a disabled assistant are
unchanged; the sample's OpenAPI document is the same. The endpoints' metadata now also holds the
generator's `GeneratedCodeAttribute`, and the generator's `IParameterBindingMetadata` reports
`HasTryParse` false for `string` parameters, which need no parsing.

| Breaking change | Required action |
|---|---|
| None. | None. |

## NewHeap.Platform.AspNet.Common: background-operation errors during a cancellation and the hub path

The runner's failure path now checks the platform's stop signals before it records an error that a
handler threw outside a step, as the recording of a failed step already does:

- While the attempt's cancellation is requested, the error no longer fails the operation. It ends
  `Cancelled` (`cancelled`, `background-operation.cancelled`), or `TimedOut` (`soft-timeout`) when
  the soft time-out fired first, and nothing retries the error, which is logged as a warning with the
  operation and attempt. This covers a client library that reports a cancelled call as an error of
  its own as well as an unrelated error after the owner cancelled.
- A lost fence and contention for the operation lock that the heartbeat recorded still win: the
  stopped attempt records nothing, or the operation is rescheduled as busy without spending a retry.
- Without such a signal the error fails the attempt as `handler-failed` with the operation's retry
  rules, as before.

`NhBackgroundOperationsOptions.HubPath` no longer has to lie below `/hub`. Since the query token
follows the configured hub paths, any absolute application path below the root works, such as
`/hubs/background-operations`, except a path on the operations' own API under
`/background-operations`. The default stays `/hub/background-operations`.

| Breaking change | Required action |
|---|---|
| An error that a handler throws outside a step while its cancellation is requested ends the operation `Cancelled`, or `TimedOut` after a fired soft time-out, without a retry, instead of `handler-failed` with a scheduled retry or a terminal failure; it is logged as a warning without a diagnostic correlation id. | None, unless monitoring, a notification policy or a test expected `handler-failed` for such an error; expect `Cancelled`. |
| An error that a handler throws outside a step after the heartbeat stopped the attempt for lock contention reschedules the operation as `concurrency-busy` without spending a retry, instead of recording `handler-failed`. | None. |
| None for `HubPath`: a path outside `/hub` is now accepted, while a relative, empty or root path and a path under `/background-operations` are still refused. | None. A host that moves the hub points the Angular client's `hubUrlSuffix` at the same path. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: MCP assignments one by one and tools reviewed before a sync

`PUT admin/agents/{id}` now treats an omitted `mcpServerIds` as it treats the other optional fields: the
agent keeps its MCP assignments. `[]` still removes them all, a list still replaces them, and a new agent
without the list still has none. An editor that read an agent before an assignment therefore no longer
drops it when it saves.

The new `PUT` and `DELETE admin/mcp-servers/{id}/agents/{agentId}` assign a server to one agent or remove
the assignment through `INhAssistantMcpServers.AssignAsync` and `UnassignAsync`, behind the admin policy:
only the assignment changes, never the agent's definition or version; a call that changes nothing is not
audited; they answer the server with its `assignedAgentIds`, `404` for an unknown server or agent and
`400` with `mcpServerIds: too-long` for an agent with 32 servers. Each change is audited as
`AdminMcpServerAssignmentChanged`.

`INhAssistantMcpServers.UpdateToolAsync` and `PUT admin/mcp-servers/{id}/tools/{remoteName}` also take a
tool that no sync has seen yet, such as a tool of a server the host cannot reach yet. They store its
reviewed settings (enabled, effect, description) as a tool with the status `missing`, no schema and the new
`NhAssistantMcpToolDto.AwaitingSync`, which no turn offers. Its name has at most 128 characters
(`remoteName: too-long`), no control characters and no other spelling of a stored tool's name
(`remoteName: invalid`); a server holds at most `NhAssistantMcpServerLimits.MaxToolsAwaitingSync` (100)
of them (`409 assistant-configuration-limit`). Such a tool can be enabled while it waits; a tool the server
stopped listing still cannot (`assistant-mcp-tool-disabled`). The first sync that sees the tool, also in
the server's spelling when only the case differs, makes it `available` with these settings and the schema
it saw; a later change of the schema disables it as for any tool, and a sync that does not see it leaves it
waiting. New audit kinds: `AdminMcpToolReviewedBeforeSync` (113) for the stored settings and
`AdminMcpToolSeenAfterReview` (114) for the first sync that saw the tool, each with `ObjectId`
`<server>/<remote name>` and `ResultCode` `enabled` or `disabled`. No migration: the row uses the existing
columns.

| Breaking change | Required action |
|---|---|
| `PUT admin/agents/{id}` without `mcpServerIds` keeps the agent's MCP assignments instead of removing them all. | A client that removed every assignment by leaving the list out sends `mcpServerIds: []`. |
| `PUT admin/mcp-servers/{id}/tools/{remoteName}` and `UpdateToolAsync` store a remote name that no sync has seen as a tool awaiting its sync instead of failing with `404 assistant-mcp-tool-not-found`, and a tool of an unknown server fails with `assistant-mcp-server-not-found`. | A client that relied on `assistant-mcp-tool-not-found` for an unknown name reads `awaitingSync` of the answer or lists the tools first. |
| None for the assignment endpoints, `AwaitingSync`, `MaxToolsAwaitingSync` and the audit kinds 113 and 114. | None; an audit sink that maps every kind adds the two new ones. |

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
