import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  afterNextRender,
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
  AgentProposalDecisionRight,
  AgentSubjectWork,
  AgentSubjectWorkAgent,
  AgentThreadLookup,
  AgentTriggerReceipt,
  NhAssistantAgentRunApiService,
  NhAssistantApiError,
  NhAssistantError,
  NhAssistantPanelService,
  NhAssistantStore,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantRunReceiptComponent } from './internal/nh-assistant-run-receipt.component';
import { NhAssistantAgentProposalsComponent } from './nh-assistant-agent-proposals.component';

let nextSuggestionsId = 0;

/** The outcome of starting one agent on the subject. */
interface NhAssistantStartedAgent {
  agentId: string;
  receipt: AgentTriggerReceipt;
}

/**
 * The agent work on one subject of the application, for example in a project's dialog: the open
 * proposals of every agent on the subject with the viewer's own decision right, including who
 * decides when the viewer may not and the optional question after a rejection, and per agent the
 * conversation about the subject and, for participants, a start action. `agentId` names the agent
 * whose conversation the host offers also before it worked on the subject. With live updates the
 * view reloads by itself when agent work on its subject changes; a proposal that closes because it
 * no longer fits stays as a closed card with the reason until it is dismissed. Opening a
 * conversation uses `NhAssistantPanelService.openSubject` and emits `conversationOpened` afterwards;
 * opening the conversation of a run that placed a proposal here emits `runConversationOpened`.
 */
