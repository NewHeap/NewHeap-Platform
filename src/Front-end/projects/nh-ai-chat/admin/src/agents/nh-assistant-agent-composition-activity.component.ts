import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentCompositionActivity,
  AgentCompositionCall,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { startNhAssistantAdminTranslations } from '../i18n/nh-assistant-admin-translations';

/** Periods the activity offers, in days before now. */
export type NhAssistantCompositionPeriod = 7 | 30 | 90;

const dayMs = 24 * 60 * 60 * 1000;

let nextId = 0;

/**
 * What an agent's compositions and scripts ran, for its administrators (TC1): per tool the calls, the
 * tool calls they ran, the model rounds they saved and the size of the results returned to the model,
 * and the latest calls with their tool calls in order and how each ended. It shows no arguments or
 * results. The server requires the assistant admin policy.
 */
@Component({
  selector: 'nh-assistant-agent-composition-activity',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-composition-activity.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './nh-assistant-agent-composition-activity.component.scss']
})
export class NhAssistantAgentCompositionActivityComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;

  readonly agentId = input.required<string>();
  /** How many of the latest calls the list shows. */
  readonly shownCalls = input(20);

  readonly id = `nh-assistant-agent-compositions-${nextId++}`;
  readonly periods: readonly NhAssistantCompositionPeriod[] = [7, 30, 90];
  readonly period = signal<NhAssistantCompositionPeriod>(30);
  readonly loading = signal(true);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly activity = signal<AgentCompositionActivity | null>(null);
  readonly calls = computed<AgentCompositionCall[]>(() => (this.activity()?.calls ?? []).slice(0, this.shownCalls()));
  readonly codeKeys = nhAssistantCodeKeys;

  constructor() {
    startNhAssistantAdminTranslations();

    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.agentId();
      untracked(() => this.load());
    });
  }

  load(): void {
    const to = Date.now();
    const range = { from: new Date(to - this.period() * dayMs).toISOString(), to: new Date(to).toISOString() };
    this.loading.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin.getAgentCompositionActivity(this.agentId(), range).subscribe({
      next: activity => {
        this.activity.set(activity);
        this.loading.set(false);
      },
      error: failure => {
        this.error.set(toNhAssistantError(failure));
        this.loading.set(false);
      }
    });
  }

  onPeriod(event: Event): void {
    this.period.set(Number((event.target as HTMLSelectElement).value) as NhAssistantCompositionPeriod);
    this.load();
  }

  /** The badge class of a tool call status. */
  statusClass(status: string): string {
    switch (status) {
      case 'succeeded':
        return 'badge success';
      case 'failed':
      case 'rejected':
        return 'badge danger';
      case 'awaiting-approval':
      case 'running':
        return 'badge warning';
      default:
        return 'badge';
    }
  }
}
