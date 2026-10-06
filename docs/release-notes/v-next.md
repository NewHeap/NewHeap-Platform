# v-next

## @newheap/platform-ai-chat: queued follow-ups and steering

The panel now queues messages submitted while a turn runs or an approval waits.
Queued messages can be edited, removed or prioritized with **Steer now**, which
cancels the active turn before sending the correction in the same conversation.
Stop pauses the queue; failures preserve unsent messages for explicit retry.
Standalone composers keep their default behavior; custom queue layouts use
`NhAssistantStore.submit`, `canSubmit` and `[queueWhileBusy]="true"`.

| Breaking change | Required action |
| --- | --- |
| Enter in the panel now queues follow-ups while busy, and the sending-unavailable notice is removed. | Use Stop to pause dispatch, Resume queue to continue, and the queue controls to edit, remove or prioritize unsent messages. Queues are local to the current tab and do not survive a reload. |
