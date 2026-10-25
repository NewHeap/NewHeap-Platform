import {DOCUMENT, isPlatformBrowser} from "@angular/common";

import {
  AfterContentInit,
  AfterViewInit,
  Component,
  ComponentRef,
  Directive,
  ElementRef,
  Inject,
  NgZone,
  OnChanges,
  OnDestroy,
  OnInit,
  PLATFORM_ID,
  Type,
  output,
  viewChild, inject, HostListener
} from '@angular/core';
import {TranslateService} from '@ngx-translate/core';
import {INhModalComponent, NhModalComponentRef} from "../../services/nh-modal.service";
import {NhModalContentDirective} from "../../directives/nh-modal.directives";
import {nhCommonText} from '../../i18n/nh-common-translations';
import {NhModalStack} from './nh-modal-stack';

let nextModalTitleId = 0;

/** Elements that can take focus with Tab, before the checks of `isTabbable`. */
const tabbableSelector = [
  'a[href]',
  'area[href]',
  'button',
  'input',
  'select',
  'textarea',
  'iframe',
  'object',
  'embed',
  'summary',
  'audio[controls]',
  'video[controls]',
  '[contenteditable]:not([contenteditable="false"])',
  '[tabindex]'
].join(',');

function activeElementOf(document: Document): HTMLElement | null {
  const active = document.activeElement;
  return active instanceof HTMLElement ? active : null;
}

function isTabbable(element: HTMLElement): boolean {
  if (element.tabIndex < 0 || element.matches(':disabled')) {
    return false;
  }

  if (element instanceof HTMLInputElement && element.type === 'hidden') {
    return false;
  }

  if (element.closest('[inert]') || element.getClientRects().length === 0) {
    return false;
  }

  return getComputedStyle(element).visibility !== 'hidden';
}

/** Tab reaches one radio button per group: the checked one, otherwise the first. */
function oneRadioPerGroup(elements: HTMLElement[]): HTMLElement[] {
  return elements.filter(element => {
    if (!(element instanceof HTMLInputElement) || element.type !== 'radio' || !element.name) {
      return true;
    }

    const group = elements.filter((other): other is HTMLInputElement =>
      other instanceof HTMLInputElement && other.type === 'radio' && other.name === element.name && other.form === element.form);
    const checked = group.find(radio => radio.checked);
    return checked ? checked === element : group[0] === element;
  });
}

export class NhModalComponentClosed {
  public constructor(init?: Partial<NhModalComponentClosed>) {
    Object.assign(this, init);
  }
}

