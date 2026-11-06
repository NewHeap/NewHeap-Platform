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

Fixed in the same release, without required action:

- `NhModalService.confirm` and `confirmDialog` now run the passed `onConfirm` on "Yes" (it may return a promise), show its error in the dialog and close the dialog after success; "No" closes the dialog, and `onClose` still runs once when it closes. `NhModalConfirmComponent` emits `confirmed` after `onConfirm` completed without an error, and ignores clicks while its confirm or cancel delegate runs.
- A modal that a route change destroys with its hosting page now leaves `NhModalService.modalRefs`, so `modalRefsOpen` reports `false` once no modal is open, and it no longer repeats `false` for one closing modal.

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

## @newheap/nh-toastr: an action button inside a toast

- `NhToastrConfig.action` (`NhToastrAction`: `label`, `value`, `dismiss`) renders a real `<button type="button">`
  in the toast, for example "Undo". Pass it per toast through the `override` parameter. Clicking it emits `value` on
  the toast's `onAction` through the new `NhToastrService.runAction(toastId)` and dismisses the toast unless
  `dismiss` is `false`; the click never counts as a tap, so `onTap` and `tapToDismiss` are not involved.
- The timeout now also pauses while keyboard focus is inside a toast, not only while the pointer is over it, and
  resumes once neither is the case, so keyboard users can reach the toast's buttons. Switching to another window
  while a toast button has focus keeps that toast paused until focus moves elsewhere on the page.
- Theme the button with the `--nh-toastr-action-*` custom properties and `--nh-toastr-actions-justify` /
  `--nh-toastr-actions-margin-top`; its focus-visible outline uses `--nh-toastr-focus-color`.
- `NhToastrComponent` gains the `actionTriggered` output, which `NhToastrContainerComponent` connects to `runAction`.
  Existing toasts without `action` render as before.

## @newheap/platform-common: form dropdown values and custom form controls

`nh-form-dropdown` keeps a value that the form sets. An external `setValue` no longer reverts, or
flips between the old and the new value on every change detection, when the selected option resolves
synchronously: the dropdown takes a new value only from a user selection, check all or uncheck all.
Single-select keeps the options `0` and `false`. Lazy options keep their `image`, and the new optional
`NhFormDropDownSettings.classesGetLambda` sets the CSS classes of an option in every loading mode. Loaded
options render inside an `OnPush` parent, Escape closes an open list and returns focus to its toggle
button, and the unused stylesheet whose `app-form-dropdown` selector never matched is removed, so
layouts do not change.

| Breaking change | Required action |
|---|---|
| `AbstractValueAccessor.writeValue` no longer calls `onChange`, so `setValue`, `reset` and an `ngModel` binding no longer emit a second `valueChanges`, mark the control dirty or emit `ngModelChange` at initialization; this includes `nh-form-dropdown` and `nh-page-size`. | Remove workarounds for the echo, such as skipping the first change or resetting the dirty state, and start initial work from initialization instead of the first `ngModelChange`. |
| `nh-form-dropdown` emits `closed` after the chosen value with `closeOnSelect`, and `toggleDropdown()` called from a click outside the dropdown opens the list once that click has finished, instead of opening and closing at once. | Read the chosen value in `closed`, and handle `opened` instead of acting right after such a `toggleDropdown()` call. |
