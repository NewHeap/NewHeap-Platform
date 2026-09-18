import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  NhAssistantLauncherComponent,
  NhAssistantPanelComponent,
  NhAssistantPanelService,
  NhAssistantStore
} from '@newheap/platform-ai-chat';
import { NhAssistantMockBackend } from '@newheap/platform-ai-chat/testing';
import { TranslateModule } from '@ngx-translate/core';
import { AssistantPlaygroundAccess } from './assistant-playground-access';
import { ASSISTANT_PLAYGROUND_PROMPTS } from './assistant-playground.scenario';

/**
 * Executable evidence for the assistant panel: a scripted mock API, the feature flag and
 * the access policy as switches, prompts for every scripted turn and the live store state.
 */
@Component({
  selector: 'app-assistant-playground',
  standalone: true,
  imports: [TranslateModule, NhAssistantLauncherComponent, NhAssistantPanelComponent],
  templateUrl: './assistant-playground.component.html',
  styleUrl: './assistant-playground.component.scss'
})
export class AssistantPlaygroundComponent {
  private readonly backend = inject(NhAssistantMockBackend);
  private readonly access = inject(AssistantPlaygroundAccess);

  readonly store = inject(NhAssistantStore);
  readonly panel = inject(NhAssistantPanelService);
  readonly prompts = ASSISTANT_PLAYGROUND_PROMPTS;
  readonly featureEnabled = this.backend.enabled;
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

  async tryPrompt(text: string): Promise<void> {
    this.panel.open();
    await this.store.initialize();
    if (this.store.activeConversation()?.status !== 'idle') {
      this.store.startNewConversation();
    }
    await this.store.send(text);
  }

  onFeatureToggle(event: Event): void {
    void this.setFeatureEnabled((event.target as HTMLInputElement).checked);
  }

  onAccessToggle(event: Event): void {
    this.setAccessGranted((event.target as HTMLInputElement).checked);
  }
}
