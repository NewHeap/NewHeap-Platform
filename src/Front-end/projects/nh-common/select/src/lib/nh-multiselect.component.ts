import { ChangeDetectionStrategy, Component, computed, forwardRef, input, linkedSignal, output } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { NhSelectBase } from './nh-select-base';
import { NhSelectIconComponent } from './nh-select-icon.component';
import { NhSelectOption } from './nh-select.models';

/** A chosen value as the multiselect shows it: a chip with the option's name. */
export interface NhSelectChip<T> {
  readonly value: T;
  /** The option of the value, or `null` when no option describes it. */
  readonly option: NhSelectOption<T> | null;
  readonly label: string;
  readonly icon: string | null;
  readonly image: string | null;
  readonly classes: string | readonly string[] | null;
  /** A chosen disabled option: the person cannot remove it. */
  readonly locked: boolean;
}

function toArray<T>(value: readonly T[] | T | null | undefined): readonly T[] {
  if (value === null || value === undefined) {
    return [];
  }

  return Array.isArray(value) ? [...value] : [value as T];
}

/**
 * Chooses several values, shown as removable chips in front of the search field: the combobox of
 * `nh-combobox` with a multi-selectable listbox. Enter or a click adds an option or removes a chosen
 * one and keeps the list open; Backspace in the empty search field removes the last chip. Every
 * change is announced to assistive technology, and the field describes the chosen values.
 *
 * Use it as a form control or with `[value]` and `(valueChange)`. The value is always an array; a
 * value the form writes is never reported back as a change, and leaving the field marks it touched.
 */
@Component({
  selector: 'nh-multiselect',
  standalone: true,
  imports: [NhSelectIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-multiselect.component.html',
  styleUrl: './nh-multiselect.component.scss',
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => NhMultiselectComponent), multi: true }],
  host: {
    class: 'nh-select nh-multiselect',
    '[class.nh-select-compact]': "resolvedSize() === 'compact'",
    '[class.nh-select-disabled]': 'isDisabled()',
    '[class.nh-select-open]': 'open()'
  }
})
export class NhMultiselectComponent<T = unknown> extends NhSelectBase<T> implements ControlValueAccessor {
  readonly multiple = true;

  /** The chosen values when the control is not a form control. */
  readonly value = input<readonly T[] | null | undefined>(undefined);

  /** Emits the values after the person added or removed one. */
  readonly valueChange = output<T[]>();
  /** Emits the options of the values after the person added or removed one. */
  readonly optionsChange = output<NhSelectOption<T>[]>();

  private readonly current = linkedSignal<readonly T[]>(() => toArray(this.value()));
  private onChange: (value: T[]) => void = () => undefined;

  readonly summaryId = `${this.uid}-summary`;

  readonly chips = computed<readonly NhSelectChip<T>[]>(() => this.current().map(value => {
    const option = this.findOption(value);
    return {
      value,
      option,
      label: option?.label ?? this.fallbackLabel(value),
      icon: option?.icon ?? null,
      image: option?.image ?? null,
      classes: option?.classes ?? null,
      locked: option?.disabled === true
    };
  }));

  /** The names of the chosen values, which describe the field. */
  readonly summary = computed(() => this.chips().map(chip => chip.label).join(', '));

  readonly inputText = computed(() => this.query() ?? '');

  writeValue(value: readonly T[] | T | null | undefined): void {
    this.current.set(toArray(value));
  }

  registerOnChange(onChange: (value: T[]) => void): void {
    this.onChange = onChange;
  }

  /** Removes a chip, as its remove button does. */
  remove(chip: NhSelectChip<T>): void {
    if (this.isDisabled() || chip.locked) {
      return;
    }

    this.commit(this.current().filter(value => !this.same(value, chip.value)));
    this.announce(this.resolvedTexts().removed(chip.label));
    this.focus();
  }

  protected isChosen(value: T): boolean {
    return this.current().some(item => this.same(item, value));
  }

  protected choose(option: NhSelectOption<T>, mode: 'toggle' | 'add'): void {
    this.remember(option);
    this.clearFailure();
    const chosen = this.isChosen(option.value);
    // A choice starts a new search; the list stays open for the next one.
    this.query.set(this.open() ? '' : null);
    if (chosen && mode === 'add') {
      return;
    }

    const texts = this.resolvedTexts();
    this.commit(chosen
      ? this.current().filter(value => !this.same(value, option.value))
      : [...this.current(), option.value]);
    this.announce(chosen ? texts.removed(option.label) : texts.added(option.label));
    this.active.set(this.rows().findIndex(row => row.option !== null && this.same(row.option.value, option.value)));
  }

  protected override selectionDescriptionId(): string | null {
    return this.chips().length > 0 ? this.summaryId : null;
  }

  protected override onEmptyBackspace(): void {
    const chips = this.chips();
    for (let index = chips.length - 1; index >= 0; index--) {
      if (!chips[index].locked) {
        this.remove(chips[index]);
        return;
      }
    }
  }

  private commit(values: readonly T[]): void {
    this.current.set(values);
    const value = [...values];
    this.onChange(value);
    this.valueChange.emit(value);
    this.optionsChange.emit(this.chips().map(chip => chip.option ?? { value: chip.value, label: chip.label }));
  }
}
