import { TemplatePortal } from '@angular/cdk/portal';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  TemplateRef,
  ViewContainerRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalDecision, ClientContext } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NH_ASSISTANT_CONFIG } from '../../nh-assistant.config';
import { NhAssistantPanelService } from '../../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../../services/nh-assistant.store';
import { describeNhAssistantClientContext } from '../../services/nh-assistant-page-context';
import { NhAssistantPushService } from '../../services/nh-assistant-push.service';
import { NhAssistantActivityComponent } from '../activity/nh-assistant-activity.component';
import { NhAssistantAgentPickerComponent } from '../agent-picker/nh-assistant-agent-picker.component';
import { NhAssistantComposerComponent } from '../composer/nh-assistant-composer.component';
import { NhAssistantConversationListComponent } from '../conversation-list/nh-assistant-conversation-list.component';
import { NhAssistantPreferencesComponent } from '../preferences/nh-assistant-preferences.component';
import { NhAssistantShareComponent } from '../share/nh-assistant-share.component';
import { NH_ASSISTANT_CONVERSATION_FRAGMENT, NH_ASSISTANT_JOIN_FRAGMENT } from '../../services/nh-assistant-share-link';
import { NhAssistantThreadComponent } from '../thread/nh-assistant-thread.component';

let nextId = 0;

/**
 * The assistant drawer. Place it once in the application layout; it renders nothing in
 * place and appears as an overlay when `NhAssistantPanelService.open()` is called. It opens
 * by itself for an invitation link (`#nh-assistant-join=...`) and for a clicked notification.
 */
