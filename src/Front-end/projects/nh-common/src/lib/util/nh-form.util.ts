import {AbstractControl, UntypedFormArray, UntypedFormGroup} from '@angular/forms';
import {TranslateService} from '@ngx-translate/core';
import {enumValuesToArray} from "./nh-common-util";

export class NhFormHelper {
  public static clearErrors(form: any): void {
    if (form) {
      Object.keys(form.controls).forEach((key: string) => {
        const control = form.controls[key];
        NhFormHelper.clearError(control);
      });
    }
  }

  private static clearError(obj: any) {

    if (obj instanceof UntypedFormGroup) {
      NhFormHelper.clearFormGroupErrors(obj);
      return;
    }

    if (obj instanceof UntypedFormArray) {
      NhFormHelper.clearFormArrayErrors(obj);
      return;
    }

    if (obj instanceof AbstractControl) {
      NhFormHelper.clearControlErrors(obj);
      return;
    }
  }

  private static clearFormGroupErrors(formGroup: UntypedFormGroup) {
    if (formGroup) {
      Object.keys(formGroup.controls).forEach((key: string) => {
        const control = formGroup.get(key);
        NhFormHelper.clearError(control);
      });
    }
  }

  private static clearFormArrayErrors(formArray: UntypedFormArray) {
    if (formArray) {
      for (const control of formArray.controls) {
        NhFormHelper.clearError(control);
      }
    }
  }

  private static clearControlErrors(control: AbstractControl) {
    if (control) {
      control.setErrors(null);
    }
  }

  public static getEnumDropDownByEnum(e: any, translateService: TranslateService, translationPrefix: string, emptyFirst: boolean = true, skipValues: any[] = []) {
    const values = enumValuesToArray(e);
    const result = [];

    if (emptyFirst) {
      result.push({id: '', name: translateService.instant('general.make-a-choice')});
    }

    for (const value of values) {
      if (skipValues && skipValues.length > 0) {
        if (skipValues.findIndex(x => <string>x === <string>value) !== -1) {
          continue;
        }
      }
      result.push({id: value, name: translateService.instant(translationPrefix + value)});
    }

    return result;
  }
}
