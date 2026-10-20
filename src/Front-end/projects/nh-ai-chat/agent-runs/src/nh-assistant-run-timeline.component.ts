import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import {
  AgentRun,
  AgentRunRetryMode,
  AgentSubjectRole,
  AgentTriggerReceipt,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantCodeKeys as nhAssistantCodeKeys,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantRunReceiptComponent } from './internal/nh-assistant-run-receipt.component';
import { NhAssistantRunResultComponent } from './nh-assistant-run-result.component';

let nextId = 0;

const endedStatuses = new Set(['completed', 'failed', 'cancelled']);

/**
 * The runs of an agent thread, newest first: trigger, status, failure, usage, proposals and the
 * summary the next run starts from, and the agent that handed a run its work. "Show the result"
 * opens a run's whole result, proposals and files in `nh-assistant-run-result`. Participants cancel
 * an open run; approvers resume or restart a failed or cancelled one, and a restart shows what became
 * of it like a start by hand. A run's failure reads as its translated code. With live updates it
 * reloads by itself when a run or proposal of the thread changes.
 */
@Component({
  selector: 'nh-assistant-run-timeline',
  standalone: true,
  imports: [
    DatePipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantRunResultComponent,
    NhAssistantRunReceiptComponent
  ],
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
  /** Emits the thread of the agent that handed a run its work, so the host can open it. */
  readonly openConversation = output<string>();

  readonly id = `nh-assistant-run-timeline-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly runs = signal<AgentRun[]>([]);
  /** The run whose result is shown; one at a time. */
  readonly openedRunId = signal<string | null>(null);
  readonly busyRunId = signal<string | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** What became of the latest restart. */
  readonly receipt = signal<AgentTriggerReceipt | null>(null);
  readonly codeKeys = nhAssistantCodeKeys;
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
    nhAssistantReloadOnAgentWork(
      change => change.conversationId === this.conversationId() && change.reason !== 'feedback',
      () => this.fetch(!this.loading() && !this.loadFailed())
    );
  }

  load(): void {
    this.fetch(false);
  }

  toggleResult(run: AgentRun): void {
    this.openedRunId.update(current => current === run.id ? null : run.id);
  }

  isOpen(run: AgentRun): boolean {
    return !endedStatuses.has(run.status);
  }

  canRetryRun(run: AgentRun): boolean {
    return this.canRetry() && (run.status === 'failed' || run.status === 'cancelled');
  }

  cancel(run: AgentRun): void {
    this.act(run, this.api.cancelRun(run.id), () => undefined);
  }

  retry(run: AgentRun, mode: AgentRunRetryMode): void {
    // A restart starts the agent again; its receipt says whether it started or waits.
    this.act(run, this.api.retryRun(run.id, mode), receipt => {
      if (mode === 'restart') {
        this.receipt.set(receipt);
      }
    });
  }

  private act<T>(run: AgentRun, request: Observable<T>, done: (result: T) => void): void {
    this.busyRunId.set(run.id);
    this.error.set(null);
    this.receipt.set(null);
    this.actionSubscription?.unsubscribe();
    this.actionSubscription = request.subscribe({
      next: result => {
        this.busyRunId.set(null);
        done(result);
        this.changed.emit();
        this.load();
      },
      error: failure => {
        this.busyRunId.set(null);
        this.error.set(toNhAssistantError(failure));
      }
    });
  }

  /** A quiet load, after a live update, shows no loading state and keeps the runs when it fails. */
  private fetch(quiet: boolean): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
    }

    this.subscription?.unsubscribe();
    this.subscription = this.api.getRuns(this.conversationId()).subscribe({
      next: list => {
        this.runs.set(list.items);
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
