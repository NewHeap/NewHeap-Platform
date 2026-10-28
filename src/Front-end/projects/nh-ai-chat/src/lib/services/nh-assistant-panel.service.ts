import { BreakpointObserver } from '@angular/cdk/layout';
import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, Injector, Signal, afterNextRender, effect, inject, signal, untracked } from '@angular/core';
import { Subscription, firstValueFrom } from 'rxjs';
import { AgentThreadLookup } from '../models/assistant-agent-run.models';
import { NhAssistantClientErrorCodes } from '../models/assistant-sse.models';
import { NhAssistantAgentRunApiService } from './nh-assistant-agent-run-api.service';
import { NhAssistantApiError, nhAssistantErrorMessageKey } from './nh-assistant-transport';
import { NhAssistantStore } from './nh-assistant.store';

/** Drawer width on wider screens; below the mobile breakpoint the panel fills the screen. */
export const NH_ASSISTANT_PANEL_WIDTH = '420px';
export const NH_ASSISTANT_PANEL_MOBILE_QUERY = '(max-width: 599.98px)';

/** What `NhAssistantPanelService.open` opens the drawer on; without any of them, what it showed last. */
export interface NhAssistantPanelOpenOptions {
  /** Opens this conversation, as `open(conversationId)` does. */
  readonly conversationId?: string;
  /**
   * Starts a new conversation with this agent, as choosing it for a new chat does; ignored with
   * `conversationId`. An agent the person may not use leaves the conversation as it is and puts no
   * draft in the composer.
   */
  readonly agentId?: string;
  /**
   * Text the composer shows for the person to review and send; it is never sent by itself. Trimmed and
   * capped at the server's `maxMessageChars`, as `NhAssistantStore.setDraft` does.
   */
  readonly draft?: string;
  /** Replaces what the person already typed. By default the composer keeps their text and drops `draft`. */
  readonly replaceDraft?: boolean;
}

/**
 * Opens and closes the assistant panel as a CDK overlay drawer on the right edge.
 * `nh-assistant-panel` registers its content once; any component can then open the panel,
 * optionally on a specific conversation, in a new conversation with an agent, or with a prepared
 * draft in the composer. Escape closes the panel and focus returns to the element that opened it.
 */
@Injectable()
export class NhAssistantPanelService {
  private readonly overlay = inject(Overlay);
  private readonly store = inject(NhAssistantStore);
  private readonly agentRuns = inject(NhAssistantAgentRunApiService);
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
    effect(() => {
      if (this.store.enabled() && this.store.restorePanelOpen() && this.portal && !this.openState()) {
        untracked(() => queueMicrotask(() => {
          if (this.portal && this.store.enabled() && this.store.restorePanelOpen() && !this.openState()) {
            this.showPanel();
          }
        }));
      }
    });
  }

  /**
   * Opens the drawer: on a conversation (`open(conversationId)` or `{ conversationId }`), in a new
   * conversation with an agent (`{ agentId }`), and with a prepared draft in the composer
   * (`{ draft, replaceDraft }`) that the person reviews and sends. The target applies once the
   * assistant's status is loaded. A draft never sends and keeps what the person already typed unless
   * `replaceDraft` is set; it is dropped when the conversation does not open, when the person may not
   * use the agent and when the drawer was closed in the meantime. A panel in a page (`mode="page"`)
   * shares the store, so a host shows a draft there with `NhAssistantStore.setDraft`.
   */
  open(target?: string | NhAssistantPanelOpenOptions): void {
    const options: NhAssistantPanelOpenOptions = typeof target === 'string' ? { conversationId: target } : target ?? {};
    const conversationId = options.conversationId || undefined;
    const agentId = conversationId ? undefined : options.agentId || undefined;
    const draft = options.draft;
    if (this.openState() && !conversationId && !agentId && draft === undefined) {
      return;
    }

    this.showPanel();
    void this.store.initialize().then(() => this.store.reloadStatus()).then(async () => {
      if (this.openState()) {
        this.store.setPanelOpen(true);
      }

      if (conversationId) {
        await this.store.openConversation(conversationId);
        if (this.store.activeConversation()?.id !== conversationId) {
          // The conversation did not open; a draft for it never lands in another one.
          return;
        }
      } else if (agentId) {
        if (!this.store.agents().some(agent => agent.id === agentId)) {
          return;
        }
        this.store.selectAgent(agentId);
        this.store.startNewConversation();
      }

      if (draft !== undefined && this.openState()) {
        this.store.setDraft(draft, { replace: options.replaceDraft === true });
      }
    });
  }

  /**
   * Opens the panel on the conversation with an agent about a subject, for example the intake
   * reviewer about one project. The server returns the agent's thread about the subject and creates
   * it without starting a run when it is missing; people then type in the conversation, which runs
   * a turn. Creating needs the participant role, readers may open an existing thread.
   *
   * Resolves with the lookup once the panel opens the conversation. A failure, such as
   * `assistant-subject-access-denied`, rejects with `NhAssistantApiError` and leaves the panel as
   * it is, so the caller shows the localized `messageKey` where the person asked.
   */
  async openSubject(agentId: string, subject: { type: string; id: string }): Promise<AgentThreadLookup> {
    const lookup = await firstValueFrom(this.agentRuns.openThread(agentId, subject.type, subject.id));
    const conversationId = lookup.thread?.conversationId;
    if (!conversationId) {
      const code = NhAssistantClientErrorCodes.invalidResponse;
      throw new NhAssistantApiError(200, code, nhAssistantErrorMessageKey(code));
    }

    this.open(conversationId);
    return lookup;
  }

  private showPanel(): void {
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
    this.store.setPanelOpen(false);
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
    } else {
      void this.store.initialize().then(() => {
        if (this.portal === portal && this.store.enabled() && this.store.restorePanelOpen()) {
          this.showPanel();
        }
      });
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
