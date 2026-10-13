import { DatePipe, DOCUMENT, PercentPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentFeedbackCategoryCount,
  AgentFeedbackItem,
  AgentFeedbackSummaryRow,
  AgentProposalFeedbackCategory,
  AgentProposalFeedbackGroup,
  NH_ASSISTANT_FEEDBACK_CATEGORIES,
  NhAssistantAdminApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/** Periods the report offers, in days before now. */
export type NhAssistantFeedbackPeriod = 7 | 30 | 90;

/** A summary row with its most frequent feedback categories. */
interface NhAssistantFeedbackSummaryView extends AgentFeedbackSummaryRow {
  topCategories: AgentFeedbackCategoryCount[];
}

const dayMs = 24 * 60 * 60 * 1000;
const topCategoryCount = 3;

let nextId = 0;

/**
 * What people said when they rejected an agent's proposals, for the agent's administrators: per
 * tool and agent version the decisions, the rejection rate, the share of rejections with feedback,
 * the number of different people and the most frequent categories, followed by the feedback itself
 * and a JSON Lines export. Comparing agent versions shows whether a change to the prompt, tools or
 * policy helped. The server requires the assistant admin policy and reports only the caller's scopes.
 */
@Component({
  selector: 'nh-assistant-agent-feedback-report',
  standalone: true,
  imports: [DatePipe, PercentPipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-feedback-report.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './nh-assistant-agent-feedback-report.component.scss']
})
export class NhAssistantAgentFeedbackReportComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly document = inject(DOCUMENT);
  private summarySubscription?: Subscription;
  private listSubscription?: Subscription;

  readonly agentId = input.required<string>();
  /** Feedback per page; the server allows at most 100. */
  readonly itemsPerPage = input(20);

  readonly id = `nh-assistant-agent-feedback-${nextId++}`;
  readonly periods: readonly NhAssistantFeedbackPeriod[] = [7, 30, 90];
  readonly categories = NH_ASSISTANT_FEEDBACK_CATEGORIES;

  readonly period = signal<NhAssistantFeedbackPeriod>(30);
  readonly toolId = signal('');
  readonly category = signal<AgentProposalFeedbackCategory | ''>('');
  readonly page = signal(1);

  readonly summaryLoading = signal(true);
  readonly summaryError = signal<NhAssistantError | null>(null);
  readonly summaryErrorKeys = computed(() => nhAssistantErrorKeys(this.summaryError()));
  readonly rows = signal<AgentFeedbackSummaryRow[]>([]);

  readonly listLoading = signal(true);
  readonly listError = signal<NhAssistantError | null>(null);
  readonly listErrorKeys = computed(() => nhAssistantErrorKeys(this.listError()));
  readonly items = signal<AgentFeedbackItem[]>([]);
  readonly total = signal(0);
  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.itemsPerPage())));

  readonly exporting = signal(false);
  readonly exportError = signal<NhAssistantError | null>(null);
  readonly exportErrorKeys = computed(() => nhAssistantErrorKeys(this.exportError()));
  readonly canExport = computed(() => !this.exporting() && !this.listLoading() && this.listError() === null && this.total() > 0);

  /** Tools with proposals in the period, plus the chosen tool when the period has none of it. */
  readonly tools = computed(() => {
    const tools = new Set(this.rows().map(row => row.toolId));
    if (this.toolId()) {
      tools.add(this.toolId());
    }
    return [...tools].sort();
  });
  readonly visibleRows = computed<NhAssistantFeedbackSummaryView[]>(() => this.rows()
    .filter(row => !this.toolId() || row.toolId === this.toolId())
    .map(row => ({
      ...row,
      topCategories: [...row.categories].sort((left, right) => right.count - left.count).slice(0, topCategoryCount)
    })));
  readonly filtered = computed(() => this.toolId() !== '' || this.category() !== '');

  /** The period the last load asked for, so the list, summary and export agree. */
  private range = periodRange(30);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.summarySubscription?.unsubscribe();
      this.listSubscription?.unsubscribe();
    });
    effect(() => {
      this.agentId();
      untracked(() => {
        this.toolId.set('');
        this.category.set('');
        this.load();
      });
    });
  }

  /** Reloads the summary and the first page of feedback for the chosen period. */
  load(): void {
    this.range = periodRange(this.period());
    this.page.set(1);
    this.loadSummary();
    this.loadList();
  }

  onPeriod(event: Event): void {
    this.period.set(Number((event.target as HTMLSelectElement).value) as NhAssistantFeedbackPeriod);
    this.load();
  }

  onTool(event: Event): void {
    this.toolId.set((event.target as HTMLSelectElement).value);
    this.page.set(1);
    this.loadList();
  }

  onCategory(event: Event): void {
    this.category.set((event.target as HTMLSelectElement).value as AgentProposalFeedbackCategory | '');
    this.page.set(1);
    this.loadList();
  }

  goTo(page: number): void {
    this.page.set(Math.min(Math.max(1, page), this.pages()));
    this.loadList();
  }

  loadSummary(): void {
    this.summaryLoading.set(true);
    this.summaryError.set(null);
    this.summarySubscription?.unsubscribe();
    this.summarySubscription = this.admin.getAgentFeedbackSummary(this.agentId(), this.range).subscribe({
      next: summary => {
        this.rows.set(summary.rows);
        this.summaryLoading.set(false);
      },
      error: (failure: unknown) => {
        this.rows.set([]);
        this.summaryLoading.set(false);
        this.summaryError.set(toNhAssistantError(failure));
      }
    });
  }

  loadList(): void {
    this.listLoading.set(true);
    this.listError.set(null);
    this.exportError.set(null);
    this.listSubscription?.unsubscribe();
    this.listSubscription = this.admin
      .getAgentFeedback(this.agentId(), {
        ...this.range,
        toolId: this.toolId() || undefined,
        category: this.category() || undefined,
        page: this.page(),
        itemsPerPage: this.itemsPerPage()
      })
      .subscribe({
        next: list => {
          this.items.set(list.items);
          this.total.set(list.total);
          this.listLoading.set(false);
        },
        error: (failure: unknown) => {
          this.items.set([]);
          this.total.set(0);
          this.listLoading.set(false);
          this.listError.set(toNhAssistantError(failure));
        }
      });
  }

  /** Downloads the feedback of the period and filters as a JSON Lines file. */
  async export(): Promise<void> {
    if (!this.canExport()) {
      return;
    }

    this.exporting.set(true);
    this.exportError.set(null);
    try {
      const blob = await this.admin.exportAgentFeedback(this.agentId(), {
        ...this.range,
        toolId: this.toolId() || undefined,
        category: this.category() || undefined
      });
      const url = URL.createObjectURL(blob);
      const link = this.document.createElement('a');
      link.href = url;
      link.download = `agent-feedback-${this.agentId()}.jsonl`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (failure) {
      this.exportError.set(toNhAssistantError(failure));
    } finally {
      this.exporting.set(false);
    }
  }

  /** Badge color of a feedback group: wrong and not allowed are failures, not now is a warning. */
  groupClass(group: AgentProposalFeedbackGroup): string {
    switch (group) {
      case 'wrong':
      case 'not-allowed':
        return 'danger';
      case 'right-but-not-now':
        return 'warning';
      default:
        return '';
    }
  }
}

function periodRange(days: number): { from: string; to: string } {
  const to = Date.now();
  return { from: new Date(to - days * dayMs).toISOString(), to: new Date(to).toISOString() };
}
