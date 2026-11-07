# v-next

## @newheap/platform-ai-chat: the alert channel hint names only offered channels

- The notification preferences' hint of an alert kind the person may not choose no longer names Web Push, or
  in-app notifications, when the server does not offer that channel (`availableChannels`).

## @newheap/platform-ai-chat: ordered list start and table column alignment in Markdown

- Assistant messages and `nh-assistant-document` now keep `start` on an ordered list that does not begin at 1
  (`3.` renders as 3) and `align` on the cells of an aligned table column. DOMPurify used to drop both because it
  checked their plain values against the link pattern. The set of allowed attributes is unchanged.

## @newheap/platform-ai-chat: comboboxes from @newheap/platform-common/select

The searchable choices of the assistant administration and of the agent runs are now `nh-combobox` of
`@newheap/platform-common/select`, with the assistant's texts and its translated error codes. Choosing the
instructions text that is already chosen no longer reads it again; the read button next to it does.

| Breaking change | Required action |
|---|---|
| `@newheap/platform-ai-chat` requires Common 6.1.0 or newer within major 6 for its select controls. | Install `@newheap/platform-common@^6.1.0` next to AI Chat. |
| The internal exports `ɵNhAssistantPickerComponent` and `ɵNhAssistantPickerOption` are removed. | Use `NhComboboxComponent` and `NhSelectOption` from `@newheap/platform-common/select`. |

## @newheap/platform-common: accessible combobox and multiselect

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
  custom properties; light and dark defaults, `size="compact"` and reduced motion are built in. Import the controls from `@newheap/platform-common/select`.
- `nh-form-dropdown` of `@newheap/platform-common` remains available as the legacy alternative.
