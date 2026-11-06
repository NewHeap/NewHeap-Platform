# v-next

## @newheap/platform-common: @newheap/nh-toastr 0.1 compatibility

- The peer dependency range now accepts `@newheap/nh-toastr` 0.1 in addition to the supported 0.0 releases, so the
  toast action API can be installed beside `@newheap/platform-common` without bypassing npm's peer checks.

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

## @newheap/platform-ai-chat: the alert channel hint names only offered channels

- The notification preferences' hint of an alert kind the person may not choose no longer names Web Push, or
  in-app notifications, when the server does not offer that channel (`availableChannels`).

## @newheap/platform-ai-chat: ordered list start and table column alignment in Markdown

- Assistant messages and `nh-assistant-document` now keep `start` on an ordered list that does not begin at 1
  (`3.` renders as 3) and `align` on the cells of an aligned table column. DOMPurify used to drop both because it
  checked their plain values against the link pattern. The set of allowed attributes is unchanged.

## @newheap/platform-select (new package): accessible combobox and multiselect

- `nh-combobox` chooses one value and `nh-multiselect` several, shown as removable chips. Both are
  standalone, `OnPush`, signal-based form controls (`formControl`, `formControlName`, `ngModel`) or work
  with `[value]` and `(valueChange)`, in the ARIA 1.2 combobox pattern with a listbox popup.
- A value the form writes is never reported back as a change, leaving the field marks it touched, the
  disabled state follows the form, and every primitive value is kept, including `0`, `false` and `''`;
  `compareWith` compares object values.
- Always a search field: local filtering on label and description, or a server `search` after
  `minQueryLength` characters (down to `0`) with a debounce, loading, empty and error states and a retry.
  `create` adds a "Create" row for text that matches no option; `noneLabel` and `noneValue` add an explicit
  empty choice to the combobox. Options carry `description`, `disabled`, `icon`, `image` and `classes`.
- Arrow keys, Home, End, Enter, Escape and Tab work as the pattern prescribes; Escape in an open list is
  marked as handled, so a surrounding `NhModal` stays open. Backspace in an empty multiselect search removes
  the last chip, and changes are announced to assistive technology.
- Texts are inputs with English defaults; `provideNhSelect` registers application-wide texts, also as a
  signal that follows the language, and an `errorText` for failed searches. Theme with the `--nh-select-*`
  custom properties; light and dark defaults, `size="compact"` and reduced motion are built in. The only
  peer dependencies are Angular and rxjs.
- `nh-form-dropdown` of `@newheap/platform-common` remains available as the legacy alternative.

## @newheap/platform-ai-chat: comboboxes from @newheap/platform-select

The searchable choices of the assistant administration and of the agent runs are now `nh-combobox` of
`@newheap/platform-select`, with the assistant's texts and its translated error codes. Choosing the
instructions text that is already chosen no longer reads it again; the read button next to it does.

| Breaking change | Required action |
|---|---|
| `@newheap/platform-ai-chat` has the new peer dependency `@newheap/platform-select`. | Install it next to the assistant: `npm install @newheap/platform-select`. |
| The internal exports `ɵNhAssistantPickerComponent` and `ɵNhAssistantPickerOption` are removed. | Use `NhComboboxComponent` and `NhSelectOption` from `@newheap/platform-select`. |

