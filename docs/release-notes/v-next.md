# v-next

## @newheap/platform-ai-chat: a prepared draft in the composer

`NhAssistantPanelService.open` takes `NhAssistantPanelOpenOptions` next to a conversation id:
`conversationId`, `agentId` (a new conversation with that agent), `draft` and `replaceDraft`. The draft
shows in the composer for the person to review and send and is never sent by itself; it is trimmed and
capped at `maxMessageChars`, and it keeps what the person already typed unless `replaceDraft` is set.
`NhAssistantStore.setDraft(text, { replace })` does the same for a panel in a page, and
`pendingDraft()` (`NhAssistantComposerDraft`) feeds the composer's `restore` input, which now also takes
a prepared draft. `restoredDraft()` keeps naming only a message the server refused.
