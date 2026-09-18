import { TemplatePortal } from '@angular/cdk/portal';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
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
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalDecision } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NH_ASSISTANT_CONFIG } from '../../nh-assistant.config';
import { NhAssistantPanelService } from '../../services/nh-assistant-panel.service';
import { NhAssistantStore } from '../../services/nh-assistant.store';
import { NhAssistantAgentPickerComponent } from '../agent-picker/nh-assistant-agent-picker.component';
import { NhAssistantComposerComponent } from '../composer/nh-assistant-composer.component';
import { NhAssistantConversationListComponent } from '../conversation-list/nh-assistant-conversation-list.component';
import { NhAssistantPreferencesComponent } from '../preferences/nh-assistant-preferences.component';
import { NhAssistantThreadComponent } from '../thread/nh-assistant-thread.component';

let nextId = 0;

/**
 * The assistant drawer. Place it once in the application layout; it renders nothing in
 * place and appears as an overlay when `NhAssistantPanelService.open()` is called.
 */
@Component({
  selector: 'nh-assistant-panel',
  standalone: true,
  imports: [
    RouterLink,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAgentPickerComponent,
    NhAssistantComposerComponent,
    NhAssistantConversationListComponent,
    NhAssistantPreferencesComponent,
    NhAssistantThreadComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-panel.component.html',
  styleUrl: './nh-assistant-panel.component.scss'
})
export class NhAssistantPanelComponent implements AfterViewInit, OnDestroy {
  readonly store = inject(NhAssistantStore);
  readonly panel = inject(NhAssistantPanelService);
  private readonly config = inject(NH_ASSISTANT_CONFIG);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private readonly drawer = viewChild.required<TemplateRef<unknown>>('drawer');
  private portal?: TemplatePortal;

  readonly titleId = `nh-assistant-panel-title-${nextId++}`;
  readonly markdown = this.config.markdown?.enabled ?? true;
  readonly showConversations = signal(false);
  readonly showPreferences = signal(false);
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
  readonly errorKeys = computed(() => {
    const error = this.store.error();
    return error ? [error.messageKey, `nh-assistant.errors.${error.code}`, 'nh-assistant.errors.generic'] : [];
  });

  constructor() {
    // Opening, creating or leaving a conversation returns from the list to the thread.
    effect(() => {
      if (this.conversationId() !== undefined) {
        untracked(() => {
          this.showConversations.set(false);
          this.showPreferences.set(false);
        });
      }
    });

    effect(() => {
      if (this.panel.isOpen()) {
        untracked(() => {
          void this.store.initialize().then(() => this.store.refreshConversations());
        });
      }
    });
  }

  ngAfterViewInit(): void {
    this.portal = new TemplatePortal(this.drawer(), this.viewContainerRef);
    this.panel.registerPanel(this.portal);
  }

  ngOnDestroy(): void {
    if (this.portal) {
      this.panel.unregisterPanel(this.portal);
    }
  }

  toggleConversations(): void {
    this.showPreferences.set(false);
    this.showConversations.update(show => !show);
  }

  togglePreferences(): void {
    this.showConversations.set(false);
    this.showPreferences.update(show => !show);
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
    void this.store.send(text);
  }

  decide(decision: ApprovalDecision): void {
    this.store.decide(decision);
  }

  cancel(): void {
    void this.store.cancel();
  }
}
