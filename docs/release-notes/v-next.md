# v-next

## @newheap/platform-ai-chat: a prepared draft in the composer

`NhAssistantPanelService.open` takes `NhAssistantPanelOpenOptions` next to a conversation id:
`conversationId`, `agentId` (a new conversation with that agent), `draft` and `replaceDraft`. The draft
shows in the composer for the person to review and send and is never sent by itself; it is trimmed and
capped at `maxMessageChars`, and it keeps what the person already typed unless `replaceDraft` is set.
`NhAssistantStore.setDraft(text, { replace })` does the same for a panel in a page, and
`pendingDraft()` (`NhAssistantComposerDraft`) feeds the composer's `restore` input, which now also takes
a prepared draft. `restoredDraft()` keeps naming only a message the server refused.

## NewHeap.Platform.AspNet.Common: division lockouts in whole UTC days

`NhUserManager.GetValidClaimsByUserIdAsync` decides whether a division membership is locked out by
comparing its `LockOutStartDateTime` and `LockOutEndDateTime` with the start of today and of
tomorrow in UTC, instead of reading their `.Value.Date`. A membership stays locked out from its start
day through its end day, or indefinitely without an end date. The predicate no longer reads the value
of an empty date, so a user without an active division or memberships gets the same claims on
EF Core InMemory as on SQL Server and PostgreSQL. The day follows the application's clock: a
registered `TimeProvider`, otherwise the system clock.

| Breaking change | Required action |
|---|---|
| A lockout date stored with a non-zero offset counts in the UTC day of its instant at the day edges; SQL Server's `CONVERT(date, …)` used the day of the stored offset before, PostgreSQL already stored UTC. | None, unless an application stores lockout dates with a local offset and expects that offset's day; store them in UTC for the day they mean. |
| A registered `TimeProvider` decides the current day of division lockouts. | None, unless a host registers a clock that differs from real time outside tests. |
