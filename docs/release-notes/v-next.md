# v-next

## @newheap/platform-ai-chat: page screenshots

| Breaking change | Required action |
| --- | --- |
| The chat package adds the `html2canvas@^1.4.1` peer for lazy page capture. | A standard npm install resolves this peer automatically; no separate install is needed. To enable this feature, set `screenshots: true` in the chat configuration for agents accepting PNG uploads. Users can initiate sharing, or the AI can request it. Each capture requires explicit user consent and preview confirmation. |

## NewHeap.Platform.AI.Chat, NewHeap.Platform.AI.Chat.AspNet: page screenshot requests

The built-in `request_page_screenshot` tool asks the sending browser to open its consent dialog with a reason. It is offered only when the message advertises `pageScreenshotAvailable` and the agent accepts PNG uploads. The transient `page-screenshot.requested` SSE event grants no consent and is not broadcast to other participants or replayed from history. The model receives an image only when the user confirms the preview and sends the attachment in a new message.
