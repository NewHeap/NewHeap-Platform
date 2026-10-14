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
import { Observable, Subscription, forkJoin } from 'rxjs';
import {
  AgentExecutionResolution,
  AgentProposal,
  AgentProposalDecision,
  AgentProposalDecisionRight,
  AgentProposalFeedback,
  NH_ASSISTANT_MAX_PROPOSAL_LOOKUP,
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
 * change that feedback later. With `decisionRights` each proposal has its own right, for example
 * from the agent work on a subject, and a pending proposal the viewer may not decide says who does.
 * A proposal that no longer fits, for example because a person already did the work, shows why.
 * With `holdClosedCards`, a list of open proposals keeps such a proposal, or one a newer proposal
 * replaced, as a closed card when it closes while shown, or when an approval is refused because of
 * it, until the person dismisses it. It reads those proposals by id with one lookup, never through
 * their threads.
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
  /**
   * The caller's live right per proposal, by approval id, for example from
   * `AgentSubjectWork.openProposals`. A proposal with a right uses it instead of `canDecide`.
   */
  readonly decisionRights = input<ReadonlyMap<string, AgentProposalDecisionRight> | null>(null);
  readonly selectable = input(false);
  /**
   * Keeps proposals that close as stale or are replaced while shown as closed cards with their
   * explanation until they are dismissed. For lists of open proposals, which drop a proposal once
   * it closes.
   */
  readonly holdClosedCards = input(false);
  /** Emits after a proposal or its feedback changed, so the host reloads its list. */
  readonly changed = output<void>();

  readonly busy = signal(false);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** Pending proposals the caller may decide. */
  readonly pending = computed(() =>
    this.proposals().filter(proposal => proposal.status === 'pending' && this.mayDecide(proposal)));
  readonly allSelected = computed(() =>
    this.pending().length > 0 && this.pending().every(proposal => this.selected().has(proposal.approvalId)));
  /** The feedback question. It does not depend on `proposals`, which drop rejected proposals in the inbox. */
  readonly feedback = signal<NhAssistantFeedbackRequest | null>(null);
  /** Proposals that closed as stale while shown, kept until dismissed; newest first. */
  readonly held = signal<AgentProposal[]>([]);
  /** The held closed cards first, then the proposals the host gave. */
  readonly shown = computed(() => {
    const held = this.held();
    const heldIds = new Set(held.map(proposal => proposal.approvalId));
    return [...held, ...this.proposals().filter(proposal => !heldIds.has(proposal.approvalId))];
  });
  private readonly lookups = new Subscription();

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.lookups.unsubscribe();
    });
  }

  /**
   * The translation key of a proposal's status: the kind of a stale proposal, "replaced" for a
   * superseded one, otherwise its status.
   */
  statusKey(proposal: AgentProposal): string {
    if (proposal.status === 'stale' && proposal.staleKind) {
      return `nh-assistant.approval.stale-kind.${proposal.staleKind}`;
    }

    return isSuperseded(proposal)
      ? 'nh-assistant.approval.superseded'
      : `nh-assistant.agent-runs.proposal.status.${proposal.status}`;
  }

  /** True when the newer proposal that replaced this one is in the list. */
  hasSuccessor(proposal: AgentProposal): boolean {
    const successor = proposal.supersededByApprovalId;
    return !!successor && this.shown().some(item => item.approvalId === successor);
  }

  /** Moves focus to the newer proposal that replaced this one. */
  showSuccessor(proposal: AgentProposal): void {
    const card = Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('[data-approval-id]'))
      .find(element => element.dataset['approvalId'] === proposal.supersededByApprovalId);
    card?.scrollIntoView({ block: 'nearest' });
    card?.focus();
  }

  /** True for a closed card the list keeps until it is dismissed. */
  isHeld(proposal: AgentProposal): boolean {
    return this.held().some(item => item.approvalId === proposal.approvalId);
  }

  /** Removes a closed card. */
  dismiss(proposal: AgentProposal): void {
    this.held.update(held => held.filter(item => item.approvalId !== proposal.approvalId));
  }

  /**
   * Called by a host of open proposals after a live update replaced `previous` with `next`: keeps
   * the shown pending proposals that left the list because they closed as stale or were replaced.
   */
  holdClosed(previous: readonly AgentProposal[], next: readonly AgentProposal[]): void {
    const nextIds = new Set(next.map(proposal => proposal.approvalId));
    this.holdStale(previous.filter(proposal => proposal.status === 'pending' && !nextIds.has(proposal.approvalId)));
  }

  /** Whether the caller may decide this proposal: its own right when one was given, otherwise `canDecide`. */
  mayDecide(proposal: AgentProposal): boolean {
    const right = this.decisionRights()?.get(proposal.approvalId);
    return right ? right.canDecide : this.canDecide();
  }

  /**
   * Who decides a pending proposal that has a right the caller lacks: another approver when
   * four-eyes keeps out the person who started the work, otherwise an approver.
   */
  waitingForKey(proposal: AgentProposal): string | null {
    const right = this.decisionRights()?.get(proposal.approvalId);
    if (!right || right.canDecide || proposal.status !== 'pending') {
      return null;
    }

    return right.reasonCode === 'assistant-four-eyes-required'
      ? 'nh-assistant.approval.waiting-for-other-approver'
      : 'nh-assistant.approval.waiting-for-approver';
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
    }, code => {
      if (code === staleCode) {
        // The approval was refused because the proposal no longer fits; show why.
        this.holdStale([proposal]);
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
      const stale = response.results
        .filter(outcome => !outcome.succeeded && outcome.code === staleCode)
        .map(outcome => outcome.approvalId);
      this.holdStale(proposals.filter(proposal => stale.includes(proposal.approvalId)));
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

  /**
   * Reads `proposals` again by id and keeps those that closed as stale or were replaced as closed
   * cards, with their kind and explanation or their successor.
   */
  private holdStale(proposals: readonly AgentProposal[]): void {
    if (!this.holdClosedCards() || proposals.length === 0) {
      return;
    }

    // The proposals are read by id, never through their threads.
    const ids = [...new Set(proposals.map(proposal => proposal.approvalId))];
    const batches: string[][] = [];
    for (let start = 0; start < ids.length; start += NH_ASSISTANT_MAX_PROPOSAL_LOOKUP) {
      batches.push(ids.slice(start, start + NH_ASSISTANT_MAX_PROPOSAL_LOOKUP));
    }
    this.lookups.add(forkJoin(batches.map(batch => this.api.lookupProposals(batch))).subscribe({
      next: lookups => {
        const closed = lookups
          .flatMap(lookup => lookup.items)
          .map(item => item.proposal)
          .filter(proposal => proposal.status === 'stale' || isSuperseded(proposal));
        if (closed.length === 0) {
          return;
        }

        this.held.update(held => [
          ...closed,
          ...held.filter(item => !closed.some(proposal => proposal.approvalId === item.approvalId))
        ]);
      },
      // Without the lookup the proposal just leaves the list, as it did before.
      error: () => undefined
    }));
  }

  private run<T>(request: Observable<T>, done?: (result: T) => void, failed?: (code: string) => void): void {
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
        const error = toNhAssistantError(failure);
        this.error.set(error);
        failed?.(error.code);
        this.changed.emit();
      }
    });
  }
}

/** The refusal of an approval because the proposal no longer fits. */
const staleCode = 'assistant-proposal-stale';

/** The application's words for a proposal, falling back to its subject and tool. */
function summaryOf(proposal: AgentProposal): string {
  return proposal.presentation?.summary
    ?? `${proposal.subjectDisplayName ?? proposal.subjectId} · ${proposal.presentation?.toolDisplayName ?? proposal.toolId}`;
}

/** True for a proposal a newer proposal for the same objects replaced. */
function isSuperseded(proposal: AgentProposal): boolean {
  return proposal.status === 'withdrawn' && !!proposal.supersededByApprovalId;
}
