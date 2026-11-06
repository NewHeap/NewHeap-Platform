# v-next

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