@Component({
  selector: 'nh-assistant-panel',
  standalone: true,
  imports: [
    RouterLink,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantActivityComponent,
    NhAssistantAgentPickerComponent,
    NhAssistantComposerComponent,
    NhAssistantConversationListComponent,
    NhAssistantPreferencesComponent,
    NhAssistantShareComponent,
    NhAssistantThreadComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-panel.component.html',
  styleUrl: './nh-assistant-panel.component.scss'
})
export class NhAssistantPanelComponent implements AfterViewInit, OnDestroy {
  readonly store = inject(NhAssistantStore);
  readonly panel = inject(NhAssistantPanelService);
  private readonly push = inject(NhAssistantPushService);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly router = inject(Router, { optional: true });
  private readonly drawer = viewChild.required<TemplateRef<unknown>>('drawer');
  private portal?: TemplatePortal;

  readonly titleId = `nh-assistant-panel-title-${nextId++}`;
  readonly markdown = this.config.markdown?.enabled ?? true;
  readonly showConversations = signal(false);
  readonly showPreferences = signal(false);
  readonly showShare = signal(false);
  readonly adminRoute = this.config.adminRoute ?? null;
  readonly conversation = this.store.activeConversation;
  private readonly conversationId = computed(() => this.conversation()?.id ?? null);
  readonly messages = computed(() => this.conversation()?.messages ?? []);
  readonly closed = computed(() => {
    const status = this.conversation()?.status;
    return status === 'failed' || status === 'archived';
  });
  readonly busy = computed(() => this.store.streaming() || this.conversation()?.status === 'running');
  readonly composerDisabled = computed(() => !this.store.canSend());
  readonly composerStatus = computed<'running' | 'waiting-for-approval' | null>(() => {
    if (this.store.streaming() || this.conversation()?.status === 'running') {
      return 'running';
    }
    return this.conversation()?.status === 'waiting-for-approval' ? 'waiting-for-approval' : null;
  });
  /** Name of another participant whose turn runs or waits, or `null`. */
  readonly activeMemberName = computed(() => {
    const member = this.store.activeMember();
    return member ? member.displayName ?? '' : null;
  });
  /** The owner and the person who started the turn may stop it. */
  readonly canStop = computed(() => this.store.isOwner() || this.store.activeMember() === null);
  readonly shared = computed(() => this.store.members().length > 0);
  readonly canShare = computed(() => this.conversation() !== null && !!this.store.collaboration());
  readonly errorKeys = computed(() => {
    const error = this.store.error();
    return error ? [error.messageKey, `nh-assistant.errors.${error.code}`, 'nh-assistant.errors.generic'] : [];
  });
  readonly noticeKeys = computed(() => {
    const notice = this.store.notice();
    return notice ? [notice.messageKey, `nh-assistant.errors.${notice.code}`, 'nh-assistant.errors.generic'] : [];
  });

  constructor() {
    // Opening, creating or leaving a conversation returns from the list to the thread.
    effect(() => {
      if (this.conversationId() !== undefined) {
        untracked(() => {
          this.showConversations.set(false);
          this.showPreferences.set(false);
          this.showShare.set(false);
        });
      }
    });

    // The open thread counts as seen only while the drawer shows it.
    effect(() => {
      const viewing = this.panel.isOpen() && !this.showConversations() && !this.showPreferences() && !this.showShare();
      untracked(() => this.store.setViewing(viewing));
    });

    // A clicked notification opens its conversation in this window.
    const notifications = this.push.openConversation$.subscribe(conversationId => this.panel.open(conversationId));
    inject(DestroyRef).onDestroy(() => notifications.unsubscribe());

    effect(() => {
      if (this.panel.isOpen()) {
        untracked(() => {
          void this.store.initialize().then(() => this.store.refreshConversations());
          this.refreshPageContext();
        });
      }
    });

    // The drawer stays open while the user navigates; keep the page-context chip current.
    const navigation = this.router?.events.subscribe(event => {
      if (event instanceof NavigationEnd && this.panel.isOpen()) {
        this.refreshPageContext();
      }
    });
    inject(DestroyRef).onDestroy(() => navigation?.unsubscribe());
  }

  ngAfterViewInit(): void {
    this.portal = new TemplatePortal(this.drawer(), this.viewContainerRef);
    this.panel.registerPanel(this.portal);
    this.openFromLocation();
  }

  ngOnDestroy(): void {
    if (this.portal) {
      this.panel.unregisterPanel(this.portal);
    }
  }

  refreshPageContext(): void {
    void this.store.refreshPageContext();
  }

  describeContext(context: ClientContext): string {
    return describeNhAssistantClientContext(context);
  }

  toggleConversations(): void {
    this.showPreferences.set(false);
    this.showShare.set(false);
    this.showConversations.update(show => !show);
  }

  togglePreferences(): void {
    this.showConversations.set(false);
    this.showShare.set(false);
    this.showPreferences.update(show => !show);
  }

  toggleShare(): void {
    this.showConversations.set(false);
    this.showPreferences.set(false);
    this.showShare.update(show => !show);
  }

  newConversation(): void {
    this.store.startNewConversation();
    this.showConversations.set(false);
  }

  openConversation(conversationId: string): void {
    void this.store.openConversation(conversationId);
    this.showConversations.set(false);
  }

  deleteConversation(conversationId: string): void {
    void this.store.deleteConversation(conversationId);
  }

  send(text: string): void {
    this.showConversations.set(false);
    this.showPreferences.set(false);
    this.showShare.set(false);
    void this.store.send(text);
  }

  /**
   * Opens the panel for an invitation link or a clicked notification in the URL fragment and
   * removes the fragment, so a reload or a shared screenshot does not reuse the token.
   */
  private openFromLocation(): void {
    const location = globalThis.location;
    const hash = location?.hash?.replace(/^#/, '') ?? '';
    const separatorIndex = hash.indexOf('=');
    const name = separatorIndex < 0 ? hash : hash.slice(0, separatorIndex);
    const value = separatorIndex < 0 ? '' : hash.slice(separatorIndex + 1);
    if (!value || (name !== NH_ASSISTANT_JOIN_FRAGMENT && name !== NH_ASSISTANT_CONVERSATION_FRAGMENT)) {
      return;
    }

    globalThis.history?.replaceState(globalThis.history.state, '', location.pathname + location.search);
    if (name === NH_ASSISTANT_CONVERSATION_FRAGMENT) {
      this.panel.open(decodeURIComponent(value));
      return;
    }

    const tokenIndex = value.indexOf('.');
    if (tokenIndex <= 0) {
      return;
    }
    const conversationId = decodeURIComponent(value.slice(0, tokenIndex));
    const token = decodeURIComponent(value.slice(tokenIndex + 1));
    this.panel.open();
    void this.store.initialize().then(() => this.store.joinConversation(conversationId, token));
  }

  decide(decision: ApprovalDecision): void {
    this.store.decide(decision);
  }

  cancel(): void {
    void this.store.cancel();
  }
}
