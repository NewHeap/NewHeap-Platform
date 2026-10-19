# v-next

## @newheap/platform-ai-chat: a failed conversation stays open

A turn that fails, for example because the language model is temporarily unavailable, no
longer closes the conversation in the panel. The server already accepted a new message after
a failed turn; the panel now does too. The error bar keeps the translated failure, the
composer stays available and the next message starts a new turn in the same conversation or
agent thread, so reopening an agent thread about a subject no longer leads back to a thread
that cannot continue. While nothing is queued, an explicit send goes out directly even with
the error bar shown; queued messages still wait for **Resume queue**. A reopened conversation
whose last turn failed shows the new `nh-assistant.panel.last-turn-failed` text, bundled in
English and Dutch. Only an archived conversation is closed. The mock API of
`@newheap/platform-ai-chat/testing` accepts a message after a failed turn like the server.

| Breaking change | Required action |
|---|---|
| `NhAssistantStore.canSend` and `canSubmit` are `true` for a `failed` conversation, and `submit` sends directly while nothing is queued, also when an error is shown. | Gate a custom composer on `canSend` or `canSubmit`; treat only `archived` as closed. |
| The mock API answers a message in a `failed` conversation with a new turn instead of `409`. | Expect a new turn after a scripted `fail` step in your tests. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Test: one retry after a transient provider error

A provider error inside the model stream is not covered by the provider's HTTP retry. When a
transient one (`server_is_overloaded`, `server_error` or `rate_limit_exceeded`) arrives before
the model wrote text or called a tool, the turn now waits two seconds and calls the model once
more, and logs that it did. A repeated or other provider error, or one after text or a tool
call, still ends the turn as `failed` with `assistant-model-unavailable`; the conversation
accepts the next message. `NhAiScriptedChatClient.RespondWithProviderError(errorCode, text)`
scripts such an error for tests.

## NewHeap.Platform.AI.Chat: live agent work for the person who starts a run by hand

`AgentWorkChanged` reached the thread's participants and the subject's member snapshot only. A
person who started an agent by hand (`POST threads/start` or `INhAssistantAgentTriggers.StartAsync`)
was neither until they opened the thread, and a thread in which only such starts and runs without
proposals had worked had no member snapshot at all. The run's start and end then reached nobody,
so the subject's suggestions and run timeline stayed on queued until a reload. Now the first
update of a thread without a member snapshot fills it through the subject access resolver, as the
first notification already did, and an update that names a run, about the run or one of its
proposals, also reaches the person who started that run while the resolver still gives them a role
on the subject, also when no member list names them.
A failing refresh or resolver call never stops the update. Shadow threads still reach only their
own audience.

| Adoption note | Required action |
|---|---|
| The first agent work update of a thread without a member snapshot calls `INhAiSubjectAccessResolver.ListMembersAsync`, and an update about a run that a person started calls `ResolveAsync` for that person, inside background operations as well. | Keep both resolver methods free of request-only state, as agent notifications already require. |
| A run started by hand now reports its start and end through `AgentWorkChanged`, also when it proposes nothing. | Reload on the update; keep polling such a run only as a fallback for a missing hub connection. |
| No migration, no configuration and no public API change. | None. |