@Directive()
export class NhModalComponentImpl<C> implements
  INhModalComponent<C>,
  OnChanges,
  OnInit,
  AfterContentInit,
  AfterViewInit,
  OnDestroy {
  protected modalComponentRef: NhModalComponentRef<C>|undefined;

  setModalComponentRef(ref: NhModalComponentRef<C>) {
    this.modalComponentRef = ref;
  }

  close() {
    this.modalComponentRef?.close();
  }

  ngOnChanges(): void {
    this.appOnChanges().then();
  }

  ngOnInit(): void {
    this.appOnInit().then();
  }

  ngAfterContentInit(): void {
    this.appAfterContentInit().then();
  }

  ngAfterViewInit(): void {
    this.appAfterViewInit().then();
  }

  ngOnDestroy(): void {
    this.appOnDestroy().then();
  }

  appOnChanges(): Promise<void> {
    return Promise.resolve();
  }

  appOnInit(): Promise<void> {
    return Promise.resolve();
  }

  appAfterContentInit(): Promise<void> {
    return Promise.resolve();
  }

  appAfterViewInit(): Promise<void> {
    return Promise.resolve();
  }

  appOnDestroy(): Promise<void> {
    return Promise.resolve();
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
  private readonly stack = inject(NhModalStack);
  private readonly translate = inject(TranslateService, {optional: true});
  private readonly zone = inject(NgZone);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  /** The element that had focus when the modal opened; it gets focus back when the modal closes. */
  private readonly restoreFocusTo: HTMLElement | null = this.browser ? activeElementOf(this.document) : null;
  private focusRestored = false;
  private closing = false;
  /** Watches content that renders its fields after the modal opened, until one takes focus. */
  private contentFocusWatch: MutationObserver | null = null;
  /** The id of the title, which names the dialog through `aria-labelledby`. */
  readonly titleId = `nh-modal-title-${++nextModalTitleId}`;
  title: string = '';
  modalHolderClasses: string = '';
  modalClasses: string = '';
  modalHeaderClasses: string = '';
  modalBodyClasses: string = '';
  isLoading: boolean = false;
  closeable: boolean = true;

  /** The accessible name of the close button: `nh-common.modal.close`, translated. */
  closeLabel(): string {
    return nhCommonText(this.translate, 'nh-common.modal.close');
  }

  ngOnInit() {
    this.document.body.classList.add('nh-modal-open');
    this.stack.push(this);
  }

  ngAfterViewInit() {
    this.focusOnOpen();

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
    if (this.closing) {
      return;
    }
    this.closing = true;

    // The modal below, or the page, takes Escape and Tab while this one fades out.
    this.stack.remove(this);
    this.stopContentFocusWatch();
    this.restoreFocus();

    const modalBackdrop = this.modalBackdrop();
    modalBackdrop.nativeElement.style.transition = 'opacity 0.5s';
    const modal = this.modal();
    modal.nativeElement.style.transition = 'transform 0.5s';
    modalBackdrop.nativeElement.style.opacity = 0;

    //When mobile, use smooth scrolldown close, on desktop just disappear
    if (window.innerWidth < 920) {
      this.modalHolder().nativeElement.style.pointerEvents = 'none';
      modal.nativeElement.style.transform = 'translateY(100vh)';
      this.releaseBody();
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
    this.stack.remove(this);
    this.stopContentFocusWatch();
    this.restoreFocus();
    this.releaseBody();
    this.componentRef?.destroy();
  }

  /**
   * Moves focus into the modal: to the content's element with `autofocus`, else the first
   * element of the body that Tab reaches, else the close button, else the dialog itself.
   * The shell does so when it opens, and once more when content that loads first renders its
   * first field; content may call it after it replaced its fields.
   */
  focusInitial(): void {
    if (this.browser) {
      this.moveFocusInside();
    }
  }

  /**
   * Focuses the modal when it opens. Content that loads before it renders its fields, such as a
   * mutation that reads its entity first, gets the close button or the dialog first; the first
   * field that appears then takes focus, unless the user moved focus in the meantime.
   */
  private focusOnOpen(): void {
    if (!this.browser || this.moveFocusInside()) {
      return;
    }

    const body = this.bodyElement();
    if (body && typeof MutationObserver !== 'undefined') {
      this.watchContentForFocus(body, activeElementOf(this.document));
    }
  }

  /** Focuses the first target; true when it is an element of the body. */
  private moveFocusInside(): boolean {
    const holder = this.holderElement();
    if (!holder) {
      return false;
    }

    const body = this.bodyElement();
    const contentTarget = body ? this.contentFocusTarget(body) : undefined;
    const target = contentTarget ?? this.tabbablesIn(holder)[0] ?? holder;
    target.focus({preventScroll: true});
    return !!contentTarget;
  }

  private contentFocusTarget(body: HTMLElement): HTMLElement | undefined {
    const autofocus = Array.from(body.querySelectorAll<HTMLElement>('[autofocus]')).find(isTabbable);
    return autofocus ?? this.tabbablesIn(body)[0];
  }

  private watchContentForFocus(body: HTMLElement, fallback: HTMLElement | null): void {
    this.zone.runOutsideAngular(() => {
      const watch = new MutationObserver(() => {
        if (activeElementOf(this.document) !== fallback) {
          this.stopContentFocusWatch();
          return;
        }

        if (this.contentFocusTarget(body)) {
          this.stopContentFocusWatch();
          this.moveFocusInside();
        }
      });
      watch.observe(body, {childList: true, subtree: true, attributes: true, attributeFilter: ['disabled', 'hidden', 'style']});
      this.contentFocusWatch = watch;
    });
  }

  private stopContentFocusWatch(): void {
    this.contentFocusWatch?.disconnect();
    this.contentFocusWatch = null;
  }

  /**
   * Escape dismisses the topmost modal when it can close, as its close button does; content
   * with `onModalEscape` decides itself. Tab and Shift+Tab stay inside the topmost modal.
   */
  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    if (event.defaultPrevented || event.isComposing || !this.stack.isTop(this)) {
      return;
    }

    if (event.key === 'Escape') {
      this.dismissWithEscape(event);
    } else if (event.key === 'Tab' && !event.altKey && !event.ctrlKey && !event.metaKey) {
      this.keepTabInside(event);
    }
  }

  private dismissWithEscape(event: KeyboardEvent): void {
    if (!this.closeable || this.closing || event.repeat) {
      return;
    }

    event.preventDefault();
    const content = this.componentRef?.instance as Partial<INhModalComponent<C>> | undefined;
    if (typeof content?.onModalEscape === 'function') {
      content.onModalEscape();
    } else {
      this.close();
    }
  }

  private keepTabInside(event: KeyboardEvent): void {
    const holder = this.holderElement();
    if (!holder) {
      return;
    }

    const tabbables = this.tabbablesIn(holder);
    if (tabbables.length === 0) {
      event.preventDefault();
      holder.focus({preventScroll: true});
      return;
    }

    // The browser moves focus while the next element is inside; at either end it wraps around.
    const active = activeElementOf(this.document);
    if (active && holder.contains(active) && this.hasTabbableBeyond(active, tabbables, event.shiftKey)) {
      return;
    }

    event.preventDefault();
    const wrapped = event.shiftKey ? tabbables[tabbables.length - 1] : tabbables[0];
    wrapped.focus({preventScroll: true});
  }

  private hasTabbableBeyond(active: HTMLElement, tabbables: HTMLElement[], backwards: boolean): boolean {
    if (backwards) {
      return tabbables.some(element =>
        !element.contains(active) && (active.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_PRECEDING) !== 0);
    }

    return tabbables.some(element => (active.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0);
  }

  private tabbablesIn(container: HTMLElement): HTMLElement[] {
    const candidates = Array.from(container.querySelectorAll<HTMLElement>(tabbableSelector)).filter(isTabbable);
    return oneRadioPerGroup(candidates);
  }

  private holderElement(): HTMLElement | undefined {
    return this.modalHolder()?.nativeElement as HTMLElement | undefined;
  }

  private bodyElement(): HTMLElement | null {
    return this.holderElement()?.querySelector<HTMLElement>('.nh-modal-body') ?? null;
  }

  /**
   * Gives focus back to the element that opened the modal, once. It does so only while focus is
   * in this modal or nowhere, so a modal opened on top or a page the user moved to keeps it.
   * When that element is gone, the modal that is now on top takes focus.
   */
  private restoreFocus(): void {
    if (this.focusRestored || !this.browser) {
      return;
    }
    this.focusRestored = true;

    const active = activeElementOf(this.document);
    const holder = this.holderElement();
    const focusHere = !active || active === this.document.body || (!!holder && holder.contains(active));
    if (!focusHere) {
      return;
    }

    const target = this.restoreFocusTo;
    if (target && target !== this.document.body && target.isConnected && !holder?.contains(target)) {
      target.focus({preventScroll: true});
      if (activeElementOf(this.document) === target) {
        return;
      }
    }

    this.stack.top()?.focusInitial();
  }

  /** The page scrolls again once the last open modal closes. */
  private releaseBody(): void {
    if (this.stack.isEmpty) {
      this.document.body.classList.remove('nh-modal-open');
    }
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
