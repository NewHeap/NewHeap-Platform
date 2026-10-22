import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentConsultation,
  NhAssistantAgentRunApiService,
  NhAssistantConsultationRoute,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';

/** The routes in the order the list names them. */
const routeOrder: readonly NhAssistantConsultationRoute[] = ['asker-decides', 'subject-agent-rights', 'subject-asker-rights'];

/**
 * Who consulted the thread's agent through another assistant, newest first: the person whose rights
 * were used, the asking assistant, the outcome, the usage and the proposals it left, with their
 * number per route. The question and the answer stay in the asking conversation and are never shown
 * here. With live updates the list reloads by itself when a run or proposal of the thread changes.
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
  readonly codeKeys = nhAssistantCodeKeys;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.conversationId();
      untracked(() => this.load());
    });
    nhAssistantReloadOnAgentWork(
      change => change.conversationId === this.conversationId() && change.reason !== 'feedback',
      () => this.fetch(!this.loading() && !this.loadFailed())
    );
  }

  load(): void {
    this.fetch(false);
  }

  /** The proposals the consultation left per route, in a fixed order; routes without proposals are left out. */
  routeCounts(consultation: AgentConsultation): { route: NhAssistantConsultationRoute; count: number }[] {
    const byRoute = consultation.proposalsByRoute ?? {};
    return routeOrder
      .map(route => ({ route, count: byRoute[route] ?? 0 }))
      .filter(item => item.count > 0);
  }

  /** A quiet load, after a live update, shows no loading state and keeps the list when it fails. */
  private fetch(quiet: boolean): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
    }

    this.subscription?.unsubscribe();
    this.subscription = this.api.getConsultations(this.conversationId()).subscribe({
      next: list => {
        this.consultations.set(list.items);
        this.total.set(list.total);
        this.loading.set(false);
        this.loadFailed.set(false);
      },
      error: () => {
        if (quiet) {
          return;
        }

        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }
}
