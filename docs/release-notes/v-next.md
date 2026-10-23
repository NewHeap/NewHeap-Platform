# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: one notification per person and run

Everything of one run that waits for a person's decision, in the run's own thread and at the objects it
placed proposals at, now reaches that person as one notification, and the run maintenance bundles the
reminders and escalations of one pass per run and person. `INhAssistantAgentNotificationPolicy` is still
asked per object (`RunEndedWithProposals`) or per proposal (`ProposalExpiring`, `ProposalEscalated`)
with the context of before, so custom policies keep working; Web Push, the in-app channel and every
`INhAssistantAgentNotificationChannel` then receive one context of a new kind per group of recipients
with the same objects, whose `Objects` name only what waits for them. A click opens the run's own
thread for its initiator and otherwise the object with the most proposals; the payload also carries the
run, which `@newheap/platform-ai-chat` hands to the application. Proposals a consultation leaves at its
subject arrive the same way, without a run.

| Breaking change | Required action |
|---|---|
| The proposals of a run reach Web Push and the channels as `NhAssistantAgentNotificationKind.RunProposalsWaiting`, `RunProposalsExpiring` and `RunProposalsEscalated` (8 to 10), one per group of recipients with the same objects; `RunEndedWithProposals`, `ProposalExpiring` and `ProposalEscalated` no longer reach them. | Handle the new kinds in a custom `INhAssistantAgentNotificationChannel` and its exhaustive switches; keep handling the old kinds in a custom notification policy. |
| `NhAssistantAgentNotificationContext` has `Objects` (`NhAssistantAgentNotificationObject`: subject, display name, conversation and proposal count); for the new kinds `ConversationId`, `Subject` and `SubjectDisplayName` name the object a click opens, `ProposalCount` counts all objects, `ApprovalId` and `ToolId` are set only for exactly one proposal and `ExpiresAt` is the earliest expiry. | Read the objects of a notification from `Objects` instead of one subject per notification. |
| Notifications about several objects read "Assistant" ("Assistent") and "N proposals at M projects wait for your decision", with the subject type's plural name in the recipient's language, or "objects" when the types differ or have no plural; reminders and escalations of several proposals are counted the same way. | Give subject types a plural with `NhAssistantSubjectTypeBuilder.NamedPlural`. |
| The Web Push payload of the new kinds carries `runId` and the tag `nh-assistant-run:<runId>`, its kinds are `agent-run-proposals`, `agent-run-proposals-expiring` and `agent-run-proposals-escalated`, and a newer notification about the same run replaces an undelivered older one. | Copy the push worker of `@newheap/platform-ai-chat` into the application's assets again. |
| The in-app message about the proposals of a run groups per run (`assistant-agent:run:<runId>`) instead of per agent thread. | None, unless the application reads `GroupKey`; then expect the run's group. |

## @newheap/platform-ai-chat: open the run of a notification

`NhAssistantPushService.openRun$` emits the run of a clicked notification about the proposals of a run,
so the application opens its inbox of that run, for example `nh-assistant-agent-inbox` with `[runId]`.
The bundled worker forwards the run with the click, and a click that opens a new window does so through
`#nh-assistant-run=<runId>.<conversationId>` (`NH_ASSISTANT_RUN_FRAGMENT`). Without a subscriber a click
opens the conversation in the panel as before.

| Breaking change | Required action |
|---|---|
| While the application subscribes to `NhAssistantPushService.openRun$`, a clicked notification about the proposals of a run emits its run there instead of opening the conversation in the panel. | Subscribe in the application shell and open the inbox of the run; copy `push/nh-assistant-push-worker.js` into the assets again, since only the new worker forwards the run. |
