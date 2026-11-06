# v-next

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: alerts wait for a person's fresh access context

- In a host whose principals come from the application's access source (`UsePrincipalsFromAccessSource`), a
  recipient who lacks the read role while the application holds no fresh context of their rights is no
  longer dropped with `no-access`. The raise plans the person, the delivery stays pending and a later
  maintenance pass checks the person again, about every five minutes. It goes out once the person is back,
  is taken back as expired when the alert expires first, or is dropped with the new reason
  `NhAssistantAlertDropReasons.NoFreshContext` (`no-fresh-context`) once `NhAssistantLimits.AlertFreshContextWait`
  (24 hours by default, from one minute to 30 days) passed since it fell due. A person whose fresh rights give
  no read role is still dropped at once with `no-access`. A waiting delivery carries the reason code
  `assistant-alert-awaiting-fresh-context`, keeps the moment it fell due and counts no delivery attempt.

| Breaking change | Required action |
|---|---|
| An alert recipient without a fresh access context is no longer dropped with `no-access`; the delivery waits and is dropped with `no-fresh-context` after `NhAssistantLimits.AlertFreshContextWait`. | None. Set `AlertFreshContextWait` to change the wait, and expect `no-fresh-context` where you read drop reasons or the `AgentAlertRecipientDropped` audit. |

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: one maintenance pass on demand

- `INhAssistantMaintenance.RunOnceAsync`, registered by `AddNewHeapAssistant`, runs one pass of the run
  maintenance now: proposal expiry, reminders and escalations, executions and overdue reports, and in a host
  with alert kinds alert expiry, purging and delivery. It is the hosted maintenance's own instance, whose
  passes no longer overlap, so when it returns a full pass ran after the call. Tests move the application's
  clock and run a pass instead of waiting for `NhAssistantLimits.MaintenanceInterval`.

## NewHeap.Platform.AI.Chat and NewHeap.Platform.AI.Chat.AspNet: boards count the work of agents that work on objects

- On `GET boards/{group}`, an agent that works only on objects now shows `openProposals` and `latestRun` for
  its threads at the objects the person reads by the member snapshot, in the person's tenant and, on a
  division's board, in that division; before, they were `0` and `null`. `isNew` follows them.
- `NhAssistantAlertKindPreferenceDto.Channels` documents that it is the person's choice whether or not the
  host offers the channel; a client names only the channels that are also in `AvailableChannels`.

## @newheap/platform-ai-chat: the alert channel hint names only offered channels

- The notification preferences' hint of an alert kind the person may not choose no longer names Web Push, or
  in-app notifications, when the server does not offer that channel (`availableChannels`).
