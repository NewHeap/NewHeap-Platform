import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  forwardRef,
  input,
  linkedSignal,
  output
} from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { NhSelectBase, NhSelectRow } from './nh-select-base';
import { NhSelectIconComponent } from './nh-select-icon.component';
import { NhSelectOption } from './nh-select.models';

/**
 * Chooses one value: an accessible combobox with a listbox popup in the ARIA 1.2 pattern, always
 * with a search field. Typing filters the options on label and description, or asks `search` after
 * `minQueryLength` characters; the arrow keys, Home and End move, Enter chooses, Escape closes the
 * list without closing a surrounding modal, and Tab leaves. A failed search says why and offers to
 * search again; `create` adds a "Create" row for text that matches no option, and `noneLabel` an
 * explicit empty choice that stands for `noneValue`.
 *
 * Use it as a form control (`formControl`, `formControlName`, `ngModel`) or with `[value]` and
 * `(valueChange)`. A value the form writes is never reported back as a change, so the control stays
 * pristine until the person chooses; leaving the field marks it touched. Every primitive value is
 * kept as it is, including `0`, `false` and `''`.
 */
@Component({
  selector: 'nh-combobox',
  standalone: true,
  imports: [NhSelectIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-combobox.component.html',
  styleUrl: './nh-combobox.component.scss',
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => NhComboboxComponent), multi: true }],
  host: {
    class: 'nh-select nh-combobox',
    '[class.nh-select-compact]': "resolvedSize() === 'compact'",
    '[class.nh-select-disabled]': 'isDisabled()',
    '[class.nh-select-open]': 'open()'
  }
})
export class NhComboboxComponent<T = unknown> extends NhSelectBase<T> implements ControlValueAccessor {
  readonly multiple = false;

  /** The chosen value when the control is not a form control. */
  readonly value = input<T | null | undefined>(undefined);
  /** The name to show for a value no option describes, such as a stored value of a server search. */
  readonly selectedLabel = input<string | null>(null);
  /** The label of an explicit empty choice at the top of the list; without it there is none. */
  readonly noneLabel = input<string | null>(null);
  /** The value the empty choice stands for. Default `null`. */
  readonly noneValue = input<T | null>(null);
  /** Shows the description of the chosen option under the closed field. Default `true`. */
  readonly showSelectedDescription = input(true, { transform: booleanAttribute });

  /** Emits the value the person chose, also `noneValue` for the empty choice. */
  readonly valueChange = output<T | null>();
  /** Emits the option the person chose, also one a search or `create` answered; `null` for the empty choice. */
  readonly optionChange = output<NhSelectOption<T> | null>();

  private readonly current = linkedSignal<T | null>(() => this.value() ?? null);
  private onChange: (value: T | null) => void = () => undefined;

  /** Whether the empty choice is the current value. */
  readonly noneChosen = computed(() => this.noneLabel() !== null && this.same(this.current(), this.noneValue()));

  /** The chosen option; a value no option describes gets `selectedLabel` or its text. */
  readonly selected = computed<NhSelectOption<T> | null>(() => {
    const value = this.current();
    if (value === null || this.noneChosen()) {
      return null;
    }

    return this.findOption(value) ?? { value, label: this.selectedLabel() ?? this.fallbackLabel(value) };
  });

  /** The typed text while the person types, otherwise the name of the choice. */
  readonly inputText = computed(() => {
    const query = this.query();
    if (query !== null) {
      return query;
    }

    return this.noneChosen() ? this.noneLabel() ?? '' : this.selected()?.label ?? '';
  });

  readonly chosenDescription = computed(() => this.showSelectedDescription() ? this.selected()?.description ?? null : null);

  writeValue(value: T | null | undefined): void {
    this.current.set(value ?? null);
  }

  registerOnChange(onChange: (value: T | null) => void): void {
    this.onChange = onChange;
  }

  protected isChosen(value: T): boolean {
    const current = this.current();
    return current !== null && !this.noneChosen() && this.same(current, value);
  }

  protected choose(option: NhSelectOption<T>): void {
    this.remember(option);
    this.clearFailure();
    this.close();
    if (this.isChosen(option.value)) {
      return;
    }

    this.commit(option.value, option);
  }

  protected override chooseNone(): void {
    this.clearFailure();
    this.close();
    if (this.noneChosen()) {
      return;
    }

    this.commit(this.noneValue(), null);
  }

  protected override noneRow(): NhSelectRow<T> | null {
    const label = this.noneLabel();
    if (label === null) {
      return null;
    }

    // Typed text hides the empty choice unless it matches; a server search answers options only.
    const text = this.typed().toLocaleLowerCase();
    if (text.length > 0 && (this.search() !== null || !label.toLocaleLowerCase().includes(text))) {
      return null;
    }

    return {
      kind: 'none',
      option: null,
      label,
      description: null,
      icon: null,
      image: null,
      classes: null,
      selected: this.noneChosen(),
      disabled: false
    };
  }

  private commit(value: T | null, option: NhSelectOption<T> | null): void {
    this.current.set(value);
    this.onChange(value);
    this.valueChange.emit(value);
    this.optionChange.emit(option);
  }
}
