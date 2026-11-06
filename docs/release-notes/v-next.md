# v-next

## @newheap/nh-toastr: an action button inside a toast

- `NhToastrConfig.action` (`NhToastrAction`: `label`, `value`, `dismiss`) renders a real `<button type="button">`
  in the toast, for example "Undo". Pass it per toast through the `override` parameter. Clicking it emits `value` on
  the toast's `onAction` through the new `NhToastrService.runAction(toastId)` and dismisses the toast unless
  `dismiss` is `false`; the click never counts as a tap, so `onTap` and `tapToDismiss` are not involved.
- The timeout now also pauses while keyboard focus is inside a toast, not only while the pointer is over it, and
  resumes once neither is the case, so keyboard users can reach the toast's buttons.
- Theme the button with the `--nh-toastr-action-*` custom properties and `--nh-toastr-actions-justify` /
  `--nh-toastr-actions-margin-top`; its focus-visible outline uses `--nh-toastr-focus-color`.
- `NhToastrComponent` gains the `actionTriggered` output, which `NhToastrContainerComponent` connects to `runAction`.
  Existing toasts without `action` render as before.
