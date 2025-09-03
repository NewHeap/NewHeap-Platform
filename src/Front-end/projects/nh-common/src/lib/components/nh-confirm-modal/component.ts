import {Component, HostListener, Input, OnInit, input, output} from '@angular/core';
import {INhModalComponent, NhModalComponentRef, NhModalService} from "../../services/nh-modal.service";
import { SafeHtml } from '@angular/platform-browser';
import {TaskResult} from "../../models/misc.models";
import {NhApiUtil} from "../../util/nh-api-util";

@Component({
    selector: 'nh-confirm-modal',
    templateUrl: './component.html',
    styleUrls: ['./component.scss'],
    standalone: false
})
export class NhModalConfirmComponent implements OnInit, INhModalComponent<NhModalConfirmComponent> {
  @Input() message: string|SafeHtml = '';
  @Input() btnConfirmText: string = 'general.yes';
  @Input() btnCancelText: string = 'general.no';
  @Input() modalClass: ''|'success'|'danger'|'warning'|'info' = '';
  @Input() btnConfirmDisabled: boolean = false;
  @Input() btnCancelDisabled: boolean = false;
  @Input() allBtnsDisabled: boolean = false;
  @Input() showLoader: boolean = false;
  @Input() errorResult: TaskResult<any>|undefined;

  readonly confirmed = output();

  modalComponentRef: NhModalComponentRef<NhModalConfirmComponent>|undefined;
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

  @Input() onConfirm: (() => void) | (() => Promise<void>) = () => {};
  @Input() onCancel: (() => void) | (() => Promise<void>) = () => {};

  readonly value = input<any>();

  async confirm() {
    this.errorResult = undefined;
    await this.executeDelegate(this.onConfirm);
  }

  async cancel() {
    this.errorResult = undefined;
    await this.executeDelegate(this.onCancel);
  }

  async executeDelegate(func: (() => void) | (() => Promise<void>)) {
    try {
      await func();
    } catch (ex) {
      this.errorResult = NhApiUtil.taskResultFromResponse(ex);
      if(this.errorResult.isSuccess) {
        this.errorResult.addError('', 'An unknown error occurred');
      }
    }
  }
}
