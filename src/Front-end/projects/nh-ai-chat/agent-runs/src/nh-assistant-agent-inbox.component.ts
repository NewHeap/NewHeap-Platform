import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentBulkDecisionFilter,
  AgentBulkDecisionKind,
  AgentBulkDecisionOverview,
  AgentBulkDecisionResult,
  AgentInbox,
  AgentInboxGroup,
  AgentInboxSort,
  AgentProposal,
  AgentProposalDecision,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵnhAssistantWatchAgentWork as nhAssistantWatchAgentWork,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAgentProposalsComponent } from './nh-assistant-agent-proposals.component';

/**
 * The approval inbox: pending agent proposals across subjects that the signed-in person may
 * decide, earliest expiry first. Proposals are decided in place, one by one or together; the
 * server checks each decision against the proposal's approval policy again. After a rejection the
 * inbox asks, optionally, what was wrong, also when the rejected proposals have left the list.
 * With live updates the inbox reloads by itself when agent work changes, for example when a
 * colleague decided a proposal or a run proposed a new one; it watches the work of agents for the
 * whole application, whose approvers are no members of it, while it is shown. A proposal that closes because it no
 * longer fits, for example because someone did the work by hand, stays as a closed card with the
 * reason until it is dismissed. A proposal a run placed at its object from another subject, such as a
 * run for the whole application, names that run; `openConversation` emits its conversation.
 *
 * `agentId`, `subjectType`, `divisionId` and `runId` filter the inbox, `toolId` its kinds of action and
 * `expiresBefore` the decision deadline, and `sort` orders it by `expiresAt` or `createdAt` instead of
 * each status's own order; changing a filter, the sort or `grouped` reloads it from the first page. A
 * host builds its own filter on `NhAssistantAgentRunApiService.getInboxSummary`, which counts what the
 * inbox lists per agent, kind of action and subject type with the same filter. With `grouped` the inbox shows the proposals per parent, such as a
 * project with its own proposals and those of its tasks, and per subject, optionally per run
 * (`groupByRun`); a group that goes on on another page says so, and "Select group" selects only its
 * proposals shown here. With `markNew` (on by default) it marks the proposals made since the person's
 * last visit of the inbox in its scope, the whole application or the division of `divisionId`, and
 * those that arrive while it is shown; with `recordVisit` (on by default) showing it records the
 * visit. Turn `recordVisit` off for an inbox that shows only part of the scope, such as one agent's
 * proposals, so the person's next visit of the whole inbox still marks what they have not seen.
 *
 * "Decide all" (`decideAll`, on by default) decides the inbox's whole filter, not only the shown page:
 * it first shows the overview of the set the server would decide (`getBulkDecisionOverview`), how many
 * times which kind of action on how many objects, the proposals of kinds that are decided one by one,
 * which stay, and how many remain beyond one set; approving or rejecting there decides exactly that set
 * (`decideBulk`), each proposal with its own rules, and the inbox shows what succeeded, what was refused
 * and what had changed. Turn `decideAll` off where people should decide one proposal at a time.
 */
