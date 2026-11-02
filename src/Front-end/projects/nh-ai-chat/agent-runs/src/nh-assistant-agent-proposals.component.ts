import { DatePipe, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, forkJoin } from 'rxjs';
import {
  AgentExecutionResolution,
  AgentInboxGroup,
  AgentProposal,
  AgentProposalDecision,
  AgentProposalDecisionRight,
  AgentProposalFeedback,
  AgentProposalOrigin,
  NH_ASSISTANT_MAX_PROPOSAL_LOOKUP,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  NhAssistantProposalFeedbackComponent,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantSubjectLinkComponent as NhAssistantSubjectLinkComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/**
 * How a proposals list shows its proposals: `comfortable` gives each proposal its card; `compact`
 * gives each one row that expands in place to that card, with the list's keys.
 */
export type NhAssistantProposalDensity = 'comfortable' | 'compact';

/** A key of a compact list in the list of keys that `?` shows: what to press and what it does. */
interface NhAssistantListKey {
  keys: string;
  does: string;
}

/** What a key of a compact list just did, read out by screen readers. */
interface NhAssistantKeyStatus {
  key: string;
  params: Record<string, string>;
}

let nextListId = 0;

/** The proposals of one run within a subject of a grouped list, in the order the list gave them. */
interface NhAssistantRunSection {
  key: string;
  runId: string | null;
  agentId: string;
  /** When the run's first proposal on the page was made. */
  proposedAt: string;
  items: AgentProposal[];
}

/** One subject within a group of a grouped list. */
interface NhAssistantSubjectSection {
  key: string;
  subjectType: string;
  subjectId: string;
  label: string;
  typeName: string | null;
  link: string | null;
  /** The group's own subject, whose proposals stand directly under the group's header. */
  own: boolean;
  count: number;
  total: number;
  runs: NhAssistantRunSection[];
}

/** One group of a grouped list, such as a parent subject of the grouped inbox. */
interface NhAssistantGroupSection {
  key: string;
  subjectType: string;
  subjectId: string;
  label: string;
  typeName: string | null;
  link: string | null;
  count: number;
  total: number;
  continuesFromPreviousPage: boolean;
  continuesOnNextPage: boolean;
  subjects: NhAssistantSubjectSection[];
  /** The group's pending proposals on this page that the caller may decide: what "Select group" selects. */
  selectableIds: string[];
}

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
 * approved or rejected together; the selection follows the list, so it holds only proposals the
 * list shows and "Approve selected" never sends one of another page. Deciding takes one action. After a rejection the list asks,
 * optionally and above the list, what was wrong; closed proposals keep a low-key action to give or
 * change that feedback later. With `decisionRights` each proposal has its own right, for example
 * from the agent work on a subject, and a pending proposal the viewer may not decide says who does.
 * A proposal that no longer fits, for example because a person already did the work, shows why.
 * With `holdClosedCards`, a list of open proposals keeps such a proposal, or one a newer proposal
 * replaced, as a closed card when it closes while shown, or when an approval is refused because of
 * it, until the person dismisses it. It reads those proposals by id with one lookup, never through
 * their threads. A proposal that a run placed here from another subject, such as a run for the whole
 * application, says so and offers that run's conversation through `openConversation`. With `groups`
 * from the grouped inbox the list shows a header per group and per subject, optionally per run
 * (`groupByRun`), and "Select group" selects only the group's pending proposals in this list that
 * the caller may decide, never those on another page. `newIds` marks proposals as new. A proposal of
 * a consultation names who asked and its route: one the person who asked decides in their own chat
 * (`asker-decides`) waits for them without actions here; one that runs with the agent's own rights
 * (`subject-agent-rights`) says so, and the person who asked reads why another approver decides.
 *
 * With `density: 'compact'` each proposal is one row: its object, kind of action, summary on one line,
 * agent and expiry, with the markers that must not disappear (its status when it no longer waits, new,
 * waiting for someone else, from a consultation and the source of its rights). Expanding a row shows its
 * card in place. The rows share one tab stop: the arrow keys, Home and End move between them, Enter
 * expands a row and Space selects it. While the focus is in the list, and never in a field or the
 * feedback form, `j` and `k` move, `x` selects, `a` approves and `r` rejects the proposal of the focused
 * row, never the selection, and `?` lists the keys. Rejecting by key asks for feedback afterwards, as a
 * click does. The comfortable cards stay the default.
 */
@Component({
  selector: 'nh-assistant-agent-proposals',
  standalone: true,
  imports: [
    DatePipe,
    NgTemplateOutlet,
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantSubjectLinkComponent,
    NhAssistantProposalFeedbackComponent
  ],
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
  /**
   * The groups of the grouped inbox (`AgentInbox.groups`) for `proposals` in the order the inbox gave
   * them: the first `count` proposals belong to the first group, and so on. Without groups, or when
   * they do not describe `proposals`, the list is flat.
   */
  readonly groups = input<readonly AgentInboxGroup[] | null>(null);
  /** Shows a header per run within a subject of a grouped list. */
  readonly groupByRun = input(false);
  /** Proposals to mark as new, by approval id, for example those made since the last visit of the inbox. */
  readonly newIds = input<ReadonlySet<string> | null>(null);
  /**
   * `compact` shows one row per proposal that expands to its card, with the list's keys, for lists of
   * many proposals; `comfortable`, the default, a card per proposal.
   */
  readonly density = input<NhAssistantProposalDensity>('comfortable');
  /** Emits after a proposal or its feedback changed, so the host reloads its list. */
  readonly changed = output<void>();
  /**
   * Emits the conversation of the run that placed a proposal here from another subject, so the host
   * opens it, for example in the assistant panel. The server checks the reader's access again.
   */
  readonly openConversation = output<string>();

  readonly id = `nh-assistant-agent-proposals-${nextListId++}`;
  readonly compact = computed(() => this.density() === 'compact');
  readonly busy = signal(false);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** The feedback question. It does not depend on `proposals`, which drop rejected proposals in the inbox. */
  readonly feedback = signal<NhAssistantFeedbackRequest | null>(null);
  /** Proposals that closed as stale while shown, kept until dismissed; newest first. */
  readonly held = signal<AgentProposal[]>([]);
  /** Pending proposals shown here that the caller may decide; a closed card the list keeps is none. */
  readonly pending = computed(() => {
    const heldIds = new Set(this.held().map(proposal => proposal.approvalId));
    return this.proposals().filter(proposal =>
      proposal.status === 'pending' && !heldIds.has(proposal.approvalId) && this.mayDecide(proposal));
  });
  /**
   * The selected proposals, by approval id. The selection follows the list: it keeps only pending
   * proposals shown here that the caller may decide. A refresh of the same page keeps it; another
   * page, another filter or a decision elsewhere drops what the list no longer shows, also when it
   * comes back later.
   */
  readonly selected = linkedSignal<readonly AgentProposal[], ReadonlySet<string>>({
    source: () => this.pending(),
    computation: (pending, previous) => {
      if (!previous) {
        return new Set();
      }

      const shown = new Set(pending.map(proposal => proposal.approvalId));
      const kept = [...previous.value].filter(id => shown.has(id));
      return kept.length === previous.value.size ? previous.value : new Set(kept);
    }
  });
  readonly allSelected = computed(() =>
    this.pending().length > 0 && this.pending().every(proposal => this.selected().has(proposal.approvalId)));
  /** The held closed cards first, then the proposals the host gave. */
  readonly shown = computed(() => {
    const held = this.held();
    const heldIds = new Set(held.map(proposal => proposal.approvalId));
    return [...held, ...this.proposals().filter(proposal => !heldIds.has(proposal.approvalId))];
  });
  /** The rows of a compact list shown expanded to their card, by approval id. */
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  /** Whether a compact list shows its keys. */
  readonly keysShown = signal(false);
  /** What a key of a compact list just did, for screen readers; `null` before the first key. */
  readonly keyStatus = signal<NhAssistantKeyStatus | null>(null);
  /** The keys of a compact list, as `?` lists them. */
  readonly listKeys: readonly NhAssistantListKey[] = ['move', 'edges', 'expand', 'select', 'approve', 'reject', 'help']
    .map(name => ({
      keys: `nh-assistant.agent-runs.compact.keys.${name}.keys`,
      does: `nh-assistant.agent-runs.compact.keys.${name}.does`
    }));
  /** The row of a compact list that had the focus last, and where it was, so its successor takes its place. */
  private readonly activeRow = signal<{ id: string; index: number } | null>(null);
  /**
   * The row with the tab stop of a compact list: the row that had the focus last, or, once it left the
   * list, the row that took its place.
   */
  readonly activeRowId = computed(() => {
    const rows = this.shown();
    if (rows.length === 0) {
      return null;
    }

    const active = this.activeRow();
    if (active && rows.some(proposal => proposal.approvalId === active.id)) {
      return active.id;
    }
    return rows[Math.min(active?.index ?? 0, rows.length - 1)].approvalId;
  });
  /** Set while a decision of a compact list may take the focused row away; its successor then gets the focus. */
  private restoreRowFocus = false;
  private readonly lookups = new Subscription();
  /**
   * The grouped list: the held closed cards stay above it; `null` for a flat list, or when the groups
   * do not describe the proposals.
   */
  readonly sections = computed<NhAssistantGroupSection[] | null>(() => {
    const groups = this.groups();
    const items = this.proposals();
    if (!groups || groups.reduce((sum, group) => sum + group.count, 0) !== items.length) {
      return null;
    }

    const heldIds = new Set(this.held().map(proposal => proposal.approvalId));
    const sections: NhAssistantGroupSection[] = [];
    let offset = 0;
    for (const group of groups) {
      const members = items.slice(offset, offset + group.count);
      offset += group.count;
      sections.push(this.groupSection(group, members, heldIds));
    }
    return sections;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.lookups.unsubscribe();
    });
    // When a decision takes the focused row of a compact list away, the row that takes its place gets
    // the focus, so a person deciding by key goes on where they were.
    effect(() => {
      const id = this.activeRowId();
      if (!this.compact() || !this.restoreRowFocus || id === null) {
        return;
      }

      afterNextRender(() => this.restoreFocusTo(id), { injector: this.injector });
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

  /**
   * The translation key and parameters that say where a placed proposal came from: the whole
   * application, a division of it, or another object, named only by its kind.
   */
  originText(origin: AgentProposalOrigin): { key: string; params: Record<string, string> } {
    if (origin.subjectType === 'nh-system') {
      return origin.subjectId?.startsWith('division-') && origin.subjectDisplayName
        ? { key: 'nh-assistant.agent-runs.proposal.placed-by.division', params: { name: origin.subjectDisplayName } }
        : { key: 'nh-assistant.agent-runs.proposal.placed-by.application', params: {} };
    }

    return origin.subjectTypeName
      ? { key: 'nh-assistant.agent-runs.proposal.placed-by.object-named', params: { type: origin.subjectTypeName } }
      : { key: 'nh-assistant.agent-runs.proposal.placed-by.object', params: {} };
  }

  /** Whether the proposal is marked as new. */
  isNew(proposal: AgentProposal): boolean {
    return this.newIds()?.has(proposal.approvalId) ?? false;
  }

  /** Whether every proposal "Select group" would select in the group is selected. */
  groupSelected(group: NhAssistantGroupSection): boolean {
    const selected = this.selected();
    return group.selectableIds.length > 0 && group.selectableIds.every(id => selected.has(id));
  }

  /**
   * Selects the group's pending proposals in this list that the caller may decide, or deselects them
   * when all are selected. Proposals of the group on another page are never selected: an approver
   * decides only what they see.
   */
  toggleGroup(group: NhAssistantGroupSection): void {
    const next = new Set(this.selected());
    if (this.groupSelected(group)) {
      group.selectableIds.forEach(id => next.delete(id));
    } else {
      group.selectableIds.forEach(id => next.add(id));
    }
    this.selected.set(next);
  }

  /** True when the newer proposal that replaced this one is in the list. */
  hasSuccessor(proposal: AgentProposal): boolean {
    const successor = proposal.supersededByApprovalId;
    return !!successor && this.shown().some(item => item.approvalId === successor);
  }

  /** Moves focus to the newer proposal that replaced this one: its card, or its row in a compact list. */
  showSuccessor(proposal: AgentProposal): void {
    const card = Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('[data-approval-id]'))
      .find(element => element.dataset['approvalId'] === proposal.supersededByApprovalId);
    const target = card?.querySelector<HTMLElement>('[data-proposal-row]') ?? card;
    target?.scrollIntoView({ block: 'nearest' });
    target?.focus();
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

  /**
   * Whether the caller may decide this proposal: its own right when one was given, otherwise
   * `canDecide`. Nobody decides a proposal here that the person who asked decides in their own chat.
   */
  mayDecide(proposal: AgentProposal): boolean {
    if (this.waitsForAsker(proposal)) {
      return false;
    }

    const right = this.decisionRights()?.get(proposal.approvalId);
    return right ? right.canDecide : this.canDecide();
  }

  /**
   * Whether a pending proposal of a consultation waits for the person who asked, who decides it in
   * their own chat: its route says so, or the server's reason for the caller's right.
   */
  waitsForAsker(proposal: AgentProposal): boolean {
    if (proposal.status !== 'pending') {
      return false;
    }

    return proposal.consultationRoute === 'asker-decides'
      || this.decisionRights()?.get(proposal.approvalId)?.reasonCode === askerDecidesCode;
  }

  /**
   * Who decides a pending proposal that has a right the caller lacks: another approver when
   * four-eyes keeps out the person who started the work, which for a consultation's proposal that
   * runs with the agent's own rights is the person who asked, otherwise an approver. A proposal that
   * waits for the person who asked says so on its own (`waitsForAsker`).
   */
  waitingForKey(proposal: AgentProposal): string | null {
    const right = this.decisionRights()?.get(proposal.approvalId);
    if (!right || right.canDecide || proposal.status !== 'pending' || this.waitsForAsker(proposal)) {
      return null;
    }

    if (right.reasonCode !== fourEyesCode) {
      return 'nh-assistant.approval.waiting-for-approver';
    }

    return proposal.consultationRoute === 'subject-agent-rights'
      ? 'nh-assistant.agent-runs.proposal.consultation.four-eyes'
      : 'nh-assistant.approval.waiting-for-other-approver';
  }

  /**
   * The badge of a consultation's proposal that runs with the agent's own rights: those of a technical
   * account when the agent answers to one, otherwise the agent's own; `null` for other proposals.
   */
  agentRightsKey(proposal: AgentProposal): string | null {
    if (proposal.consultationRoute !== 'subject-agent-rights') {
      return null;
    }

    return proposal.grantSource === 'accountable'
      ? 'nh-assistant.agent-runs.proposal.consultation.runs-with.accountable'
      : 'nh-assistant.agent-runs.proposal.consultation.runs-with.agent';
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
    this.decideOne(proposal, decision, false);
  }

  /**
   * Decides the selected proposals together. Only proposals this list shows are sent, in the list's
   * order, each with the proposal hash the person saw, so the server refuses one that changed since: a
   * person decides only what they see.
   */
  decideSelected(decision: AgentProposalDecision): void {
    const selected = this.selected();
    const chosen = this.pending().filter(proposal => selected.has(proposal.approvalId));
    if (chosen.length === 0) {
      return;
    }

    const proposals = this.proposals();
    this.run(this.api.decideMany(chosen, decision), response => {
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

  /**
   * Whether the application executes the proposal: it was handed to the application, so an approver
   * resolves an unknown outcome by handing it to the application again instead of running it.
   */
  executedByApplication(proposal: AgentProposal): boolean {
    return !!proposal.executionReportDueAt;
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

  /** Whether the caller may select this proposal to decide it together with others. */
  canSelect(proposal: AgentProposal): boolean {
    return this.selectable() && proposal.status === 'pending' && !this.isHeld(proposal) && this.mayDecide(proposal);
  }

  /** The application's words for a proposal, falling back to its subject and tool. */
  summaryText(proposal: AgentProposal): string {
    return summaryOf(proposal);
  }

  /**
   * The marker of a compact row that waits for someone else: the person who asked through a
   * consultation, or another approver; `null` when nobody else is waited for.
   */
  waitingMarkerKey(proposal: AgentProposal): string | null {
    return this.waitsForAsker(proposal) ? 'nh-assistant.approval.waiting-for-asker' : this.waitingForKey(proposal);
  }

  /** The id of the card a compact row expands to. */
  rowDetailsId(proposal: AgentProposal): string {
    return `${this.id}-row-${proposal.approvalId}`;
  }

  isExpanded(proposal: AgentProposal): boolean {
    return this.expanded().has(proposal.approvalId);
  }

  /** Shows or hides the card of a compact row. */
  toggleExpanded(proposal: AgentProposal): void {
    const next = new Set(this.expanded());
    if (next.has(proposal.approvalId)) {
      next.delete(proposal.approvalId);
    } else {
      next.add(proposal.approvalId);
    }
    this.expanded.set(next);
  }

  /** Gives the tab stop of a compact list to the row that got the focus. */
  activate(proposal: AgentProposal): void {
    const index = this.shown().findIndex(item => item.approvalId === proposal.approvalId);
    this.activeRow.set({ id: proposal.approvalId, index: Math.max(0, index) });
  }

  toggleKeys(): void {
    if (this.keysShown()) {
      this.hideKeys(false);
    } else {
      this.showKeys();
    }
  }

  /** Shows the keys of a compact list and moves the focus to them. */
  showKeys(): void {
    this.keysShown.set(true);
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`#${this.id}-keys`)?.focus(), { injector: this.injector });
  }

  /** Hides the keys; from the keys themselves the focus returns to the row with the tab stop. */
  hideKeys(returnFocus: boolean): void {
    this.keysShown.set(false);
    if (!returnFocus) {
      return;
    }

    const id = this.activeRowId();
    afterNextRender(() => {
      const button = id === null ? undefined : this.rowButton(id);
      if (button) {
        this.focusRowButton(button);
      }
    }, { injector: this.injector });
  }

  /**
   * The keys of a compact list. They act only while the focus is in a row of the list, and never in an
   * input, text area, select, editable content or the feedback form, so typing never decides anything
   * (WCAG 2.1.4). Arrow keys, Home and End act on a row itself; Space selects a row that can be selected
   * and otherwise lets Enter's expansion happen. `a` and `r` decide the focused row's proposal, never the
   * selection, and a held key decides nothing more.
   */
  onListKeydown(event: KeyboardEvent): void {
    if (!this.compact() || event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || isField(event.target)) {
      return;
    }

    if (event.key === '?') {
      event.preventDefault();
      if (this.keysShown()) {
        this.hideKeys(true);
      } else {
        this.showKeys();
      }
      return;
    }

    const target = event.target instanceof HTMLElement ? event.target : null;
    const approvalId = target?.closest<HTMLElement>('[data-approval-id]')?.dataset['approvalId'];
    const proposal = approvalId ? this.shown().find(item => item.approvalId === approvalId) : undefined;
    if (!target || !proposal) {
      return;
    }

    const onRow = target.hasAttribute('data-proposal-row');
    switch (event.key) {
      case 'ArrowDown':
      case 'ArrowUp':
      case 'Home':
      case 'End':
        if (!onRow) {
          return;
        }
        this.focusRow(proposal, event.key);
        break;
      case 'j':
        this.focusRow(proposal, 'ArrowDown');
        break;
      case 'k':
        this.focusRow(proposal, 'ArrowUp');
        break;
      case ' ':
        if (!onRow || !this.canSelect(proposal)) {
          return;
        }
        this.selectByKey(proposal);
        break;
      case 'x':
        if (!event.repeat) {
          this.selectByKey(proposal);
        }
        break;
      case 'a':
      case 'r':
        if (!event.repeat) {
          this.decideByKey(proposal, event.key === 'a' ? 'approve' : 'reject');
        }
        break;
      default:
        return;
    }

    event.preventDefault();
  }

  /** Space selected the row on key down; the button must not also expand on key up. */
  onRowKeyup(event: KeyboardEvent, proposal: AgentProposal): void {
    if (event.key === ' ' && this.compact() && this.canSelect(proposal)) {
      event.preventDefault();
    }
  }

  private selectByKey(proposal: AgentProposal): void {
    if (this.busy()) {
      return;
    }
    if (!this.canSelect(proposal)) {
      this.announce('not-selectable', proposal);
      return;
    }

    this.toggle(proposal);
    this.announce(this.selected().has(proposal.approvalId) ? 'selected' : 'deselected', proposal);
  }

  private decideByKey(proposal: AgentProposal, decision: AgentProposalDecision): void {
    if (this.busy()) {
      return;
    }
    if (proposal.status !== 'pending' || this.isHeld(proposal) || !this.mayDecide(proposal)) {
      this.announce('not-decidable', proposal);
      return;
    }

    this.decideOne(proposal, decision, true);
  }

  /** Decides one proposal. In a compact list, the row that takes the decided row's place gets the focus. */
  private decideOne(proposal: AgentProposal, decision: AgentProposalDecision, byKey: boolean): void {
    if (this.compact()) {
      this.activate(proposal);
      this.restoreRowFocus = true;
    }

    this.run(this.api.decide(proposal, decision), result => {
      const asks = decision === 'reject' && result.status === 'rejected' && result.canGiveFeedback === true;
      if (asks) {
        this.ask([proposal.approvalId], summaryOf(proposal));
      }
      if (byKey) {
        this.announce(decision === 'approve' ? 'approved' : asks ? 'rejected-feedback' : 'rejected', proposal);
      }
    }, code => {
      if (code === staleCode) {
        // The approval was refused because the proposal no longer fits; show why.
        this.holdStale([proposal]);
      }
    });
  }

  /** Moves the focus from a row to the next, the previous, the first or the last row. */
  private focusRow(from: AgentProposal, key: 'ArrowDown' | 'ArrowUp' | 'Home' | 'End'): void {
    const rows = this.rowButtons();
    if (rows.length === 0) {
      return;
    }

    const index = rows.findIndex(row => row.dataset['proposalRow'] === from.approvalId);
    const last = rows.length - 1;
    const targets: Record<typeof key, number> = {
      ArrowDown: Math.min(index + 1, last),
      ArrowUp: Math.max(index - 1, 0),
      Home: 0,
      End: last
    };
    this.focusRowButton(rows[targets[key]]);
  }

  /**
   * Focuses a row and gives it the tab stop. The tab stop does not wait for the focus event, which a
   * browser window in the background does not fire.
   */
  private focusRowButton(button: HTMLElement): void {
    const target = this.shown().find(proposal => proposal.approvalId === button.dataset['proposalRow']);
    if (target) {
      this.activate(target);
    }
    button.focus();
  }

  /**
   * Gives the focus to the row that took a decided row's place, but only when the decision took the
   * focus away: focus the person moved elsewhere stays there.
   */
  private restoreFocusTo(id: string): void {
    if (!this.restoreRowFocus) {
      return;
    }

    this.restoreRowFocus = false;
    const document = this.host.nativeElement.ownerDocument;
    if (document.activeElement !== null && document.activeElement !== document.body) {
      return;
    }

    const button = this.rowButton(id);
    if (button) {
      this.focusRowButton(button);
    }
  }

  private rowButtons(): HTMLElement[] {
    return Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('[data-proposal-row]'));
  }

  private rowButton(id: string): HTMLElement | undefined {
    return this.rowButtons().find(row => row.dataset['proposalRow'] === id);
  }

  private announce(status: string, proposal: AgentProposal): void {
    this.keyStatus.set({ key: `nh-assistant.agent-runs.compact.status.${status}`, params: { summary: summaryOf(proposal) } });
  }

  private groupSection(group: AgentInboxGroup, members: AgentProposal[], heldIds: ReadonlySet<string>): NhAssistantGroupSection {
    const key = `${group.subjectType}|${group.subjectId}`;
    const subjects: NhAssistantSubjectSection[] = [];
    let offset = 0;
    for (const subject of group.subjects) {
      const items = members
        .slice(offset, offset + subject.count)
        .filter(proposal => !heldIds.has(proposal.approvalId)
          && proposal.subjectType === subject.subjectType
          && proposal.subjectId === subject.subjectId);
      offset += subject.count;
      const first = items[0];
      subjects.push({
        key: `${key}|${subject.subjectType}|${subject.subjectId}`,
        subjectType: subject.subjectType,
        subjectId: subject.subjectId,
        label: first?.subjectDisplayName ?? subject.subjectId,
        typeName: first?.subjectTypeName ?? null,
        link: first?.subjectLink ?? null,
        own: subject.subjectType === group.subjectType && subject.subjectId === group.subjectId,
        count: subject.count,
        total: subject.total,
        runs: runsOf(items)
      });
    }

    return {
      key,
      subjectType: group.subjectType,
      subjectId: group.subjectId,
      label: group.displayName ?? group.subjectId,
      typeName: group.subjectTypeName ?? null,
      link: group.subjectLink ?? null,
      count: group.count,
      total: group.total,
      continuesFromPreviousPage: group.continuesFromPreviousPage,
      continuesOnNextPage: group.continuesOnNextPage,
      subjects,
      selectableIds: members
        .filter(proposal => !heldIds.has(proposal.approvalId) && proposal.status === 'pending' && this.mayDecide(proposal))
        .map(proposal => proposal.approvalId)
    };
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

/** Someone other than the person who started the work decides the proposal. */
const fourEyesCode = 'assistant-four-eyes-required';

/** The person who asked decides a consultation's proposal in their own chat; nobody decides it here. */
const askerDecidesCode = 'assistant-consult-asker-decides';

/** The application's words for a proposal, falling back to its subject and tool. */
function summaryOf(proposal: AgentProposal): string {
  return proposal.presentation?.summary
    ?? `${proposal.subjectDisplayName ?? proposal.subjectId} · ${proposal.presentation?.toolDisplayName ?? proposal.toolId}`;
}

/** The proposals of each run in a row, in the order given. */
function runsOf(items: readonly AgentProposal[]): NhAssistantRunSection[] {
  const runs: NhAssistantRunSection[] = [];
  for (const proposal of items) {
    const last = runs.at(-1);
    if (last && last.runId === proposal.runId) {
      last.items.push(proposal);
      continue;
    }

    runs.push({
      key: `${proposal.runId ?? 'none'}|${proposal.approvalId}`,
      runId: proposal.runId,
      agentId: proposal.agentId,
      proposedAt: proposal.createdAt,
      items: [proposal]
    });
  }
  return runs;
}

/** True for a proposal a newer proposal for the same objects replaced. */
function isSuperseded(proposal: AgentProposal): boolean {
  return proposal.status === 'withdrawn' && !!proposal.supersededByApprovalId;
}

/**
 * True when a key goes to something a person types or chooses in, where the list's letters must never
 * act: an input, text area, select, editable content or the feedback form.
 */
function isField(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }

  return target.isContentEditable
    || target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), nh-assistant-proposal-feedback') !== null;
}
