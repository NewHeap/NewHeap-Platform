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

## NewHeap.Platform.AI.Common, NewHeap.Platform.AI.AspNet.Common, NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: a channel's chats, the turn's person, question notices and expired waits

An application's own channel, such as a messaging app, now does everything a person does in the
client without calling the assistant's endpoints. `INhAssistantConversations` (`CreateAsync`,
`GetAsync`, `CancelAsync` with `NhAssistantNewConversation` and `NhAssistantConversationOptions`)
creates, reads and stops a person's chat with the checks and codes of `POST conversations`,
`GET conversations/{id}` and `POST conversations/{id}/cancel`, and
`INhAssistantConversationTurns.AnswerAsync` (`NhAssistantConversationAnswer`) answers the question
the chat waits for in the order of the answer endpoint. `NhAssistantConversationTurnResult.PendingQuestion`
carries that question. Like the turns, the services serve chats only and run for the person the
application vouches for, without the caller's request.

Every assistant turn names its conversation and the channel that started it on
`NhAiInvocationContext.ConversationId` and `NhAiInvocationContext.Channel`, which turn context
providers, tool discovery, retrieval and every tool call receive: the chat, the agent's thread or the
consulted agent's thread, and the channel of `NhAssistantConversationTurnOptions` (also in the
consultations such a turn starts), `null` for the endpoints' turns and runs. Neither enters a proposal
hash, approval evidence or an audit record. The new `INhAiTurnPerson`, a singleton that
`AddNewHeapPlatformAIAspNet` and `AddNewHeapAssistant` register, gives the person of the current turn
(`User`, `ActorId`, `TenantId`): the request's user in a turn of the endpoints, the person the
application vouched for in a channel's turn or a turn that outlived its request, and nothing outside a
person's turn.

A turn that waits for a question keeps `waiting-for-approval`. Web Push now tells the question's
addressee with a notice of its own (`question-waiting`, English and Dutch texts, high urgency) and
the person whose turn asked nothing when it asks someone else; approvals notify as before. A new
message in a chat whose question or inline approval passed its lifetime on the application's clock
closes it as a late answer or decision does, with the same codes (`assistant-question-expired`,
`assistant-approval-expired`) and audit (`QuestionExpired`, `ApprovalExpired`), releases the waiting
turn and then starts its own turn; a still valid one keeps the chat busy, and reads write nothing.
Agent threads keep their own rules. A question's expiry that a channel's answer or message finds now
records the channel in the audit.

| Breaking change | Required action |
|---|---|
| `INhAssistantConversationTurns` gained `AnswerAsync`. | An application's own implementation, such as a test double, implements `AnswerAsync`; callers change nothing. |
| A message in a chat whose question or approval passed its lifetime is no longer refused with `409 assistant-conversation-busy`: what the chat waited for closes as expired and the message starts its turn. | None, unless a client or test expected the busy refusal after the wait ended; expect the new turn, and the expiry codes on the closed question or approval. |
| A waiting question sends the Web Push kind `question-waiting` to its addressee instead of `approval-required` to the person whose turn asked it. | None for the library's push worker, which shows any kind. A service worker or client of its own that branches on the kind handles `question-waiting`. |
| None for `NhAiInvocationContext.ConversationId`, `NhAiInvocationContext.Channel`, `INhAiTurnPerson`, `INhAssistantConversations` and `NhAssistantConversationTurnResult.PendingQuestion`: they are additive. | None. A tool, policy or turn context provider that read the person from the request reads `INhAiTurnPerson` instead, so it also knows the person in a channel's turn. |

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
