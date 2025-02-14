import { Injectable } from "@angular/core";
import { UntypedFormControl, UntypedFormGroup } from "@angular/forms";
import {TaskResultItem} from "../models/misc.models";

@Injectable()
export class NhTaskResultFormValidationService {
  public validate(formGroup: UntypedFormGroup, errors: TaskResultItem[]) {
    if (errors.length === 0) {
      return;
    }

    if (!formGroup.controls['']) {
      formGroup.addControl('', new UntypedFormControl());
    }

    for (const field of errors) {
      this.addError(formGroup, field.name, field.errorMessages);
    }
  }

  public addError(formGroup: UntypedFormGroup, controlName: string|undefined|null, errorMessages: string[]) {
    if (!formGroup || !errorMessages || errorMessages.length === 0) {
      return;
    }

    if(!controlName || controlName?.trim() === '') {
      controlName = '';
    }

    if (!formGroup.controls || !formGroup.controls['']) {
      formGroup.addControl('', new UntypedFormControl());
    }

    const formControl = this.getFormControlFromFormGroup(formGroup, controlName)
    ?? this.getFormControlFromFormGroup(formGroup, '');

    if(!formControl) {
      return;
    }

    if (!formControl.errors) {
      formControl.setErrors({ remote: errorMessages });
    } else {
      if ((formControl.errors['remote']?.length ?? 0) < 1) {
        formControl.errors['remote'] = [];
      }

      formControl.errors['remote'].concat(errorMessages);
    }
  }

  private getFormControlFromFormGroup(formGroup: UntypedFormGroup, formControlKey: string): UntypedFormControl {
    const separator = '.';
    let formControl: UntypedFormControl | undefined

    for (let key in formGroup.controls) {
      if (formGroup. controls.hasOwnProperty(key)) {
        if (key.toLowerCase() === formControlKey.toLowerCase()) {
          formControl = <UntypedFormControl>formGroup.controls[key];
          break;
        }
      }
    }

    if (!formControl) {
      formControl = <UntypedFormControl>formGroup.controls[""];
    }

    return formControl
  }
}
