import { Component, Input, input } from '@angular/core';

@Component({
    selector: 'nh-loader',
    templateUrl: './component.html',
    styleUrls: ['./component.scss'],
    standalone: false
})
export class NhLoaderComponent {
  clicked = false;
  active = false;
  closed = false;

  readonly dark = input(false);
  readonly hasBackdrop = input(true);
  readonly backdropTransparent = input(true);
  private _containerClass: string = '';
  @Input() set containerClass(value: string[] | string) {
    this._containerClass = Array.isArray(value) ? value.join(' ') : value;
  }
  get containerClass() {
    return this._containerClass;
  }

  constructor(
  ) {

  }
}
