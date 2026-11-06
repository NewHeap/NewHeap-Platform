import {ComponentRef, DestroyRef, EventEmitter, Injectable, Type, ViewContainerRef, inject} from '@angular/core';
import {NavigationStart, Router} from '@angular/router';
import {BehaviorSubject, Subscription, filter} from 'rxjs';
import {NhModalComponent, NhModalComponentClosed} from "../components/nh-modal/component";
import {NhModalConfirmComponent} from "../components/nh-confirm-modal/component";

export interface INhModalComponent<C> {
  setModalComponentRef(ref: NhModalComponentRef<C>): void;

  /**
   * Called instead of closing when Escape reaches the topmost modal and the modal can close.
   * Content without it closes as the close button does. Mutating and confirmation content
   * call their `closeDialog`.
   */
  onModalEscape?(): void;

  /**
   * Called instead of closing when the user navigates back or forward with the browser while the
   * modal can close and closes on back navigation. Content without it is dismissed as Escape
   * dismisses it: through `onModalEscape` when it has one, otherwise as the close button does.
   * Mutating and confirmation content stay open while their save or confirmation runs, and
   * otherwise call `onModalEscape`.
   */
  onModalBackNavigation?(): void;
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
  /**
   * Whether browser back or forward navigation closes the modal, as Escape does. On by default.
   * Set it to `false` for a modal that must stay open while the page below it changes; such a
   * modal must be hosted in the application root's view container, because a modal hosted in a
   * page is destroyed with that page. A modal that cannot close (`closeable: false`) stays open
   * either way, and navigation started from code never closes a modal.
   */
  closeOnBackNavigation: boolean = true;
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
    this.dismissOnBrowserHistoryNavigation();
  }

  setViewContainerRef(viewContainerRef: ViewContainerRef) {
    this.viewContainerRef = viewContainerRef;
  }

  /** Opens a confirmation without a message; see `confirmDialog`. */
  confirm(onConfirm: (() => void) | (() => Promise<any>), onClose?: (() => void), options?: NhModalOptions) {
    return this.confirmDialog('',onConfirm,onClose,options);
  }

  /**
   * Opens a confirmation that shows `text`. "Yes" runs `onConfirm`, which may return a promise, and
   * closes the dialog once it completed; when it throws, the dialog stays open and shows the error.
   * "No", the close button, Escape and back navigation close the dialog without running it.
   * `onClose` runs once when the dialog closes, whichever way.
   */
  confirmDialog(text: string,onConfirm: (() => void) | (() => Promise<any>), onClose?: (() => void), options?: NhModalOptions) {
    const modal = this.open(NhModalConfirmComponent, options);
    const content = modal.contentComponent!;
    content.message = text;
    content.onConfirm = async () => {
      await onConfirm();
    };
    content.onCancel = () => content.closeDialog({});

    const confirmed$ = content.confirmed.subscribe(() => content.closeDialog({}));
    const onClose$ = modal.closed.subscribe(() => {
      if (onClose) {
        onClose();
      }
      confirmed$.unsubscribe();
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
    modalRef.modalComponent.closeOnBackNavigation = options.closeOnBackNavigation ?? true;

    // Called once for every way the modal leaves; only the first call changes the open modals.
    const modalRefClosed = () => {
      if (!this.modalRefs.includes(modalRef)) {
        return;
      }

      this.modalRefs = this.modalRefs.filter(x => x !== modalRef);
      if (this.modalRefs.length === 0) {
        this.modalRefsOpen.next(false);
      }
    };

    modalRef.modalComponent.closed.subscribe(modalRefClosed);
    modalRef.closed.subscribe(modalRefClosed);
    // A page that hosts the modal destroys it on a route change without closing it.
    modalComponentRef.onDestroy(modalRefClosed);
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

  /**
   * The browser's back and forward buttons dismiss the open modals, as Escape does. The router
   * reports them as a `NavigationStart` with the `popstate` trigger. Navigation started from code,
   * such as `router.navigate` or a link inside a modal, keeps the modals open.
   */
  private dismissOnBrowserHistoryNavigation(): void {
    const router = inject(Router, {optional: true});
    if (!router) {
      return;
    }

    const subscription = router.events
      .pipe(filter((event): event is NavigationStart => event instanceof NavigationStart && event.navigationTrigger === 'popstate'))
      .subscribe(() => this.dismissForBackNavigation());

    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  /**
   * Dismisses the modals that are open when the navigation starts, the topmost first. Each one
   * decides as `NhModalComponent.dismissOnBackNavigation` describes, so a modal that cannot close
   * or opted out stays open wherever it is in the stack. A modal opened meanwhile, such as a
   * confirmation that the content opens before it discards changes, stays open. A modal that its
   * hosting page destroyed has already left `modalRefs`.
   */
  private dismissForBackNavigation(): void {
    const openModalRefs = [...this.modalRefs].reverse();

    for (const modalRef of openModalRefs) {
      modalRef.modalComponent.dismissOnBackNavigation();
    }
  }
}