@Component({
  selector: 'nh-assistant-agent-inbox',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantIconComponent, NhAssistantTranslatePipe, NhAssistantAgentProposalsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-inbox.component.html',
  styleUrl: './nh-assistant-agent-runs.scss',
  // The overview of "Decide all"; the list's own styles stay in the shared sheet.
  styles: [`
    .bulk-decision { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
    .bulk-decision h4 { margin: 0; font-size: 14px; }
    .bulk-decision ul { margin: 0; padding-left: 18px; }
    .bulk-decision li, .bulk-decision p { overflow-wrap: anywhere; }
    .bulk-decision textarea { width: 100%; min-height: 56px; box-sizing: border-box; font: inherit; resize: vertical; }
  `]
})
export class NhAssistantAgentInboxComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly proposalList = viewChild(NhAssistantAgentProposalsComponent);
  private subscription?: Subscription;
  private visitSubscription?: Subscription;
  /** The scope whose visit this view recorded; the visit is recorded once per scope shown. */
  private visited: string | null = null;
  /**
   * What the first read of the shown scope found: the visit before this one and the proposals that
   * were new since. Later reads, after live updates, paging or a decision, compare with both, so what
   * was new when the inbox was opened stays new while it is shown.
   */
  private opened: { scopeKey: string; previousVisit: string | null; newIds: Set<string> } | null = null;

  /** Limits the inbox to one agent. */
  readonly agentId = input<string | undefined>(undefined);
  /** Limits the inbox to one kind of subject, for example `project`. */
  readonly subjectType = input<string | undefined>(undefined);
  /**
   * Limits the inbox to the subjects of one division, for example the active division; its visits are
   * then those of that division.
   */
  readonly divisionId = input<string | null | undefined>(undefined);
  /** Limits the inbox to the proposals of one run. */
  readonly runId = input<string | null | undefined>(undefined);
  /** Limits the inbox to one or more kinds of action (`AgentProposal.toolId`). */
  readonly toolId = input<string | readonly string[] | null | undefined>(undefined);
  /** Limits the inbox to proposals whose decision deadline is before this ISO time. */
  readonly expiresBefore = input<string | null | undefined>(undefined);
  /** Orders the inbox by the decision deadline or by when proposals were made, earliest first. */
  readonly sort = input<AgentInboxSort | null | undefined>(undefined);
  /** Groups the proposals per parent and subject instead of listing them by expiry alone. */
  readonly grouped = input(false);
  /** In a grouped inbox, also shows a header per run within a subject. */
  readonly groupByRun = input(false);
  /** Marks the proposals made since the person's last visit of the inbox, and those that arrive while it is shown. */
  readonly markNew = input(true);
  /** Records the person's visit of the inbox in its scope when it is shown. */
  readonly recordVisit = input(true);
  /** Offers "Decide all" for the inbox's whole filter, after an overview of what it decides. */
  readonly decideAll = input(true);
  readonly itemsPerPage = input(20);
  /**
   * Emits the conversation of the run that placed a proposal at its object from another subject, so
   * the host opens it, for example in the assistant panel.
   */
  readonly openConversation = output<string>();

  readonly page = signal(1);
  readonly loading = signal(true);
  /** True after the first successful load; later loads keep the list, and an open feedback question, in place. */
  readonly loaded = signal(false);
  readonly loadFailed = signal(false);
  readonly items = signal<AgentProposal[]>([]);
  readonly total = signal(0);
  /** The groups of the page in a grouped inbox; `null` for the flat inbox. */
  readonly groups = signal<AgentInboxGroup[] | null>(null);
  /** The proposals of the page marked as new. */
  readonly newIds = signal<ReadonlySet<string>>(new Set());
  /**
   * The person's visit of the shown scope before the inbox was opened: `null` before their first
   * visit, `undefined` while unknown or when new proposals are not marked.
   */
  readonly previousVisit = signal<string | null | undefined>(undefined);
  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.itemsPerPage())));

  /** The overview "Decide all" shows before deciding; `null` while it is closed. */
  readonly overview = signal<AgentBulkDecisionOverview | null>(null);
  /** The outcome of the last "Decide all"; `null` before one. */
  readonly bulkResult = signal<AgentBulkDecisionResult | null>(null);
  readonly bulkBusy = signal(false);
  readonly bulkError = signal<NhAssistantError | null>(null);
  readonly bulkErrorKeys = computed(() => nhAssistantErrorKeys(this.bulkError()));
  /** The optional reason "Decide all" records with every decision. */
  readonly bulkReason = signal('');
  private bulkSubscription?: Subscription;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.visitSubscription?.unsubscribe();
      this.bulkSubscription?.unsubscribe();
    });
    effect(() => {
      this.agentId();
      this.subjectType();
      this.divisionId();
      this.runId();
      this.toolId();
      this.expiresBefore();
      this.sort();
      this.grouped();
      untracked(() => {
        this.page.set(1);
        // An overview belongs to the filter it was made for.
        this.closeOverview();
        this.load();
      });
    });
    // Any agent's work may add, decide or close a proposal of this inbox, also work for the whole
    // application, which reaches the approvers only while they watch it.
    nhAssistantWatchAgentWork();
    nhAssistantReloadOnAgentWork(() => true, () => this.refresh());
  }

  load(): void {
    this.fetch(false);
  }

  /** "Decide all": shows what deciding the inbox's whole filter would decide, before anything is decided. */
  openOverview(): void {
    this.bulkSubscription?.unsubscribe();
    this.bulkBusy.set(true);
    this.bulkError.set(null);
    this.bulkResult.set(null);
    this.bulkReason.set('');
    this.bulkSubscription = this.api.getBulkDecisionOverview(this.filter()).subscribe({
      next: overview => {
        this.bulkBusy.set(false);
        this.overview.set(overview);
      },
      error: failure => {
        this.bulkBusy.set(false);
        this.bulkError.set(toNhAssistantError(failure));
      }
    });
  }

  closeOverview(): void {
    this.bulkSubscription?.unsubscribe();
    this.bulkBusy.set(false);
    this.overview.set(null);
    this.bulkError.set(null);
  }

  /** Decides exactly the set of the shown overview, then shows the tallies and reloads the inbox. */
  decideOverview(decision: AgentProposalDecision): void {
    const token = this.overview()?.token;
    if (!token || this.bulkBusy()) {
      return;
    }

    this.bulkSubscription?.unsubscribe();
    this.bulkBusy.set(true);
    this.bulkError.set(null);
    this.bulkSubscription = this.api.decideBulk(token, decision, this.bulkReason()).subscribe({
      next: result => {
        this.bulkBusy.set(false);
        this.overview.set(null);
        this.bulkResult.set(result);
        this.load();
      },
      error: failure => {
        this.bulkBusy.set(false);
        this.bulkError.set(toNhAssistantError(failure));
      }
    });
  }

  onBulkReason(event: Event): void {
    this.bulkReason.set((event.target as HTMLTextAreaElement).value);
  }

  /** The translation key of a kind's line, which names one object or several. */
  kindKey(kind: AgentBulkDecisionKind): string {
    return kind.subjects === 1
      ? 'nh-assistant.agent-runs.inbox.bulk.kind-one-object'
      : 'nh-assistant.agent-runs.inbox.bulk.kind';
  }

  /** The translation key of an overview's text about one proposal or several. */
  one(key: 'title' | 'individual' | 'remaining', count: number): string {
    return `nh-assistant.agent-runs.inbox.bulk.${key}${count === 1 ? '-one' : ''}`;
  }

  /** The names of the kinds of action that are decided one by one. */
  individualNames(overview: AgentBulkDecisionOverview): string {
    return overview.individual.tools.map(tool => tool.displayName).join(', ');
  }

  /** The inbox's filter, without its paging and grouping, as "Decide all" decides it. */
  private filter(): AgentBulkDecisionFilter {
    return {
      agentId: this.agentId(),
      subjectType: this.subjectType(),
      divisionId: this.divisionId() ?? undefined,
      runId: this.runId() ?? undefined,
      toolId: this.toolId() ?? undefined,
      expiresBefore: this.expiresBefore() ?? undefined,
      sort: this.sort() ?? undefined
    };
  }

  goTo(page: number): void {
    this.page.set(Math.min(Math.max(1, page), this.pages()));
    this.load();
  }

  /**
   * Reloads after a live update. Once the inbox is shown it reloads quietly: no loading state, and
   * a failure keeps the list and an open feedback question in place. While a decision of the list
   * is in flight it waits, because the decision reloads the inbox when it ends.
   */
  private refresh(): void {
    if (this.proposalList()?.busy()) {
      return;
    }

    this.fetch(this.loaded() && !this.loading() && !this.loadFailed());
  }

  private fetch(quiet: boolean): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
    }

    const divisionId = this.divisionId() ?? null;
    this.subscription?.unsubscribe();
    this.subscription = this.api
      .getInbox({
        agentId: this.agentId(),
        subjectType: this.subjectType(),
        divisionId: divisionId ?? undefined,
        runId: this.runId() ?? undefined,
        toolId: this.toolId() ?? undefined,
        expiresBefore: this.expiresBefore() ?? undefined,
        sort: this.sort() ?? undefined,
        // Only a grouped inbox asks for groups, so the requests of a flat inbox stay as they were.
        grouped: this.grouped() || undefined,
        page: this.page(),
        itemsPerPage: this.itemsPerPage()
      })
      .subscribe({
        next: inbox => {
          const lastPage = Math.max(1, Math.ceil(inbox.total / this.itemsPerPage()));
          if (inbox.items.length === 0 && this.page() > lastPage) {
            // Proposals were decided elsewhere and this page no longer exists.
            this.page.set(lastPage);
            this.fetch(quiet);
            return;
          }

          const previous = this.items();
          this.items.set(inbox.items);
          this.groups.set(this.grouped() ? inbox.groups ?? null : null);
          this.markNewOf(inbox);
          if (quiet) {
            this.proposalList()?.holdClosed(previous, inbox.items);
          }
          this.total.set(inbox.total);
          this.loading.set(false);
          this.loadFailed.set(false);
          this.loaded.set(true);
          this.recordVisitOf(inbox, divisionId);
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

  /**
   * Marks what is new: what the server marks since the visit this view recorded, and what was made
   * after the person's visit before the inbox was opened, on any page. Before the first visit
   * everything is new. A server without visits marks nothing.
   */
  private markNewOf(inbox: AgentInbox): void {
    if (!this.markNew() || inbox.newApprovalIds === undefined) {
      this.newIds.set(new Set());
      this.previousVisit.set(undefined);
      return;
    }

    const scopeKey = inbox.scopeKey ?? 'global';
    if (this.opened?.scopeKey !== scopeKey) {
      this.opened = { scopeKey, previousVisit: inbox.lastVisitedAt ?? null, newIds: new Set() };
    }

    const opened = this.opened;
    const before = opened.previousVisit === null ? null : Date.parse(opened.previousVisit);
    for (const id of inbox.newApprovalIds) {
      opened.newIds.add(id);
    }
    for (const proposal of inbox.items) {
      if (before === null || Date.parse(proposal.createdAt) > before) {
        opened.newIds.add(proposal.approvalId);
      }
    }

    this.previousVisit.set(opened.previousVisit);
    this.newIds.set(new Set(inbox.items
      .filter(proposal => opened.newIds.has(proposal.approvalId))
      .map(proposal => proposal.approvalId)));
  }

  /**
   * Records the visit of the shown scope after its first read, so that read still marks what arrived
   * since the visit before.
   */
  private recordVisitOf(inbox: AgentInbox, divisionId: string | null): void {
    const scopeKey = inbox.scopeKey;
    if (!this.recordVisit() || scopeKey === undefined || this.visited === scopeKey) {
      return;
    }

    this.visited = scopeKey;
    this.visitSubscription?.unsubscribe();
    // A visit that cannot be recorded only means the next visit shows more as new.
    this.visitSubscription = this.api.visitInbox(divisionId).subscribe({ error: () => undefined });
  }
}
