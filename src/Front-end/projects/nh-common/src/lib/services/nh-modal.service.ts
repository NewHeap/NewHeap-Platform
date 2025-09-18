import {ComponentRef, EventEmitter, Injectable, Type, ViewContainerRef} from '@angular/core';
import {BehaviorSubject, Subscription} from 'rxjs';
import {NhModalComponent, NhModalComponentClosed} from "../components/nh-modal/component";
import {NhModalConfirmComponent} from "../components/nh-confirm-modal/component";

export interface INhModalComponent<C> {
  setModalComponentRef(ref: NhModalComponentRef<C>): void;
}

export class NhModalComponentRef<C> {
  public readonly closed: EventEmitter<NhModalComponentClosed> = new EventEmitter<NhModalComponentClosed>();

  private _onCloses: Subscription[] = [];

  onClose(callback: () => void) {
    this._onCloses.push(this.closed.subscribe(callback));
  }

  close() {
    if (this.componentRef && this.componentRef.instance) {
      this.componentRef.destroy();
      this.closed.emit();
    }
    for (let item of this._onCloses) {
      item.unsubscribe();
    }
  }

  get modalComponent(): NhModalComponent<C> {
    return this.componentRef.instance;
  }

  get contentComponent(): C | undefined {
    return this.modalComponent.componentRef?.instance;
  }

  constructor(public componentRef: ComponentRef<NhModalComponent<C>>) {
  }
}

export class NhModalOptions {
  title: string = '';
  modalHolderClasses: string = '';
  modalClasses: string = '';
  modalHeaderClasses: string = '';
  isLoading: boolean = false;
  closeable: boolean = true;
  modalBodyClasses: string = '';
  viewContainerRef?: ViewContainerRef;

  public constructor(init?: Partial<NhModalOptions>) {
    Object.assign(this, init);
  }
}

@Injectable({
  providedIn: 'root'
})
export class NhModalService {
  private viewContainerRef: ViewContainerRef | undefined;
  modalRefs: NhModalComponentRef<any>[] = [];
  modalRefsOpen: BehaviorSubject<boolean> = new BehaviorSubject<boolean>(false);

  constructor() {
  }

  setViewContainerRef(viewContainerRef: ViewContainerRef) {
    this.viewContainerRef = viewContainerRef;
  }

  confirm(onConfirm: (() => void) | (() => Promise<any>), onClose?: (() => void), options?: NhModalOptions) {
    return this.confirmDialog('',onConfirm,onClose,options);
  }

  confirmDialog(text: string,onConfirm: (() => void) | (() => Promise<any>), onClose?: (() => void), options?: NhModalOptions) {
    const modal = this.open(NhModalConfirmComponent, options);
    modal.contentComponent!.message = text;
    const onConfirm$ = modal.contentComponent!.confirmed.subscribe(() => {
      Promise.resolve(onConfirm()).then();
    });
    const onClose$ = modal.closed.subscribe(() => {
      if (onClose) {
        onClose();
      }
      onConfirm$.unsubscribe();
      onClose$.unsubscribe();
    });
    return modal;
  }


  /**
   * Create a modal dialog with a given component as content
   * @param componentType Type of the component to show in the modal
   * @param options Options for the modal
   * @param inputs Inputs to set on {@link componentType}
   */
  open<C extends INhModalComponent<C>>(componentType: Type<C>, options?: Partial<NhModalOptions>, inputs: Record<string, any> = {}): NhModalComponentRef<C> {
    const viewContainerRef = options?.viewContainerRef ?? this.viewContainerRef;

    if (!viewContainerRef) {
      throw 'View container ref missing';
    }

    options = options ?? new NhModalOptions();

    const modalComponentRef = viewContainerRef.createComponent<NhModalComponent<C>>(NhModalComponent);


    try {
      modalComponentRef.instance.setContentComponent(componentType);
      // Set inputs on the component
      if (inputs) {
        for (let key in inputs) {
          modalComponentRef.instance.componentRef?.setInput(key, inputs[key]);
        }
      }

    } catch (ex: any) {
      if (ex?.name === 'NullInjectorError') {
        console.error('Injection failed, possibly because the component is not declared in the root module. Try to provide the ViewContainerRef in the options of the open method.');
      }
      throw ex;
    }


    const modalRef = new NhModalComponentRef(modalComponentRef);

    modalComponentRef.instance.setModalComponentRef(modalRef);

    modalRef.modalComponent.title = options.title ?? '';
    modalRef.modalComponent.modalHolderClasses = options.modalHolderClasses ?? '';
    modalRef.modalComponent.modalClasses = options.modalClasses ?? '';
    modalRef.modalComponent.modalBodyClasses = options.modalBodyClasses ?? '';
    modalRef.modalComponent.modalHeaderClasses = options.modalHeaderClasses ?? '';
    modalRef.modalComponent.closeable = options.closeable ?? true;

    const modalRefClosed = () => {
      this.modalRefs = this.modalRefs.filter(x => x !== modalRef);
      if (this.modalRefs.length === 0) {
        this.modalRefsOpen.next(false);
      }
    };

    modalRef.modalComponent.closed.subscribe(modalRefClosed);
    modalRef.closed.subscribe(modalRefClosed);
    modalRef.contentComponent?.setModalComponentRef(modalRef);

    this.modalRefs.push(modalRef);
    this.modalRefsOpen.next(true);

    return modalRef;
  }

  close(componentRef: NhModalComponentRef<any>) {
    if (componentRef) {
      // First trigger the modal close, so that the animation and such is triggered. The actual close method will be called via the modalComponent
      componentRef.modalComponent.close();
      this.modalRefs = this.modalRefs.filter(x => x !== componentRef);
      if (this.modalRefs.length === 0) {
        this.modalRefsOpen.next(false);
      }
    }
  }

  closeLatest() {
    const latestModalRef = this.modalRefs[this.modalRefs.length - 1];
    this.close(latestModalRef);
  }
}
