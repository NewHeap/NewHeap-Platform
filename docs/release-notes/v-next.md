# v-next

## @newheap/platform-ai-chat: the host's own empty state

`nh-assistant-panel` places the host's content with `nhAssistantEmptyState` in place of its own empty
state ("How can I help?") while the conversation has no messages, in a page and in the drawer, for
example the host's own words and opening questions that call `NhAssistantStore.setDraft`. Without it
the panel's own empty state shows as before.

## @newheap/platform-ai-chat: the composer empties after a fast send

A message typed and sent between two renders, as fast automated typing does, went out while its text
stayed in the composer, where the person could send it again. The composer now empties, and a draft
that replaces text typed since the last render shows as well.
