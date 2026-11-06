import {forwardRef} from '@angular/core';
import {ControlValueAccessor, NG_VALUE_ACCESSOR} from '@angular/forms';

/**
 * Base class for a custom form control. Assign a user's change to `value`: that reports it to the
 * form through `onChange`. A value that the form writes through `writeValue` is only stored, because
 * the form already has it, so `setValue`, `reset` and a template-driven `ngModel` binding do not
 * echo back as a change and do not make the control dirty.
 */
export abstract class AbstractValueAccessor implements ControlValueAccessor {
  _value: any = '';
  get value(): any {
    return this._value;
  };

  set value(v: any) {
    if (v !== this._value) {
      this._value = v;
      this.onChange(v);
    }
  }

  writeValue(value: any) {
    this._value = value;
  }

  onChange = (_: any) => {
  };
  onTouched = () => {
  };

  registerOnChange(fn: (_: any) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }
}

export function MakeProvider(type: any) {
  return {
    provide: NG_VALUE_ACCESSOR,
    useExisting: forwardRef(() => type),
    multi: true
  };
}