@Component({
  selector: 'nh-assistant-subject-suggestions',
  standalone: true,
  imports: [
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantAgentProposalsComponent,
    NhAssistantRunReceiptComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-subject-suggestions.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantSubjectSuggestionsComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly panel = inject(NhAssistantPanelService);
  private readonly store = inject(NhAssistantStore);
  private readonly proposalList = viewChild(NhAssistantAgentProposalsComponent);
  private subscription?: Subscription;
  private startSubscription?: Subscription;
  private destroyed = false;

  /** The application's kind of subject, for example `project`. */
  readonly subjectType = input.required<string>();
  readonly subjectId = input.required<string>();
  /** The agent whose conversation about the subject the host offers, also before it worked on it. */
  readonly agentId = input<string | undefined>(undefined);
  /** Emits once the panel opens the conversation, so a host in a dialog can make room for it. */
  readonly conversationOpened = output<AgentThreadLookup>();
  /**
   * Emits once the panel opens the conversation of a run that placed a proposal here from another
   * subject, so a host in a dialog can make room for it.
   */
  readonly runConversationOpened = output<string>();

  readonly headingId = `nh-assistant-subject-suggestions-${++nextSuggestionsId}`;
  readonly loading = signal(true);
  /** True after the first successful load; later loads keep the list, and an open feedback question, in place. */
  readonly loaded = signal(false);
  readonly loadFailed = signal(false);
  /** The caller has no role on the subject, or it no longer exists. */
  readonly unavailable = signal(false);
  readonly work = signal<AgentSubjectWork | null>(null);
  /** The agent whose conversation is opening or which is starting. */
  readonly opening = signal<string | null>(null);
  readonly starting = signal<string | null>(null);
  readonly started = signal<NhAssistantStartedAgent | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  /** True while the assistant panel can show a conversation. */
  readonly assistantEnabled = this.store.enabled;
  readonly agents = computed<AgentSubjectWorkAgent[]>(() => this.work()?.agents ?? []);
  readonly proposals = computed(() => this.work()?.openProposals.map(item => item.proposal) ?? []);
  readonly decisionRights = computed<ReadonlyMap<string, AgentProposalDecisionRight>>(() =>
    new Map((this.work()?.openProposals ?? []).map(item => [
      item.proposal.approvalId,
      { canDecide: item.canDecide, reasonCode: item.reasonCode }
    ])));

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.subscription?.unsubscribe();
      this.startSubscription?.unsubscribe();
    });
    // Browser only: the access policy and the status need the signed-in user.
    afterNextRender(() => {
      void this.store.initialize();
    });
    effect(() => {
      this.subjectType();
      this.subjectId();
      this.agentId();
      untracked(() => {
        this.work.set(null);
        this.loaded.set(false);
        this.started.set(null);
        this.error.set(null);
        this.load();
      });
    });
    nhAssistantReloadOnAgentWork(
      change => change.subjectType === this.subjectType() && change.subjectId === this.subjectId(),
      () => this.refresh()
    );
  }

  load(): void {
    this.fetch(false);
  }

  /** The translation keys of an agent's name: from the server's status, then the convention. */
  agentNameKeys(agentId: string): string[] {
    const known = this.store.agents().find(agent => agent.id === agentId)?.displayNameKey;
    return known ? [known, `nh-assistant.agents.${agentId}.name`] : [`nh-assistant.agents.${agentId}.name`];
  }

  /** Readers may open an existing conversation; creating one needs the participant role. */
  canOpen(agent: AgentSubjectWorkAgent): boolean {
    return agent.thread !== null || this.work()?.role !== 'reader';
  }

  /**
   * Opens the conversation of the run that placed a proposal here, such as a run for the whole
   * application, in the assistant panel; the server checks the reader's access to it.
   */
  openRunConversation(conversationId: string): void {
    this.panel.open(conversationId);
    this.runConversationOpened.emit(conversationId);
  }

  async open(agent: AgentSubjectWorkAgent): Promise<void> {
    this.opening.set(agent.agentId);
    this.error.set(null);
    try {
      const lookup = await this.panel.openSubject(agent.agentId, { type: this.subjectType(), id: this.subjectId() });
      if (!this.destroyed) {
        this.conversationOpened.emit(lookup);
        if (!agent.thread) {
          // The conversation now exists; show it as the agent's thread.
          this.fetch(true);
        }
      }
    } catch (failure) {
      if (!this.destroyed) {
        this.error.set(toNhAssistantError(failure));
      }
    } finally {
      if (!this.destroyed) {
        this.opening.set(null);
      }
    }
  }

  start(agent: AgentSubjectWorkAgent): void {
    this.starting.set(agent.agentId);
    this.error.set(null);
    this.started.set(null);
    this.startSubscription?.unsubscribe();
    this.startSubscription = this.api
      .startAgent({ agentId: agent.agentId, subjectType: this.subjectType(), subjectId: this.subjectId() })
      .subscribe({
        next: receipt => {
          this.starting.set(null);
          this.started.set({ agentId: agent.agentId, receipt });
          this.fetch(true);
        },
        error: failure => {
          this.starting.set(null);
          this.error.set(toNhAssistantError(failure));
        }
      });
  }

  /**
   * Reloads after a live update. Once the work is shown it reloads quietly: no loading state, and
   * a failure keeps what is shown, including an open feedback question. While a decision of the
   * list is in flight it waits, because the decision reloads the list when it ends.
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
      this.unavailable.set(false);
    }

    this.subscription?.unsubscribe();
    this.subscription = this.api.getSubjectWork(this.subjectType(), this.subjectId(), this.agentId()).subscribe({
      next: work => {
        const previous = this.proposals();
        this.work.set(work);
        if (quiet) {
          this.proposalList()?.holdClosed(previous, this.proposals());
        }
        const started = this.started();
        const startedAgent = started ? work.agents.find(agent => agent.agentId === started.agentId) : undefined;
        if (started?.receipt.runId && !startedAgent?.thread?.activeRun) {
          // The run the start announced has ended; its proposals are in the list.
          this.started.set(null);
        }
        this.loading.set(false);
        this.loadFailed.set(false);
        this.unavailable.set(false);
        this.loaded.set(true);
      },
      error: failure => {
        if (quiet) {
          return;
        }

        this.loading.set(false);
        if (failure instanceof NhAssistantApiError && failure.status === 404) {
          this.unavailable.set(true);
        } else {
          this.loadFailed.set(true);
        }
      }
    });
  }
}
