import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  ClientContext,
  NhAssistantLauncherComponent,
  NhAssistantPanelComponent,
  NhAssistantPanelService,
  NhAssistantStore
} from '@newheap/platform-ai-chat';
import { NhAssistantMockBackend } from '@newheap/platform-ai-chat/testing';
import { RouterLink } from '@angular/router';
import { NhAssistantAgentBoardComponent } from '@newheap/platform-ai-chat/agent-runs';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { AssistantPlaygroundAccess } from './assistant-playground-access';
import {
  ASSISTANT_PLAYGROUND_ADMIN_ROUTE,
  ASSISTANT_PLAYGROUND_PROMPTS,
  PLAYGROUND_CHANGED_REMOTE_TOOLS,
  PLAYGROUND_COLLEAGUE,
  PLAYGROUND_COLLEAGUE_MESSAGE,
  PLAYGROUND_PROJECT_PAGES,
  PLAYGROUND_MCP_SERVER_ID,
  PROJECT_ASSISTANT_ID
} from './assistant-playground.scenario';
import { SampleAssistantPageContext } from './sample-assistant.config';

/** SPM-369: how much of the unsent message the lab's card quotes. */
const unsentPreviewLength = 80;

/**
 * Executable evidence for the assistant panel: a scripted mock API, the feature flag and
 * the access policy as switches, prompts for every scripted turn and the live store state.
 * Prompts start a new conversation while another one still runs, so several conversations
 * work at the same time, and a simulated colleague continues a shared conversation live. A prepared
 * status brief opens the assistant with the brief in the composer for the person to review (SPM-350),
 * and what the person typed and did not send waits in the composer when the drawer opens again
 * (SPM-369).
 */
@Component({
  selector: 'app-assistant-playground',
  standalone: true,
  imports: [RouterLink, TranslateModule, NhAssistantLauncherComponent, NhAssistantPanelComponent, NhAssistantAgentBoardComponent],
  templateUrl: './assistant-playground.component.html',
  styleUrl: './assistant-playground.component.scss'
})
export class AssistantPlaygroundComponent {
  private readonly backend = inject(NhAssistantMockBackend);
  private readonly access = inject(AssistantPlaygroundAccess);
  private readonly translate = inject(TranslateService);

  readonly store = inject(NhAssistantStore);
  readonly panel = inject(NhAssistantPanelService);
  readonly prompts = ASSISTANT_PLAYGROUND_PROMPTS;
  readonly featureEnabled = this.backend.enabled;
  readonly adminGranted = this.backend.canAdminister;
  readonly adminRoute = ASSISTANT_PLAYGROUND_ADMIN_ROUTE;
  readonly schemaChangeSimulated = signal(false);
  readonly projectPages = PLAYGROUND_PROJECT_PAGES;
  readonly pageContext = inject(SampleAssistantPageContext);
  readonly colleague = PLAYGROUND_COLLEAGUE;
  readonly colleagueBusy = signal(false);
  readonly canSimulateColleague = computed(() =>
    !this.colleagueBusy() && this.store.activeConversation()?.status === 'idle' && !this.store.streaming());
  /** SPM-350: whether the prepared brief replaces what the person already typed in the composer. */
  readonly replaceDraft = signal(false);
  /**
   * SPM-369: what the composer holds and has not sent in the open conversation, as the store keeps it
   * in this tab's memory, shortened for the card. Closing the drawer keeps it; sending it and switching
   * the access permission off, as signing out does, forget it.
   */
  readonly unsentPreview = computed(() => {
    const unsent = this.store.unsentMessage();
    if (!unsent) {
      return null;
    }

    const text = unsent.text.trim();
    return {
      prepared: unsent.prepared,
      count: unsent.text.length,
      text: text.length > unsentPreviewLength ? `${text.slice(0, unsentPreviewLength)}…` : text
    };
  });

  constructor() {
    // Leaving the page closes the simulated project page, as a real page would on destroy.
    inject(DestroyRef).onDestroy(() => this.pageContext.clear());
  }

