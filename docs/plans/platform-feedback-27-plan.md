# Platform Feedback on 27.0.0 Plan

A consuming application that lets other applications talk to its assistant over MCP reported four points on NuGet
27.0.0: a caller cannot wait for a turn it did not start, a chat profile has no reasoning setting, nothing reports
progress during a turn, and the turn log says too little to explain a slow turn. Lars also asked what makes a turn
slow and to include the fixes. Each finding was checked against `main` at a28bb2ac (NuGet 27.0.0,
`@newheap/platform-ai-chat` 0.26.0). This plan records one choice per finding. It is a plan, not usage documentation:
once it is built, the guidance rules, the release notes and the sample cases are authoritative.

## Status

Decided by Lars on 2026-10-30: FB67 to FB71, every one as advised (option (a)). Built the same day in one branchless
change on `main`, since the five points touch the same turn path.

## Decisions

| # | Decision |
|---|---|
| FB67 | **`INhAssistantConversationTurns.WaitAsync`.** It waits for the turn of a chat, up to a bound the caller names, without the caller polling. |
| FB68 | **`NhAiModelProfileBuilder.WithReasoning(effort, output)`.** The executor applies it to every governed call of that profile. A caller's own `ChatOptions.Reasoning` wins. |
| FB69 | **`Progress` on `NhAssistantConversationTurnOptions` and `NhAssistantTurnWait`**, an `IProgress<NhAssistantTurnProgress>` with what the turn does: started, text, tool started, tool completed, ended. |
| FB70 | **`NhAssistantTurnTimings`** in the turn log and on `NhAssistantConversationTurnResult`: total, preparation with its MCP, retrieval and file parts, model time and calls, tool time and time to the first text. |
| FB71 | **MCP servers connect at the same time, and an unreachable server is remembered** for `NhAssistantMcpOptions.UnreachableRetryDelay`, 30 seconds by default. |

## Rules for all of it

- Every change is opt-in or compatible unless its choice says otherwise. A breaking change gets a
  `Breaking change | Required action` row.
- Every change follows the repository rules:
  - tests with authorization negatives;
  - a release-notes section per release unit;
  - guidance and consumer guide updates;
  - an executable sample case, extending the existing case where one covers the surface;
  - the public API snapshot for a public change.
- Nothing names the consuming application.

## What makes a turn slow

A throwaway test ran real turns through `INhAssistantConversationTurns` on PostgreSQL, with a model that answers at
once, so everything measured is the library's own work. Each turn made three model calls and two tool calls.

| Measured | Result |
|---|---|
| A warm turn | 44 to 85 ms in total |
| From the call to the first model call | 22 to 58 ms |
| SQL | 42 commands per turn, 22 to 34 ms: 21 before the first model call, 14 during, 7 after |
| After 82 stored messages | no growth: the history and the conversation read are bounded |
| First turn after a host start | about 950 ms in a Debug build, once: query compilation and JIT |

So the library's own preparation and storage cost milliseconds next to a model that takes seconds. No SQL change is
worth its risk. The time goes to what the turn waits for, and three things stand out in the code:

1. **MCP servers connect one after the other.** `NhAssistantMcpToolSource.GetToolsAsync` awaits each server in turn.
   A connection that is not cached costs a TLS handshake, `initialize` and `tools/list`, and connections that forward
   a caller's token are never cached, so those costs add up on every turn.
2. **An unreachable server costs every turn its whole timeout.** Connecting and listing are each bounded by
   `ConnectTimeout`, 10 seconds by default, and a failure is not remembered. One server that is down delays every
   turn of every agent that uses it by up to 10 seconds before the model is called, while the person sees nothing.
3. **Reasoning effort has no home.** A model that thinks at its default effort is the largest controllable cost of a
   short answer, which is finding FB68.

What was checked and left alone:

- **Server-sent events** already disable buffering and flush per batch.
- **The prompt** already keeps stable instructions before turn data, which keeps a provider's prompt cache useful.
- **The title** uses the first line by default and needs no model call.
- **Live publishing per delta** is awaited, but the measured turn showed no cost and a queue would move the audience
  check off the turn's flow, next to its tools. Not worth that risk without a measurement that shows a cost.
