# v-next

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
