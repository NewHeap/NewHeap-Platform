import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentRunHistoryItem,
  NhAssistantAgentRunApiService,
  ɵNhAssistantIconComponent as NhAssistantIconComponent
} from '@newheap/platform-ai-chat';
import { NhAssistantRunResultComponent } from '../nh-assistant-run-result.component';

/** How many runs the history of a board tile reads at a time. */
const pageSize = 5;

let nextId = 0;

/**
 * The runs of one agent for one subject of the whole application, newest first, as a board tile shows
 * them (internal): status, time and the beginning of each result, the whole result on demand in
 * `nh-assistant-run-result`, and more runs on request. The server's people's history (`GET runs`)
 * decides what the reader sees.
 */
@Component({
  selector: 'nh-assistant-board-history',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantIconComponent, NhAssistantRunResultComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="board-history" [attr.aria-labelledby]="id + '-title'" [attr.aria-busy]="loading()">
      <h4 [id]="id + '-title'">{{ 'nh-assistant.agent-runs.board.history-title' | translate }}</h4>
      @if (loading() && runs().length === 0) {
        <p class="muted" role="status">{{ 'nh-assistant.agent-runs.board.history-loading' | translate }}</p>
      } @else if (loadFailed() && runs().length === 0) {
        <div class="state" role="alert">
          <p>{{ 'nh-assistant.agent-runs.load-failed' | translate }}</p>
          <button class="secondary" type="button" (click)="load()">{{ 'nh-assistant.agent-runs.retry' | translate }}</button>
        </div>
      } @else if (runs().length === 0) {
        <p class="muted" data-board-history-empty>{{ 'nh-assistant.agent-runs.board.history-empty' | translate }}</p>
      } @else {
        <ol class="history-runs">
          @for (run of runs(); track run.id) {
            <li class="history-run" [attr.data-board-history-run]="run.id">
              <div class="meta">
                <span
                  class="badge"
                  [class.success]="run.status === 'completed'"
                  [class.danger]="run.status === 'failed'"
                  [class.warning]="run.status === 'waiting-for-approval'">{{ 'nh-assistant.agent-runs.run.status.' + run.status | translate }}</span>
                <time [attr.datetime]="timeOf(run)">{{ timeOf(run) | date: 'medium' }}</time>
              </div>
              @if (run.result; as result) {
                <p class="summary result">{{ result }}@if (run.resultTruncated) {…}</p>
              }
              <div class="card-actions">
                <button
                  class="text-button"
                  type="button"
                  [attr.aria-expanded]="shown() === run.id"
                  [attr.aria-controls]="id + '-' + run.id"
                  (click)="toggle(run.id)">
                  {{ (shown() === run.id ? 'nh-assistant.agent-runs.result.hide' : 'nh-assistant.agent-runs.result.show') | translate }}
                </button>
              </div>
              @if (shown() === run.id) {
                <div class="work" [id]="id + '-' + run.id">
                  <nh-assistant-run-result [runId]="run.id" (openConversation)="openConversation.emit($event)" />
                </div>
              }
            </li>
          }
        </ol>
        @if (runs().length < total()) {
          <button class="secondary" type="button" data-board-history-more [disabled]="loading()" (click)="more()">
            <nh-assistant-icon name="list" />{{ 'nh-assistant.agent-runs.board.history-more' | translate }}
          </button>
        } @else if (limited()) {
          <p class="muted">{{ 'nh-assistant.agent-runs.board.history-limited' | translate }}</p>
        }
      }
    </section>
  `,
  styleUrl: '../nh-assistant-agent-runs.scss'
})
export class NhAssistantBoardHistoryComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private subscription?: Subscription;
  private page = 1;

  readonly agentId = input.required<string>();
  readonly subjectType = input.required<string>();
  readonly subjectId = input.required<string>();
  /** Emits the conversation a shown result offers to open. */
  readonly openConversation = output<string>();

  readonly id = `nh-assistant-board-history-${nextId++}`;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly runs = signal<AgentRunHistoryItem[]>([]);
  readonly total = signal(0);
  readonly limited = signal(false);
  /** The run whose whole result is shown; one at a time. */
  readonly shown = signal<string | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.agentId();
      this.subjectType();
      this.subjectId();
      untracked(() => this.load());
    });
  }

  load(): void {
    this.page = 1;
    this.shown.set(null);
    this.fetch(false);
  }

  more(): void {
    this.page++;
    this.fetch(true);
  }

  toggle(runId: string): void {
    this.shown.update(current => current === runId ? null : runId);
  }

  /** When the run ended, or started, or was queued. */
  timeOf(run: AgentRunHistoryItem): string {
    return run.endedAt ?? run.startedAt ?? run.createdAt;
  }

  private fetch(append: boolean): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api
      .getRunHistory({
        agentId: this.agentId(),
        subjectType: this.subjectType(),
        subjectId: this.subjectId(),
        page: this.page,
        itemsPerPage: pageSize
      })
      .subscribe({
        next: history => {
          this.runs.update(runs => append ? [...runs, ...history.items.filter(item => !runs.some(run => run.id === item.id))] : history.items);
          this.total.set(history.total);
          this.limited.set(history.limited);
          this.loading.set(false);
        },
        error: () => {
          if (append) {
            this.page--;
          }
          this.loading.set(false);
          this.loadFailed.set(true);
        }
      });
  }
}
