# v-next

## @newheap/platform-ai-chat: Markdown libraries load on first use

`marked` and `dompurify` now load on the first Markdown the assistant renders instead of with the
application, which takes about 76 KiB out of its initial bundle. Until they arrive, the text shows
escaped, as it already did during server rendering. Diagrams in `nh-assistant-document` wait for
DOMPurify before they are sanitized and drawn.

| Breaking change | Required action |
|---|---|
| Assistant Markdown renders after `marked` and `dompurify` have loaded, not during the first change detection | In tests, `await fixture.whenStable()` and call `fixture.detectChanges()` again before asserting rendered Markdown. |
