# @newheap/platform-common/select

Accessible Angular combobox and multiselect for NewHeap applications:
`nh-combobox` chooses one value, `nh-multiselect` several, shown as removable
chips. Both are standalone, `OnPush` and signal-based form controls with an
always-present search field, local filtering or a server search, an optional
"Create" row and full keyboard support in the ARIA 1.2 combobox pattern.

## Installation

The package is public on npmjs.org and installs without a registry token. An optional project `.npmrc` can make the public source explicit:

```text
registry=https://registry.npmjs.org/
@newheap:registry=https://registry.npmjs.org/
```

Install the package:

```bash
npm install @newheap/platform-common
```

View available versions on [npmjs.org](https://www.npmjs.com/package/@newheap/platform-common). For NuGet, npm and AI-plugin installation, see [Consume public packages](../../../../../docs/how-to/consume-public-packages.md).

## Compatibility

The package supports Angular 20.3.28 through Angular 22. The select controls use Angular Forms and RxJS. Installation follows the peer dependencies of Common. It is built on the oldest
supported Angular baseline so the published partial-Ivy output remains
consumable across that range.

## Choose one value

```typescript
import { NhComboboxComponent, NhSelectOption } from '@newheap/platform-common/select';

@Component({
  imports: [ReactiveFormsModule, NhComboboxComponent],
  template: `
    <label for="status">Status</label>
    <nh-combobox inputId="status" [formControl]="status" [options]="statuses" noneLabel="No status" />
  `
})
export class ProjectStatusComponent {
  readonly status = new FormControl<ProjectStatus | null>(null);
  readonly statuses: NhSelectOption<ProjectStatus>[] = [
    { value: ProjectStatus.Draft, label: 'Draft', description: 'Not planned yet.' },
    { value: ProjectStatus.Active, label: 'Active' },
    { value: ProjectStatus.Archived, label: 'Archived', disabled: true }
  ];
}
```

Every primitive value is kept as it is, including `0`, `false` and `''`; pass
`compareWith` for object values. `noneLabel` adds an explicit empty choice that
stands for `noneValue` (default `null`). A value the form writes is never
reported back as a change, so the control stays pristine until the person
chooses, and leaving the field marks it touched. Without a form, bind
`[value]` and `(valueChange)`; `(optionChange)` emits the chosen option.

## Choose several values

```html
<label for="members">Members</label>
<nh-multiselect inputId="members" [formControl]="memberIds" [options]="members" placeholder="Add members" />
```

Enter or a click adds an option or removes a chosen one and keeps the list
open; Backspace in the empty search field removes the last chip. A chosen
disabled option is locked. Each change is announced to assistive technology.

## Server search and create

```html
<nh-combobox
  inputId="owner"
  [formControl]="ownerId"
  [search]="searchPeople"
  [minQueryLength]="2"
  [selectedLabel]="project.ownerName"
  [create]="createPerson"
  (optionCreated)="people.push($event)" />
```

`search` answers an `Observable` or `Promise` of options for the typed text,
after `minQueryLength` characters (default 2, `0` searches as soon as the list
opens) and a pause of `searchDebounceMs` (default 250). An answer to an older
text is ignored. A failed search says why below the field, through
`errorText`, and offers to try again instead of claiming that nothing was
found. `create` offers a "Create" row for text that equals no option's label
and chooses the option it answers. Show stored values that the options do not
contain with `selectedLabel` (combobox) or `selectedOptions` (both).

## Keyboard

| Key | Action |
|---|---|
| Arrow Down / Arrow Up | Open the list; move to the next or previous option, past disabled ones |
| Alt + Arrow Down / Alt + Arrow Up | Open or close the list |
| Home / End | With the list open, move to the first or last option |
| Enter | Choose the active option, or toggle it in a multiselect |
| Escape | Close the list; the key is marked as handled, so a surrounding modal stays open |
| Tab | Close the list and move on |
| Backspace | In an empty multiselect search, remove the last chip |

## Texts

Texts are inputs with English defaults: `placeholder`, `noneLabel` and
`texts` (`NhSelectTexts`). Register application-wide texts, for example
translated ones that follow the language, once with `provideNhSelect`:

```typescript
providers: [
  provideNhSelect(() => {
    const translate = inject(TranslateService);
    const texts = () => ({
      noResults: translate.instant('select.no-results'),
      typeMore: (count: number) => translate.instant('select.type-more', { count })
    });
    const config = signal({ texts: texts() });
    translate.onLangChange.subscribe(() => config.set({ texts: texts() }));
    return config;
  })
]
```

## Theming

Set the `--nh-select-*` custom properties on `:root` or a container: `font`,
`font-size`, `min-height`, `padding-inline`, `radius`, `option-radius`,
`background`, `text`, `text-muted`, `border`, `border-hover`, `focus`,
`accent`, `danger`, `popup-background`, `popup-border`, `popup-shadow`,
`popup-max-height`, `option-active-background`, `chip-background`,
`chip-text`, `z-index` and `motion-duration`, plus `compact-font-size`,
`compact-min-height` and `compact-padding-inline` for `size="compact"`. The
controls bring light and dark defaults for `prefers-color-scheme` and drop
their motion for `prefers-reduced-motion`.
