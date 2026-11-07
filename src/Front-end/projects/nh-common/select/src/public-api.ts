/*
 * Public API Surface of @newheap/platform-common/select
 */
export {
  NH_SELECT_CONFIG,
  NH_SELECT_DEFAULT_TEXTS,
  provideNhSelect
} from './lib/nh-select.models';
export type {
  NhSelectCompare,
  NhSelectConfig,
  NhSelectConfigSource,
  NhSelectCreate,
  NhSelectErrorText,
  NhSelectOperation,
  NhSelectOption,
  NhSelectSearch,
  NhSelectSize,
  NhSelectTexts
} from './lib/nh-select.models';
export type { NhSelectRow } from './lib/nh-select-base';
// The shared base of both controls; exported for their declarations, not for extension.
export { NhSelectBase as ɵNhSelectBase } from './lib/nh-select-base';
export { NhComboboxComponent } from './lib/nh-combobox.component';
export { NhMultiselectComponent } from './lib/nh-multiselect.component';
export type { NhSelectChip } from './lib/nh-multiselect.component';
