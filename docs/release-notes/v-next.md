# v-next

## @newheap/platform-common: debounced search fields

- `onDebounce` of `[ngModel][nhDebounce]` and of `nh-search-input` no longer reports the initial `ngModel`
  value. `preDebounce` and `onDebounce` shared one guard that `preDebounce` consumed, so `onDebounce`
  reported the initial value 500 ms after the field appeared and a page loaded its data twice. A value
  typed while the initial value is still within the debounce time is reported.
- A value that still waits for the debounce time when the field is destroyed is no longer reported while
  the field is destroyed.
- `nhButtonDebounce` no longer writes `tap` to the console for every click.
- `nh-search-input` has an accessible name: the new `label` input, by default the translated
  `general.search` text that its placeholder shows. The new optional `inputId` puts an id on the input.
  Its look, `value` and `onDebounce` are unchanged.

| Breaking change | Required action |
|---|---|
| `onDebounce` of `[ngModel][nhDebounce]` and `nh-search-input` no longer reports the initial value. | Load the initial data yourself, for example in the collection's initial load, instead of from the first `onDebounce`. |
| A value that waits for the debounce time is dropped when its field is destroyed. | Save such a value from `preDebounce` or on blur when it must survive closing the field, as in an autosave field. |
| `nh-search-input` sets `aria-label`, which wins over a `<label>` that wraps it or points at it. | Pass that label's text through `[label]`. |

## @newheap/platform-common: modals close on browser back and forward navigation

`NhModalService` now follows the router: a `NavigationStart` with the `popstate` trigger, which the
browser's back and forward buttons cause, dismisses every open modal as Escape does, the topmost
first, so cleanup and the `closed` event run. Content with the new optional
`INhModalComponent.onModalBackNavigation` decides itself; other content with `onModalEscape` gets that
call, and other content closes as the close button does. Mutating and confirmation content call
`onModalEscape`, and therefore `closeDialog`, but stay open while their save or confirmation runs. A
modal opened with `closeable: false` stays open, and navigation started from code keeps every modal
open. `NhModalOptions.closeOnBackNavigation` (default `true`) opts a modal out.

| Breaking change | Required action |
|---|---|
| An open modal closes on browser back or forward navigation instead of staying open over the next page. | Pass `closeOnBackNavigation: false` in `NhModalOptions` for a modal that must survive the navigation, and host it in the root view container (`NhModalService.setViewContainerRef`). |
| A `closeDialog` or `onModalEscape` override of modal content now also runs on browser back or forward navigation. | Override `onModalBackNavigation` when back navigation needs other handling than Escape. |

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