- **Tool calls of one model response** run one after the other. Running them together would need the interceptor's
  order, budget and approval logic to change, and order matters for what a person sees. A script already chains reads.
- **Files of a turn** open one after the other. They only cost time in chats with files, and FB70 now shows it.

## Choices

Each choice lists the options that were weighed. "(advice)" marks the recommendation.

### FB67. Waiting for the end of a turn

`SendAsync`, `DecideAsync` and `AnswerAsync` already return when the turn ends, but only to the caller that started
it. A channel that gives up after its own time limit, such as an MCP tool with a client timeout, keeps the turn
running with `KeepTurnAfterCallerLeaves`, and every later read then has to poll `GetAsync`. The turn's events are
internal, so nothing else can observe them.

- **(a) `WaitAsync` on `INhAssistantConversationTurns` (advice).** The same access rules as a turn, a bound the caller
  names, and the result of `SendAsync`, with the new status `running` when the bound passes first. On the host of the
  turn the end arrives as an event; for a turn on another host the wait reads the conversation at a short interval,
  inside the library.
- (b) Public live events through a new interface. An application would have to build the wait, the access check and
  the case of another host itself.
- (c) A stored turn outcome with a new table. It would make the status of an earlier turn exact, but needs a migration
  on both providers for a case the conversation's status already answers well enough.

#### Limit of the choice

A turn that ended before `WaitAsync` was called is read from the conversation, which keeps no outcome per turn: a
turn that was cancelled reads as `completed`, and a failed turn has no code.

### FB68. Reasoning per chat profile

`NhAiModelProfileBuilder` has no reasoning setting, so every application wraps its client to set
`ChatOptions.Reasoning`.

- **(a) `WithReasoning(ReasoningEffort effort, ReasoningOutput? output = null)`, applied by `NhAiChatExecutor`
  (advice).** Every governed call passes there, and the executor knows which profile of a fallback chain answered, so
  each profile keeps its own setting. A caller's own `ChatOptions.Reasoning` wins.
- (b) A decorating client from `ResolveChatAsync`. It would also reach callers that use the client directly, but it
  hides the setting from the executor and adds a layer to every profile.

### FB69. Progress during a turn

The endpoint's stream carries what happens, but `INhAssistantConversationTurns` shows nothing until the turn ended.

- **(a) An `IProgress<NhAssistantTurnProgress>` on the options of a turn and of a wait (advice).** It carries the kind,
  the text delta and the tool's id, name and outcome. It carries no arguments or results, so a channel can pass it on
  without a new data decision. A failing handler is logged and never stops the turn.
- (b) An `IAsyncEnumerable` turn stream instead of the result. It replaces the simple call that channels use today.
- (c) MCP progress notifications inside the library. The channel owns its MCP tool, so it owns what it says there; the
  sample shows the mapping.

### FB70. Timings per step

The turn log has the number of tool calls and nothing about time. `NhAiUsageRecord` already holds the duration of every
model call.

- **(a) `NhAssistantTurnTimings`, in the log line and on the turn's result (advice).** A usage sink adds up the model
  calls of the turn, so the model layer does not change. The log stays content-free.
- (b) Per-step activities only. They need a trace backend, while a log line is there in every deployment.
- (c) Columns on the message. Useful for reports, but it needs a migration and nobody asked for history.

### FB71. MCP connections

- **(a) Connect at the same time, keep the agent's order, and remember an unreachable server (advice).**
  - Planning a server stays sequential, since it reads the store. Connecting runs together, and the tools are added
    in the agent's order, so the list a model sees stays stable, which keeps its prompt cache useful.
  - The credential source serializes calls to a provider within one scope, because an application's provider may use
    a scoped `DbContext`.
  - An unreachable server is remembered for `UnreachableRetryDelay`, so one turn pays the timeout instead of every
    turn. It counts only for `mcp-unreachable`, never for a refused credential, which belongs to one person. A change
    to the server, an administrator's test and zero all ignore it.
- (b) Only a shorter default `ConnectTimeout`. A slow server would fail where it works today.
- (c) A background refresh of tool lists. It removes the wait after the cache expires as well, but it needs a lifetime
  of its own and a rule for tools that changed, while (a) already removes the worst cases.
