import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import {
  AgentRun,
  AgentRunRetryMode,
  AgentSubjectRole,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

const endedStatuses = new Set(['completed', 'failed', 'cancelled']);

/**
 * The runs of an agent thread, newest first: trigger, status, failure, usage, proposals and the
 * summary the next run starts from. Participants cancel an open run; approvers resume or restart
 * a failed or cancelled one.
 */
@Component({
  selector: 'nh-assistant-run-timeline',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-run-timeline.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantRunTimelineComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private subscription?: Subscription;
  private actionSubscription?: Subscription;

  readonly conversationId = input.required<string>();
  readonly role = input<AgentSubjectRole>('reader');
  /** Emits after a run was cancelled or retried. */
  readonly changed = output<void>();

  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly runs = signal<AgentRun[]>([]);
  readonly busyRunId = signal<string | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly canCancel = computed(() => this.role() === 'participant' || this.role() === 'approver');
  readonly canRetry = computed(() => this.role() === 'approver');

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.actionSubscription?.unsubscribe();
    });
    effect(() => {
      this.conversationId();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api.getRuns(this.conversationId()).subscribe({
      next: list => {
        this.runs.set(list.items);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  isOpen(run: AgentRun): boolean {
    return !endedStatuses.has(run.status);
  }

  canRetryRun(run: AgentRun): boolean {
    return this.canRetry() && (run.status === 'failed' || run.status === 'cancelled');
  }

  cancel(run: AgentRun): void {
    this.act(run, this.api.cancelRun(run.id));
  }

  retry(run: AgentRun, mode: AgentRunRetryMode): void {
    this.act(run, this.api.retryRun(run.id, mode));
  }

  private act(run: AgentRun, request: Observable<unknown>): void {
    this.busyRunId.set(run.id);
    this.error.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = request.subscribe({
      next: () => {
        this.busyRunId.set(null);
        this.changed.emit();
        this.load();
      },
      error: failure => {
        this.busyRunId.set(null);
        this.error.set(toNhAssistantError(failure));
      }
    });
  }
}