  /** Simulates opening a project page: the next message sends this project as page context. */
  openProjectPage(page: ClientContext): void {
    this.pageContext.set(page);
    void this.store.refreshPageContext();
  }

  closeProjectPage(): void {
    this.pageContext.clear();
    void this.store.refreshPageContext();
  }
  readonly accessGranted = toSignal(this.access.granted, { initialValue: true });
  readonly conversationStatus = computed(() => this.store.activeConversation()?.status ?? null);

  async setFeatureEnabled(enabled: boolean): Promise<void> {
    this.backend.setEnabled(enabled);
    await this.store.reloadStatus();
    if (!enabled) {
      this.panel.close();
    }
  }

  setAccessGranted(granted: boolean): void {
    this.access.granted.next(granted);
    if (!granted) {
      this.panel.close();
    }
  }

  /**
   * Sends a prompt. When the open conversation is busy, the prompt starts a new conversation
   * and the other one keeps running in the background.
   */
  async tryPrompt(text: string): Promise<void> {
    this.panel.open();
    await this.store.initialize();
    if (this.store.streaming() || (this.store.activeConversation()?.status ?? 'idle') !== 'idle') {
      this.store.startNewConversation();
    }
    await this.store.send(text);
  }

  /** Runs one long answer and queues two follow-ups in the same conversation. */
  async tryMessageQueue(): Promise<void> {
    this.panel.open();
    await this.store.initialize();
    this.store.startNewConversation();
    if (await this.store.submit('Write a long status report for all projects.')) {
      await this.store.submit('What am I looking at?');
      await this.store.submit('Which projects are active?');
    }
  }

  /**
   * SPM-350: opens the assistant in a new chat with the project assistant and a prepared status brief
   * in the composer, about the open project page or else the whole portfolio, in the person's
   * language. Nothing is sent: the person reviews the brief, changes it and sends it. A brief keeps
   * what the person already typed unless "Replace what I typed" is on.
   */
  draftStatusBrief(): void {
    const page = this.pageContext.value();
    const project = page?.entities?.[0];
    const draft = project
      ? this.translate.instant('project.assistant-draft-project-brief', { name: page?.title ?? project.id, key: project.id })
      : this.translate.instant('project.assistant-draft-portfolio-brief');
    this.panel.open({ agentId: PROJECT_ASSISTANT_ID, draft, replaceDraft: this.replaceDraft() });
  }

  onReplaceDraftToggle(event: Event): void {
    this.replaceDraft.set((event.target as HTMLInputElement).checked);
  }

  /** The colleague joins the open conversation and asks the assistant something; it arrives live. */
  async simulateColleague(): Promise<void> {
    const conversationId = this.store.activeConversation()?.id;
    if (!conversationId || !this.canSimulateColleague()) {
      return;
    }
    this.colleagueBusy.set(true);
    try {
      await this.backend.simulateParticipantTurn(conversationId, PLAYGROUND_COLLEAGUE, PLAYGROUND_COLLEAGUE_MESSAGE);
    } finally {
      this.colleagueBusy.set(false);
    }
  }

  async setAdminGranted(granted: boolean): Promise<void> {
    this.backend.setCanAdminister(granted);
    await this.store.reloadStatus();
  }

  /** Changes the input schema of one remote tool; the next sync disables that tool. */
  simulateSchemaChange(): void {
    this.backend.setRemoteTools(PLAYGROUND_MCP_SERVER_ID, PLAYGROUND_CHANGED_REMOTE_TOOLS);
    this.schemaChangeSimulated.set(true);
  }

  onAdminToggle(event: Event): void {
    void this.setAdminGranted((event.target as HTMLInputElement).checked);
  }

  onFeatureToggle(event: Event): void {
    void this.setFeatureEnabled((event.target as HTMLInputElement).checked);
  }

  onAccessToggle(event: Event): void {
    this.setAccessGranted((event.target as HTMLInputElement).checked);
  }
}
