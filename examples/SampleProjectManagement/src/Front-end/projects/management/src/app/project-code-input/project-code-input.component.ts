import { Component, input } from '@angular/core';
import { AbstractValueAccessor, MakeProvider } from '@newheap/platform-common';

@Component({
  selector: 'app-project-code-input',
  standalone: true,
  template: `
    <label>
      <span>{{ label() }}</span>
      <input
        type="text"
        [value]="value"
        [disabled]="disabled"
        (input)="updateValue($event)"
        (blur)="onTouched()"
        maxlength="12">
    </label>
  `,
  styles: `
    :host,label{display:block} label{display:grid;gap:6px} span{color:#617069;font-size:10px;font-weight:700;text-transform:uppercase}
    input{box-sizing:border-box;width:100%;border:1px solid #cbd6d0;border-radius:9px;padding:10px;background:#fff;text-transform:uppercase}
    input:disabled{opacity:.55}
  `,
  providers: [MakeProvider(ProjectCodeInputComponent)]
})
export class ProjectCodeInputComponent extends AbstractValueAccessor {
  readonly label = input('Projectcode');
  disabled = false;

  updateValue(event: Event): void {
    this.value = (event.target as HTMLInputElement).value.trim().toUpperCase();
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled = isDisabled;
  }
}
