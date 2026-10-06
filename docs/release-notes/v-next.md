# v-next

## @newheap/platform-ai-chat: conversation controls and prompt bar

The assistant picker now sits inside the message composer. Separate sharing and
member controls sit above it, and configured notifications can be toggled from
the panel header. The panel uses quieter surfaces and spacing in light and dark
themes. Standalone pickers and sharing components retain their default behavior;
the new `compact` and `section` inputs allow consumers to opt into these layouts.

| Breaking change | Required action |
| --- | --- |
| The panel's header picker and page-context chip have moved into composer controls and an icon. | Update custom CSS that targets the old header picker or `.context-chip`; the page-context icon retains the include/exclude action. |
