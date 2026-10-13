import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentConsultation,
  NhAssistantAgentRunApiService,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';

/**
 * Who consulted the thread's agent through another assistant, newest first: the person whose rights
 * were used, the asking assistant, the outcome, the usage and the proposals left for approvers. The
 * question and the answer stay in the asking conversation and are never shown here.
 */
@Component({
  selector: 'nh-assistant-agent-consultations',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-consultations.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantAgentConsultationsComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private subscription?: Subscription;

  readonly conversationId = input.required<string>();

  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly consultations = signal<AgentConsultation[]>([]);
  readonly total = signal(0);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.conversationId();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api.getConsultations(this.conversationId()).subscribe({
      next: list => {
        this.consultations.set(list.items);
        this.total.set(list.total);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }
}
