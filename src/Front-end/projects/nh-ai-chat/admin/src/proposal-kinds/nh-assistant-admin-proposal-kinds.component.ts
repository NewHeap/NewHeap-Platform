import { NgTemplateOutlet, PercentPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, TemplateRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  NhAssistantAdminApiService,
  NhAssistantError,
  ProposalKind,
  ProposalKinds,
  RoutineCandidate,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTokensDirective as NhAssistantTokensDirective,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { startNhAssistantAdminTranslations } from '../i18n/nh-assistant-admin-translations';
import { nhAssistantDurationText } from './nh-assistant-decision-time';
import { NhAssistantRoutineCandidateRowComponent } from './nh-assistant-routine-candidate-row.component';

/** Periods the view offers, in days before now. */
export type NhAssistantProposalKindsPeriod = 7 | 30 | 90;

/**
 * What the view hands the template of `candidateActions` for one agent's row: the candidate (agent id,
 * tool id, its numbers and whether it is a candidate) and the kind of action it belongs to.
 */
export interface NhAssistantRoutineCandidateActionContext {
  $implicit: RoutineCandidate;
  kind: ProposalKind;
}

const dayMs = 24 * 60 * 60 * 1000;

let nextId = 0;

/**
 * The view per kind of action over all agents, for administrators (FB82): per kind of action the
 * name the proposal cards show, the decisions, the rejection rate and how long people take to decide
 * (median and 90th percentile), and per agent whether the numbers make the kind a candidate for
 * running without approval, by the thresholds the server applied, which the view explains. Kinds with
 * a candidate open on load. The view only reads: a candidate is a suggestion and nothing about
 * deciding changes. The server requires the assistant admin policy.
 *
 * `candidateActions` adds a last column to each agent's row with the host's own content for the
 * candidate, such as a control that acts on it; the template receives the candidate as `$implicit`
 * and the kind as `kind`.
 */
@Component({
  selector: 'nh-assistant-admin-proposal-kinds',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [
    NgTemplateOutlet,
    PercentPipe,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantRoutineCandidateRowComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-admin-proposal-kinds.component.html',
  styleUrls: ['../nh-assistant-admin-shared.scss', './nh-assistant-admin-proposal-kinds.component.scss']
})
export class NhAssistantAdminProposalKindsComponent {
  private readonly admin = inject(NhAssistantAdminApiService);
  private subscription?: Subscription;

  /** Narrows the view to one agent's kinds of action; every agent by default. */
  readonly agentId = input<string | null>(null);
  /** The content of a last column in each agent's row, such as an action on the candidate. */
  readonly candidateActions = input<TemplateRef<NhAssistantRoutineCandidateActionContext> | null>(null);

  readonly id = `nh-assistant-proposal-kinds-${nextId++}`;
  readonly periods: readonly NhAssistantProposalKindsPeriod[] = [7, 30, 90];
  readonly period = signal<NhAssistantProposalKindsPeriod>(30);
  readonly onlyCandidates = signal(false);

  readonly loading = signal(true);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly view = signal<ProposalKinds | null>(null);
  /** The kinds whose agents are shown, by tool id. */
  readonly expanded = signal<ReadonlySet<string>>(new Set());

  readonly thresholds = computed(() => this.view()?.thresholds ?? null);
  readonly kinds = computed(() => (this.view()?.kinds ?? [])
    .filter(kind => !this.onlyCandidates() || this.candidateCount(kind) > 0));
  readonly approvalRatePercent = computed(() => Math.round((this.thresholds()?.minimumApprovalRate ?? 0) * 1000) / 10);
  /** The number of columns, so a kind's agents line up with it. */
  readonly columns = computed(() => this.candidateActions() ? 9 : 8);

  constructor() {
    startNhAssistantAdminTranslations();

    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
    effect(() => {
      this.agentId();
      untracked(() => this.load());
    });
  }

  /** Loads the view for the chosen period and opens the kinds that have a candidate. */
  load(): void {
    const to = Date.now();
    this.loading.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.admin
      .getProposalKinds({
        from: new Date(to - this.period() * dayMs).toISOString(),
        to: new Date(to).toISOString(),
        agentId: this.agentId() ?? undefined
      })
      .subscribe({
        next: view => {
          this.view.set(view);
          this.expanded.set(new Set(view.kinds.filter(kind => this.candidateCount(kind) > 0).map(kind => kind.toolId)));
          this.loading.set(false);
        },
        error: (failure: unknown) => {
          this.view.set(null);
          this.loading.set(false);
          this.error.set(toNhAssistantError(failure));
        }
      });
  }

  onPeriod(event: Event): void {
    this.period.set(Number((event.target as HTMLSelectElement).value) as NhAssistantProposalKindsPeriod);
    this.load();
  }

  onOnlyCandidates(event: Event): void {
    this.onlyCandidates.set((event.target as HTMLInputElement).checked);
  }

  isExpanded(kind: ProposalKind): boolean {
    return this.expanded().has(kind.toolId);
  }

  toggle(kind: ProposalKind): void {
    const next = new Set(this.expanded());
    if (!next.delete(kind.toolId)) {
      next.add(kind.toolId);
    }
    this.expanded.set(next);
  }

  candidateCount(kind: ProposalKind): number {
    return kind.agents.filter(agent => agent.candidate).length;
  }

  duration(seconds: number | undefined | null): { key: string; params: Record<string, number> } | null {
    return seconds === undefined || seconds === null ? null : nhAssistantDurationText(seconds);
  }
}
