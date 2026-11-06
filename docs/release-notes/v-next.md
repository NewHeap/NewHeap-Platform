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
