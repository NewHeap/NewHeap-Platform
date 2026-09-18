import { BreakpointObserver } from '@angular/cdk/layout';
import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, Injector, Signal, afterNextRender, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { NhAssistantStore } from './nh-assistant.store';

/** Drawer width on wider screens; below the mobile breakpoint the panel fills the screen. */
export const NH_ASSISTANT_PANEL_WIDTH = '420px';
export const NH_ASSISTANT_PANEL_MOBILE_QUERY = '(max-width: 599.98px)';

/**
 * Opens and closes the assistant panel as a CDK overlay drawer on the right edge.
 * `nh-assistant-panel` registers its content once; any component can then open the panel,
 * optionally on a specific conversation. Escape closes the panel and focus returns to the
 * element that opened it.
 */
@Injectable()
export class NhAssistantPanelService {
  private readonly overlay = inject(Overlay);
  private readonly store = inject(NhAssistantStore);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);
  private readonly breakpoints = inject(BreakpointObserver);

  private readonly openState = signal(false);
  private overlayRef?: OverlayRef;
  private portal?: TemplatePortal;
  private subscriptions = new Subscription();
  private returnFocusTo: HTMLElement | null = null;

  readonly isOpen: Signal<boolean> = this.openState.asReadonly();

  constructor() {
    inject(DestroyRef).onDestroy(() => this.dispose());
  }

  open(conversationId?: string): void {
    void this.store.initialize();
    if (conversationId) {
      void this.store.openConversation(conversationId);
    }

    if (this.openState()) {
      return;
    }

    const active = this.document.activeElement;
    this.returnFocusTo = active instanceof HTMLElement ? active : null;
    this.openState.set(true);
    this.attach();
  }

  close(): void {
    if (!this.openState()) {
      return;
    }

    this.openState.set(false);
    this.overlayRef?.detach();

    const target = this.returnFocusTo;
    this.returnFocusTo = null;
    if (target?.isConnected) {
      target.focus();
    }
  }

  toggle(): void {
    if (this.openState()) {
      this.close();
    } else {
      this.open();
    }
  }

  /** Called by `nh-assistant-panel`; hosts should place that component once instead. */
  registerPanel(portal: TemplatePortal): void {
    this.portal = portal;
    if (this.openState()) {
      this.attach();
    }
  }

  unregisterPanel(portal: TemplatePortal): void {
    if (this.portal !== portal) {
      return;
    }

    this.overlayRef?.detach();
    this.portal = undefined;
  }

  private attach(): void {
    if (!this.portal || this.overlayRef?.hasAttached()) {
      return;
    }

    const overlayRef = this.ensureOverlay();
    overlayRef.attach(this.portal);
    this.updateSize(this.breakpoints.isMatched(NH_ASSISTANT_PANEL_MOBILE_QUERY));

    afterNextRender(() => {
      const target = overlayRef.overlayElement.querySelector<HTMLElement>('[data-nh-assistant-autofocus]');
      target?.focus();
    }, { injector: this.injector });
  }

  private ensureOverlay(): OverlayRef {
    if (this.overlayRef) {
      return this.overlayRef;
    }

    const overlayRef = this.overlay.create({
      positionStrategy: this.overlay.position().global().top('0').right('0'),
      scrollStrategy: this.overlay.scrollStrategies.noop(),
      hasBackdrop: false,
      panelClass: 'nh-assistant-overlay-pane',
      height: '100%',
      width: NH_ASSISTANT_PANEL_WIDTH,
      maxWidth: '100vw'
    });

    this.subscriptions.add(overlayRef.keydownEvents().subscribe(event => {
      if (event.key === 'Escape') {
        event.preventDefault();
        this.close();
      }
    }));
    this.subscriptions.add(this.breakpoints.observe(NH_ASSISTANT_PANEL_MOBILE_QUERY).subscribe(state => {
      this.updateSize(state.matches);
    }));

    this.overlayRef = overlayRef;
    return overlayRef;
  }

  private updateSize(mobile: boolean): void {
    this.overlayRef?.updateSize({ width: mobile ? '100vw' : NH_ASSISTANT_PANEL_WIDTH, height: '100%' });
  }

  private dispose(): void {
    this.subscriptions.unsubscribe();
    this.overlayRef?.dispose();
    this.overlayRef = undefined;
  }
}
