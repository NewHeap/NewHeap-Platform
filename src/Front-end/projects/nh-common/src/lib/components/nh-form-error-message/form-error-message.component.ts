import {Component, Input, OnInit, ViewEncapsulation} from '@angular/core';
import {AbstractControl, UntypedFormControl} from '@angular/forms';
import {TranslatePipe} from "@ngx-translate/core";

@Component({
  selector: 'nh-form-error-message',
  templateUrl: './form-error-message.component.html',
  styleUrls: ['./form-error-message.component.scss'],
  encapsulation: ViewEncapsulation.None,
  standalone: false
})
export class NhFormErrorMessageComponent implements OnInit {
  @Input() control: UntypedFormControl|AbstractControl|undefined;

  constructor() {
  }

  ngOnInit() {

  }

  getErrors(): Array<string> {
    const errors: Array<string> = [];
    if (this.control && this.control.errors) {
      if (this.control.errors['remote']) {
        for (const remoteErrorMsg of this.control.errors['remote']) {
          errors.push(remoteErrorMsg);
        }
      }
    }

    return errors;
  }
}
