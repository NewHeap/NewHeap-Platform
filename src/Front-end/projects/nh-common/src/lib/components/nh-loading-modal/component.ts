import { Component, input } from '@angular/core';
import {INhModalComponent, NhModalComponentRef} from "../../services/nh-modal.service";

@Component({
    selector: 'nh-loading-modal',
    templateUrl: './component.html',
    styleUrls: ['./component.scss'],
    standalone: false
})
export class NhModalLoadingComponent implements INhModalComponent<NhModalLoadingComponent> {
  readonly information = input<string>('');
  modalComponentRef: NhModalComponentRef<NhModalLoadingComponent> | undefined;

  setModalComponentRef(ref: NhModalComponentRef<NhModalLoadingComponent>): void {
    this.modalComponentRef = ref;
  }
}
