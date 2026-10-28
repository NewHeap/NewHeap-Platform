import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentCompositionFeatureStatus,
  AgentCompositionStatus,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { startNhAssistantAdminTranslations } from '../../i18n/nh-assistant-admin-translations';

let nextId = 0;

/**
 * What the assistant actually does with compositions and the script tool for one agent now, per
 * feature, and why: on, not installed, not allowed by code or for agents created here, off for the
 * agent, or off by the application. Where the application's switch decides, it shows every channel.
 * It reads `GET admin/agents/{id}/compositions` whenever the agent changes.
 */
@Component({
  selector: 'nh-assistant-agent-compositions',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-compositions.component.html',
  styleUrls: ['../../nh-assistant-admin-shared.scss', './agent-page.scss']
})
export class NhAssistantAgentCompositionsComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;

  /** The agent whose state is shown. */
  readonly agentId = input.required<string>();
  /** The agent's version: a saved change reads the state again. */
  readonly version = input<number>(0);

  readonly id = `nh-assistant-agent-compositions-${nextId++}`;
  readonly status = signal<AgentCompositionStatus | null>(null);
  readonly loading = signal(false);
  readonly error = signal<NhAssistantError | null>(null);

  constructor() {
    startNhAssistantAdminTranslations();
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      const agentId = this.agentId();
      this.version();
      untracked(() => this.load(agentId));
    });
  }

  /** Reads the state again, for example after a failure. */
  reload(): void {
    this.load(this.agentId());
  }

  /** The text of why the feature is on or off; not installed names the feature. */
  reasonKey(feature: AgentCompositionFeatureStatus): string {
    const reason = feature.reason === 'not-installed' ? `not-installed-${feature.feature}` : feature.reason;
    return `nh-assistant.admin.agent-page.compositions.reasons.${reason}`;
  }

  /** Whether the channels differ or the switch decided, so each channel is worth showing. */
  showsChannels(feature: AgentCompositionFeatureStatus): boolean {
    return feature.reason === 'on' || feature.reason === 'off-by-application';
  }

  errorKeys(): string[] {
    return nhAssistantErrorKeys(this.error());
  }

  private load(agentId: string): void {
    this.subscription?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    this.subscription = this.admin.getAgentCompositionStatus(agentId).subscribe({
      next: status => {
        this.loading.set(false);
        this.status.set(status);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.status.set(null);
        this.error.set(toNhAssistantError(error));
      }
    });
  }
}
