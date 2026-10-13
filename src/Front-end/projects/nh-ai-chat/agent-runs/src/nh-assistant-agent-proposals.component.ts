import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import {
  AgentExecutionResolution,
  AgentProposal,
  AgentProposalDecision,
  AgentProposalFeedback,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  NhAssistantProposalFeedbackComponent,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/** A feedback question: after a rejection above the list, or from a card's own action. */
interface NhAssistantFeedbackRequest {
  approvalIds: string[];
  initial: AgentProposalFeedback | null;
  placement: 'prompt' | 'card';
  /** What was rejected, for a single rejection. */
  summary: string | null;
  /** The feedback was stored; the panel thanks the person until the next action. */
  saved: boolean;
}

/**
 * A list of agent proposals with their decision and execution state. People who may decide see
 * approve and reject on pending proposals and the resolution of an execution with an unknown
 * outcome; the server checks every decision again. With `selectable`, pending proposals can be
 * approved or rejected together. Deciding takes one action. After a rejection the list asks,
 * optionally and above the list, what was wrong; closed proposals keep a low-key action to give or
 * change that feedback later.
 */
@Component({
  selector: 'nh-assistant-agent-proposals',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantProposalFeedbackComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-proposals.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantAgentProposalsComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private subscription?: Subscription;

  readonly proposals = input.required<AgentProposal[]>();
  /** Whether the caller may decide: an approver of the subjects or the inbox owner. */
  readonly canDecide = input(false);
  readonly selectable = input(false);
  /** Emits after a proposal or its feedback changed, so the host reloads its list. */
  readonly changed = output<void>();

  readonly busy = signal(false);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly pending = computed(() => this.proposals().filter(proposal => proposal.status === 'pending'));
  readonly allSelected = computed(() =>
    this.pending().length > 0 && this.pending().every(proposal => this.selected().has(proposal.approvalId)));
  /** The feedback question. It does not depend on `proposals`, which drop rejected proposals in the inbox. */
  readonly feedback = signal<NhAssistantFeedbackRequest | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  toggle(proposal: AgentProposal): void {
    const next = new Set(this.selected());
    if (next.has(proposal.approvalId)) {
      next.delete(proposal.approvalId);
    } else {
      next.add(proposal.approvalId);
    }
    this.selected.set(next);
  }

  toggleAll(): void {
    this.selected.set(this.allSelected() ? new Set() : new Set(this.pending().map(proposal => proposal.approvalId)));
  }

  decide(proposal: AgentProposal, decision: AgentProposalDecision): void {
    this.run(this.api.decide(proposal, decision), result => {
      if (decision === 'reject' && result.status === 'rejected' && result.canGiveFeedback === true) {
        this.ask([proposal.approvalId], summaryOf(proposal));
      }
    });
  }

  decideSelected(decision: AgentProposalDecision): void {
    const ids = [...this.selected()];
    if (ids.length === 0) {
      return;
    }

    const proposals = this.proposals();
    this.run(this.api.decideMany(ids, decision), response => {
      this.selected.set(new Set());
      if (decision !== 'reject') {
        return;
      }

      const rejected = response.results
        .filter(outcome => outcome.succeeded && outcome.status === 'rejected')
        .map(outcome => outcome.approvalId);
      if (rejected.length > 0) {
        const single = rejected.length === 1 ? proposals.find(proposal => proposal.approvalId === rejected[0]) : undefined;
        this.ask(rejected, single ? summaryOf(single) : null);
      }
    });
  }

  resolve(proposal: AgentProposal, action: AgentExecutionResolution): void {
    this.run(this.api.resolve(proposal.approvalId, action));
  }

  /** Opens the feedback question in the card of a closed proposal, with the caller's own feedback. */
  giveFeedback(proposal: AgentProposal): void {
    this.feedback.set({
      approvalIds: [proposal.approvalId],
      initial: proposal.feedback ?? null,
      placement: 'card',
      summary: null,
      saved: false
    });
  }

  /** True while the feedback question for this proposal is open, above the list or in its card. */
  isAsking(proposal: AgentProposal): boolean {
    const request = this.feedback();
    return request !== null && !request.saved && request.approvalIds.includes(proposal.approvalId);
  }

  /** True when the card of this proposal shows the feedback question or its thanks. */
  inCard(proposal: AgentProposal): boolean {
    const request = this.feedback();
    return request?.placement === 'card' && request.approvalIds[0] === proposal.approvalId;
  }

  /** Keeps the panel, which now thanks the person, and lets the host reload the proposals. */
  onFeedbackSaved(): void {
    this.feedback.update(request => request ? { ...request, saved: true } : request);
    this.changed.emit();
  }

  /** Closes the question; from a card, focus returns to the card's feedback action. */
  onFeedbackDismissed(): void {
    const request = this.feedback();
    this.feedback.set(null);
    if (request?.placement !== 'card') {
      return;
    }

    const [approvalId] = request.approvalIds;
    afterNextRender(() => {
      const action = Array.from(this.host.nativeElement.querySelectorAll<HTMLButtonElement>('[data-feedback-approval]'))
        .find(item => item.dataset['feedbackApproval'] === approvalId);
      action?.focus();
    }, { injector: this.injector });
  }

  private ask(approvalIds: string[], summary: string | null): void {
    this.feedback.set({ approvalIds, initial: null, placement: 'prompt', summary, saved: false });
  }

  private run<T>(request: Observable<T>, done?: (result: T) => void): void {
    this.busy.set(true);
    this.error.set(null);
    if (this.feedback()?.saved) {
      this.feedback.set(null);
    }
    this.subscription?.unsubscribe();
    this.subscription = request.subscribe({
      next: result => {
        this.busy.set(false);
        done?.(result);
        this.changed.emit();
      },
      error: failure => {
        this.busy.set(false);
        this.error.set(toNhAssistantError(failure));
        this.changed.emit();
      }
    });
  }
}

/** The application's words for a proposal, falling back to its subject and tool. */
function summaryOf(proposal: AgentProposal): string {
  return proposal.presentation?.summary
    ?? `${proposal.subjectDisplayName ?? proposal.subjectId} · ${proposal.presentation?.toolDisplayName ?? proposal.toolId}`;
}
