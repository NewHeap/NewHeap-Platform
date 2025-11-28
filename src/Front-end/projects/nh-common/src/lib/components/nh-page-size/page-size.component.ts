import {Component, Input} from '@angular/core';
import {of} from 'rxjs';
import {AbstractValueAccessor, MakeProvider} from "../../accessors/abstract-value.accessor";
import {DefaultMultiSelectSettings, NhFormDropDownSettings} from "../nh-form-dropdown/form-dropdown.component";

@Component({
  selector: 'nh-page-size',
  templateUrl: './page-size.component.html',
  styleUrls: ['./page-size.component.scss'],
  standalone: false,
  providers: [MakeProvider(NhPageSizeComponent)],
})
export class NhPageSizeComponent extends AbstractValueAccessor {
  isDisabled = false;
  @Input() name: string = 'page-size';

  pageSizeDropDownSettings = new NhFormDropDownSettings({
    lazyLoad: false,
    loadLambda: () => of([
      {id: 10, value: 10},
      {id: 25, value: 25},
      {id: 50, value: 50},
      {id: 100, value: 100},
    ]),
    keyGetLambda: x => x.id,
    valueGetLambda: x => x.value,
    multiSelectSettings: new DefaultMultiSelectSettings({selectionLimit: 1, closeOnSelect: true, enableSearch: false})
  });

  setDisabledState?(isDisabled: boolean): void {
    // optional: disable your input
    this.isDisabled = isDisabled;
  }
}
