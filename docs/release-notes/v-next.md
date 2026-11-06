# v-next

## @newheap/platform-common: context menu

`NhContextMenuService` renders item titles as text, opens a WAI-ARIA menu that
works with the keyboard and returns focus when it closes, and keeps the menu
inside the viewport of a scrolled page. `NhContextMenuItem` gains `iconClass`,
`disabled` and `checked`, and `NhContextMenu.fromElement` anchors a menu to the
button that opens it. A rejected `onClick` closes the menu and reaches the
`ErrorHandler`.

| Breaking change | Required action |
| --- | --- |
| `NhContextMenuItem.title` is rendered as text, no longer as HTML. | Remove markup from titles; put icons in `iconClass` (rendered as `<i class="..." aria-hidden="true">`). |
| The menu uses `position: fixed` with `x`/`y` as viewport coordinates. | Pass `clientX`/`clientY` (or `NhContextMenu.fromEvent`), not `pageX`/`pageY` or coordinates with the scroll offset added. |
| Focus moves into the menu on open and returns to the previously focused element on close; a pointer down outside closes it instead of a click. | Do not move focus from code right after `open`; drop a `stopPropagation` that only kept a menu opened from a click handler open. |
