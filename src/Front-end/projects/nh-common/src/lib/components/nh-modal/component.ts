import { DOCUMENT } from '@angular/common';
import {
  AfterViewInit,
  Component,
  ComponentRef,
  ElementRef,
  Inject,
  NgZone,
  OnDestroy,
  OnInit,
  Type,
  output,
  viewChild, inject
} from '@angular/core';
import {INhModalComponent, NhModalComponentRef} from "../../services/nh-modal.service";
import {NhModalContentDirective} from "../../directives/nh-modal.directives";

export class NhModalComponentClosed {
  public constructor(init?: Partial<NhModalComponentClosed>) {
    Object.assign(this, init);
  }
}

export class NhModalComponentImpl<C> implements INhModalComponent<C> {
  protected modalComponentRef: NhModalComponentRef<C>|undefined;

  setModalComponentRef(ref: NhModalComponentRef<C>) {
    this.modalComponentRef = ref;
  }

  close() {
    this.modalComponentRef?.close();
  }
}

@Component({
    selector: 'nh-modal',
    templateUrl: './component.html',
    styleUrls: ['./component.scss'],
    standalone: false
})
export class NhModalComponent<C> implements OnInit, OnDestroy, AfterViewInit {
  readonly modalContent = viewChild.required(NhModalContentDirective);
  readonly modalHolder = viewChild.required<ElementRef>('modalHolder');
  readonly modalBackdrop = viewChild.required<ElementRef>('modalBackdrop');
  readonly modal = viewChild.required<ElementRef>('modal');

  readonly closed = output<NhModalComponentClosed>();
  public componentRef: ComponentRef<C> | undefined;
  private modalComponentRef: NhModalComponentRef<C> | undefined;
  private document: Document = inject(DOCUMENT);
  title: string = '';
  modalHolderClasses: string = '';
  modalClasses: string = '';
  modalHeaderClasses: string = '';
  modalBodyClasses: string = '';
  isLoading: boolean = false;
  closeable: boolean = true;

  ngOnInit() {
    this.document.body.classList.add('nh-modal-open');
  }

  ngAfterViewInit() {
    //Timeout makes sure everything is rendered because browsers needs a little time and if no timeout the animation wil stutter
    setTimeout(() => {
      if(this.modal()) {
        this.modal().nativeElement.style.transform = '';
      }

      if(this.modalBackdrop()) {
        this.modalBackdrop().nativeElement.style.opacity = 0.85;
      }
    }, 50);
  }

  setModalComponentRef(modalComponentRef: NhModalComponentRef<C>) {
    this.modalComponentRef = modalComponentRef;
  }

  setContentComponent(componentType: Type<C>) {
    const viewContainerRef = this.modalContent().viewContainerRef;
    viewContainerRef.clear();
    this.componentRef = viewContainerRef.createComponent<C>(componentType);
  }


  close() {
    const modalBackdrop = this.modalBackdrop();
    modalBackdrop.nativeElement.style.transition = 'opacity 0.5s';
    const modal = this.modal();
    modal.nativeElement.style.transition = 'transform 0.5s';
    modalBackdrop.nativeElement.style.opacity = 0;

    //When mobile, use smooth scrolldown close, on desktop just disappear
    if (window.innerWidth < 920) {
      this.modalHolder().nativeElement.style.pointerEvents = 'none';
      modal.nativeElement.style.transform = 'translateY(100vh)';
      this.document.body.classList.remove('nh-modal-open');
    } else {
      modal.nativeElement.style.transition = 'opacity 0.5s';
      modal.nativeElement.style.opacity = '0';
    }
    setTimeout(() => {
      const data = new NhModalComponentClosed({});
      this.closed.emit(data);
      this.modalComponentRef?.close();
    }, 500);
  }

  ngOnDestroy() {
    this.document.body.classList.remove('nh-modal-open');
    this.componentRef?.destroy();
  }

  modalSlideStart(event: MouseEvent) {
  }

  modalTouchStart(event: TouchEvent) {
  }

  modalTouchEnd(event: TouchEvent) {
    this.modalScrollStop();
  }

  modalTouchMove(event: TouchEvent) {
    this.modalScrollMove(event.touches[0].clientY);
  }

  modalMouseUp(event: MouseEvent) {
    this.modalScrollStop();
  }

  modalScrollStop() {

  }

  modalMouseMove(event: MouseEvent) {
    this.modalScrollMove(event.clientY);
  }

  modalScrollMove(y: number) {

  }
}
