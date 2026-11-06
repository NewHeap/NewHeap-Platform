import {Component, DestroyRef, Input, OnInit, inject, input, output} from '@angular/core';
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

  /**
   * Emitted after the confirm delegate (`onConfirm`) completed without an error. Not emitted when the
   * delegate failed, showed a failed `errorResult`, or closed the dialog itself.
   * `NhModalService.confirm` and `confirmDialog` close the dialog on it.
   */
  readonly confirmed = output();

  modalComponentRef: NhModalComponentRef<NhModalConfirmComponent>|undefined;
  /** Whether the confirm or cancel delegate runs. */
  private delegateRunning = false;
  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
    });
  }

  setModalComponentRef(ref: NhModalComponentRef<NhModalConfirmComponent>): void {
    this.modalComponentRef = ref;
  }

  /** Escape on the topmost modal shell, when it can close: closes through `closeDialog`. */
  onModalEscape(): void {
    this.closeDialog({});
  }

  /**
   * Browser back or forward navigation while the modal can close: closes through `onModalEscape`,
   * as Escape does. While the confirm or cancel delegate runs the modal stays open, so its result
   * or error is shown.
   */
  onModalBackNavigation(): void {
    if (this.delegateRunning) {
      return;
    }

    this.onModalEscape();
  }

  /**
   * @deprecated The modal shell handles Escape on keydown for the topmost modal that can close and
   * calls `onModalEscape`; this method no longer listens to the document.
   */
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

  /**
   * Runs `onConfirm` and emits `confirmed` when it completed without an error. A click while the
   * confirm or cancel delegate still runs is ignored, so the delegate never runs twice at once.
   */
  async confirm() {
    if (this.delegateRunning) {
      return;
    }

    this.errorResult = undefined;
    await this.executeDelegate(this.onConfirm);

    // The delegate may have closed the dialog, or shown a failed result without throwing.
    if (this.destroyed || this.showsFailure()) {
      return;
    }

    this.confirmed.emit();
  }

  /** Runs `onCancel`. A click while the confirm or cancel delegate still runs is ignored. */
  async cancel() {
    if (this.delegateRunning) {
      return;
    }

    this.errorResult = undefined;
    await this.executeDelegate(this.onCancel);
  }

  async executeDelegate(func: (() => void) | (() => Promise<void>)) {
    this.delegateRunning = true;
    try {
      await func();
    } catch (ex) {
      this.errorResult = NhApiUtil.taskResultFromResponse(ex);
      if(this.errorResult.isSuccess) {
        this.errorResult.addError('', 'An unknown error occurred');
      }
    } finally {
      this.delegateRunning = false;
    }
  }

  private showsFailure(): boolean {
    return !!this.errorResult && !this.errorResult.isSuccess;
  }
}
