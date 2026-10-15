import { DatePipe, DecimalPipe, DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentEvaluation,
  AgentEvaluationCost,
  AgentReplay,
  NhAssistantAdminApiService,
  NhAssistantError,
  NhAssistantPanelService,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/** Periods the evaluation offers, in days before now; the server allows at most 92. */
export type NhAssistantEvaluationPeriod = 7 | 30 | 90;

/** One row of the cost comparison: a measure of the live agent next to the variant's. */
interface NhAssistantCostRow {
  key: 'runs' | 'model-calls' | 'tokens' | 'tool-calls';
  live: AgentEvaluationCost;
  variant: AgentEvaluationCost;
}

const dayMs = 24 * 60 * 60 * 1000;
const defaultReplayRuns = 100;
const maxReplayRuns = 500;

let nextId = 0;

/**
 * Compares a shadow variant of an agent with the live agent, for the agent's administrators: the
 * runs that handled the same events, per tool the proposals both made, only one made and what
 * people decided about the live ones, what each cost and how each routed, with links to the threads
 * of the differences. Small numbers mislead, so counts stand next to every share. Administrators
 * replay the recorded runs of the period into the variant and export the comparison as JSON Lines.
 * The report holds no messages or arguments.
 */
@Component({
  selector: 'nh-assistant-agent-evaluation',
  standalone: true,
  imports: [DatePipe, DecimalPipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-evaluation.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './nh-assistant-agent-feedback-report.component.scss']
})
export class NhAssistantAgentEvaluationComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private readonly panel = inject(NhAssistantPanelService, { optional: true });
  private readonly document = inject(DOCUMENT);
  private subscription?: Subscription;
  private replaySubscription?: Subscription;

  /** The live agent. */
  readonly agentId = input.required<string>();
  /** Its shadow variants; the first is compared until another is chosen. */
  readonly variants = input.required<readonly string[]>();

  readonly id = `nh-assistant-agent-evaluation-${nextId++}`;
  readonly periods: readonly NhAssistantEvaluationPeriod[] = [7, 30, 90];
  readonly maxReplayRuns = maxReplayRuns;
  readonly canOpenThreads = this.panel !== null;

  readonly variant = signal('');
  readonly period = signal<NhAssistantEvaluationPeriod>(7);
  readonly loading = signal(true);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly evaluation = signal<AgentEvaluation | null>(null);

  readonly exporting = signal(false);
  readonly exportError = signal<NhAssistantError | null>(null);
  readonly exportErrorKeys = computed(() => nhAssistantErrorKeys(this.exportError()));
  readonly canExport = computed(() => !this.exporting() && !this.loading() && (this.evaluation()?.runs.paired ?? 0) > 0);

  readonly replays = signal<AgentReplay[]>([]);
  readonly replayRuns = signal(defaultReplayRuns);
  readonly replaying = signal(false);
  readonly replayError = signal<NhAssistantError | null>(null);
  readonly replayErrorKeys = computed(() => nhAssistantErrorKeys(this.replayError()));
  readonly replayRunning = computed(() => this.replays().some(replay => replay.status === 'running'));

  readonly costRows = computed<NhAssistantCostRow[]>(() => {
    const evaluation = this.evaluation();
    return evaluation
      ? (['runs', 'model-calls', 'tokens', 'tool-calls'] as const).map(key => ({ key, live: evaluation.liveCost, variant: evaluation.variantCost }))
      : [];
  });
  /** Routing matters only when one of the two worked in a skill. */
  readonly showRouting = computed(() => (this.evaluation()?.routing ?? []).some(row => row.liveSkill !== null || row.variantSkill !== null));

  /** The period the last load asked for, so the report, the export and a replay agree. */
  private range = periodRange(7);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.replaySubscription?.unsubscribe();
    });
    effect(() => {
      const variants = this.variants();
      this.agentId();
      untracked(() => {
        if (!variants.includes(this.variant())) {
          this.variant.set(variants[0] ?? '');
        }
        this.load();
      });
    });
  }

  /** Reloads the comparison and the variant's replays for the chosen period. */
  load(): void {
    this.range = periodRange(this.period());
    this.error.set(null);
    this.exportError.set(null);
    this.subscription?.unsubscribe();
    if (!this.variant()) {
      this.loading.set(false);
      this.evaluation.set(null);
      return;
    }

    this.loading.set(true);
    this.subscription = this.admin.getAgentEvaluation(this.agentId(), { variant: this.variant(), ...this.range }).subscribe({
      next: evaluation => {
        this.evaluation.set(evaluation);
        this.loading.set(false);
      },
      error: (failure: unknown) => {
        this.evaluation.set(null);
        this.loading.set(false);
        this.error.set(toNhAssistantError(failure));
      }
    });
    this.loadReplays();
  }

  loadReplays(): void {
    this.replaySubscription?.unsubscribe();
    this.replaySubscription = this.admin.getAgentReplays(this.variant()).subscribe({
      next: list => this.replays.set(list.items),
      error: () => this.replays.set([])
    });
  }

  onVariant(event: Event): void {
    this.variant.set((event.target as HTMLSelectElement).value);
    this.load();
  }

  onPeriod(event: Event): void {
    this.period.set(Number((event.target as HTMLSelectElement).value) as NhAssistantEvaluationPeriod);
    this.load();
  }

  onReplayRuns(event: Event): void {
    const value = Math.round(Number((event.target as HTMLInputElement).value));
    this.replayRuns.set(Number.isFinite(value) ? Math.min(Math.max(value, 1), maxReplayRuns) : defaultReplayRuns);
  }

  /** Replays the live agent's recorded runs of the period into the variant. */
  replay(): void {
    if (this.replaying() || this.replayRunning() || !this.variant()) {
      return;
    }

    this.replaying.set(true);
    this.replayError.set(null);
    this.admin
      .startAgentReplay(this.variant(), { sourceAgentId: this.agentId(), ...this.range, maxRuns: this.replayRuns() })
      .subscribe({
        next: replay => {
          this.replaying.set(false);
          this.replays.update(items => [replay, ...items.filter(item => item.id !== replay.id)]);
        },
        error: (failure: unknown) => {
          this.replaying.set(false);
          this.replayError.set(toNhAssistantError(failure));
        }
      });
  }

  /** Downloads the paired runs of the comparison as a JSON Lines file. */
  async export(): Promise<void> {
    if (!this.canExport()) {
      return;
    }

    this.exporting.set(true);
    this.exportError.set(null);
    try {
      const blob = await this.admin.exportAgentEvaluation(this.agentId(), { variant: this.variant(), ...this.range });
      const url = URL.createObjectURL(blob);
      const link = this.document.createElement('a');
      link.href = url;
      link.download = `agent-evaluation-${this.agentId()}-${this.variant()}.jsonl`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (failure) {
      this.exportError.set(toNhAssistantError(failure));
    } finally {
      this.exporting.set(false);
    }
  }

  /** Opens a thread of the comparison in the assistant panel, when the host has one. */
  openThread(conversationId: string): void {
    this.panel?.open(conversationId);
  }

  /** The share of a count in a total, or `null` without a total. */
  share(count: number, total: number): number | null {
    return total > 0 ? count / total : null;
  }

  total(row: AgentEvaluation['tools'][number]): number {
    return row.agreed + row.liveOnly;
  }

  categories(row: AgentEvaluation['tools'][number]): { category: string; count: number }[] {
    return Object.entries(row.liveDecisions.rejectionCategories)
      .map(([category, count]) => ({ category, count }))
      .sort((left, right) => right.count - left.count);
  }
}

function periodRange(days: number): { from: string; to: string } {
  const to = Date.now();
  return { from: new Date(to - days * dayMs).toISOString(), to: new Date(to).toISOString() };
}
