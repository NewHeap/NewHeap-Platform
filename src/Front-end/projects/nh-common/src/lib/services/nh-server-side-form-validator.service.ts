import {Injectable} from "@angular/core";
import {UntypedFormGroup, UntypedFormControl, AbstractControl} from "@angular/forms";

export interface IServerSideFormValidationService {
}

@Injectable()
export class NhServerSideFormValidationService implements IServerSideFormValidationService {

  public validate<T extends IServerSideFormValidator>(formValidatorType: {
    new(): T;
  }, formGroup: UntypedFormGroup, objectToValidate: any): void {

    let formValidator = new formValidatorType();
    let formValidationResult: FormValidationResult = formValidator.validate(objectToValidate);

    if (!formValidationResult.hasErrors()) {
      return;
    }

    if (!formGroup.controls[""]) {
      formGroup.addControl("", new UntypedFormControl());
    }

    let formErrors = formValidationResult.getErrors();
    for (let i = 0; i < formErrors.length; i++) {
      let formError = formErrors[i];
      let formControl: UntypedFormControl = formValidator.getFormControlFromFormGroup(formGroup, formError.getFieldName());

      if (formControl == null) {
        formControl = <UntypedFormControl>formGroup.controls[""];
      }

      let shouldDisableAfterSet = false;
      if(formControl.disabled) { // Disabled form controls cannot have errors set
        formControl.enable();
        shouldDisableAfterSet = true;
      }

      if (!formControl.errors) {
        formControl.setErrors({remote: formError.getErrorMessages()});
      } else {
        if (!formControl.errors['remote'] || formControl.errors['remote'].length < 1) {
          formControl.errors['remote'] = [];
        }

        for (let formErrorMessage of formError.getErrorMessages()) {
          formControl.errors['remote'].push(formErrorMessage);
        }
      }

      if(shouldDisableAfterSet) {
        //formControl.disable();
      }
    }
  }
}

export interface IServerSideFormValidator {
  getFormControlFromFormGroup(formGroup: UntypedFormGroup, formControlKey: string): UntypedFormControl;

  validate(object: any): FormValidationResult;
}

export class AspMvcFormServerSideFormValidator implements IServerSideFormValidator {
  public getFormControlFromFormGroup(formGroup: UntypedFormGroup, formControlKey: string): UntypedFormControl {
    const separator = '.';
    let formControl: UntypedFormControl|null = null;
    const formControlKeySplit = formControlKey.split(separator);

    if (formControlKeySplit.length == 1) {
      for (let key in formGroup.controls) {
        if (formGroup.controls.hasOwnProperty(key)) {
          if (key.toLowerCase() == formControlKey.toLowerCase()) {
            formControl = <UntypedFormControl>formGroup.controls[key];
            break;
          }
        }
      }
    } else if (formControlKeySplit.length > 1) {
      const baseKey = formControlKeySplit[0];
      const subKey = formControlKeySplit.filter(x => x !== baseKey).join(separator);

      for (let key in formGroup.controls) {
        if (formGroup.controls.hasOwnProperty(key)) {
          if (key.toLowerCase() == baseKey.toLowerCase()) {
            if (typeof formGroup.controls[key] === 'object') {
              formControl = this.getFormControlFromFormGroup(<UntypedFormGroup>formGroup.controls[baseKey], subKey);
            }

            break;
          }
        }
      }
    }

    if (formControl == null) {
      formControl = <UntypedFormControl>formGroup.controls[""];
    }

    return formControl
  }

  public validate(object: any): FormValidationResult {
    let validationResult = new FormValidationResult();

    if (object && object != null) {
      if (object.error_description !== undefined) {
        validationResult.addErrorByFieldName("", object.error_description);
      }

      if (object.ModelState) {
        for (let key in object.ModelState) {

          if(!object.ModelState.hasOwnProperty(key)) {
            continue;
          }
          let errors = object.ModelState[key];
          for (let subKey in errors) {
            validationResult.addErrorByFieldName(key.replace("model.", ""), errors[subKey]);
          }
        }
      }

      if (object.error) {
        for (let key in object.error) {
          if(!object.error.hasOwnProperty(key)) {
            continue;
          }

          let errors = object.error[key];
          for (let subKey in errors) {
            if(!errors.hasOwnProperty(subKey)) {
              continue;
            }if(!errors.hasOwnProperty(subKey)) {
              continue;
            }

            validationResult.addErrorByFieldName(key, errors[subKey]);
          }
        }
      }

      if (object.Message !== undefined) {
        validationResult.addErrorByFieldName("", object.Message);
      }
    }

    return validationResult;
  }
}

export interface IFormValidationResult {
}

export class FormValidationResult implements IFormValidationResult {
  private formErrors: Array<FormError> = [];

  public getErrors(): Array<FormError> {
    return this.formErrors;
  }

  public hasErrors(): boolean {
    return (this.getErrors().length > 0);
  }

  public hasErrorForFieldName(fieldName: string): boolean {
    return (this.getErrorForFieldName(fieldName) !== null);
  }

  public getErrorForFieldName(fieldName: string): FormError|null {
    let formError: FormError|null = null;

    for (let i = 0; i < this.formErrors.length; i++) {
      if (this.formErrors[i].getFieldName() == fieldName) {
        formError = this.formErrors[i];
      }
    }

    return formError;
  }

  public addErrorByFieldName(fieldName: string, errorMessage: string): void {
    let formError: FormError|null = this.getErrorForFieldName(fieldName);

    if (formError == null) {
      formError = new FormError(fieldName);
      this.formErrors.push(formError);
    }

    formError.addErrorMessage(errorMessage);
  }

  constructor() {
  }
}

export interface IFormError {
}

export class FormError implements IFormError {
  private fieldName: string = '';
  private errorMessages: Array<string> = [];

  public getErrorMessages(): Array<string> {
    return this.errorMessages;
  }

  public getFieldName(): string {
    return this.fieldName;
  }

  public addErrorMessage(errorMessage: string): void {
    this.errorMessages.push(errorMessage);
  }

  private setFieldName(fieldName: string): void {
    if (!fieldName || fieldName == null) {
      fieldName = '';
    }

    this.fieldName = fieldName;
  }

  constructor(fieldName: string) {
    this.setFieldName(fieldName);
  }
}
