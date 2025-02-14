import {Component, HostListener, Input, OnInit, input, output} from '@angular/core';
import {INhModalComponent, NhModalComponentRef, NhModalService} from "../../services/nh-modal.service";

@Component({
    selector: 'nh-confirm-modal',
    templateUrl: './component.html',
    styleUrls: ['./component.scss'],
    standalone: false
})
export class NhModalConfirmComponent implements OnInit, INhModalComponent<NhModalConfirmComponent> {
  @Input() description?: string;
  readonly cancelText = input<string>('Annuleren');
  readonly confirmText = input<string>('Bevestigen');
  readonly showCancel = input<boolean>(true);
  readonly showConfirm = input<boolean>(true);

  readonly confirmed = output();

  modalComponentRef: NhModalComponentRef<NhModalConfirmComponent>|undefined;

  constructor(
    private modalService: NhModalService
  ) { }

  setModalComponentRef(ref: NhModalComponentRef<NhModalConfirmComponent>): void {
    this.modalComponentRef = ref;
  }

  @HostListener('document:keyup', ['$event'])
  onKeyUp(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      event.stopImmediatePropagation();
      this.closeDialog({});
    }
  }

  ngOnInit() { }

  closeDialog(event?: any) {
    this.modalComponentRef?.close();
  }
}
