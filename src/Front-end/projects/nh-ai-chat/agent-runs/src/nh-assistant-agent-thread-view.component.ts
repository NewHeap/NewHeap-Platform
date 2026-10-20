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
  AgentProposal,
  AgentThreadLookup,
  AgentTriggerReceipt,
  LiveAgentWorkChanged,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵnhAssistantReloadOnAgentWork as nhAssistantReloadOnAgentWork,
  ɵNhAssistantSubjectLinkComponent as NhAssistantSubjectLinkComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';
import { NhAssistantAgentArtifactsComponent } from './nh-assistant-agent-artifacts.component';
import { NhAssistantAgentConsultationsComponent } from './nh-assistant-agent-consultations.component';
import { NhAssistantAgentMemoryComponent } from './nh-assistant-agent-memory.component';
import { NhAssistantAgentProposalsComponent } from './nh-assistant-agent-proposals.component';
import { NhAssistantRunTimelineComponent } from './nh-assistant-run-timeline.component';

export type NhAssistantAgentThreadTab = 'proposals' | 'runs' | 'consultations' | 'memory' | 'artifacts';

/**
 * The agent's work on one subject, for example an "Intake review" tab on a project page: the
 * caller's role, a start action for participants, and the thread's proposals, runs, consultations
 * by other assistants, working memory and files. Everything follows the caller's live role on the
 * subject. With live updates the view reloads by itself when the thread's runs, proposals or
 * feedback change, also when a colleague decided elsewhere.
 */
@Component({
  selector: 'nh-assistant-agent-thread-view',
  standalone: true,
  imports: [
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
    NhAssistantSubjectLinkComponent,
    NhAssistantAgentProposalsComponent,
    NhAssistantRunTimelineComponent,
    NhAssistantAgentMemoryComponent,
    NhAssistantAgentArtifactsComponent,
    NhAssistantAgentConsultationsComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-thread-view.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantAgentThreadViewComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly proposalList = viewChild(NhAssistantAgentProposalsComponent);
  private subscription?: Subscription;
  private proposalSubscription?: Subscription;
  private startSubscription?: Subscription;

  readonly agentId = input.required<string>();
  readonly subjectType = input.required<string>();
  readonly subjectId = input.required<string>();
  /**
   * Emits the thread's conversation id, so the host can open it in the assistant panel; also the
   * conversation of a run that placed one of the thread's proposals here from another subject.
   */
  readonly openConversation = output<string>();

  readonly tab = signal<NhAssistantAgentThreadTab>('proposals');
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly lookup = signal<AgentThreadLookup | null>(null);
  readonly proposals = signal<AgentProposal[]>([]);
  readonly message = signal('');
  readonly starting = signal(false);
  readonly receipt = signal<AgentTriggerReceipt | null>(null);
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly thread = computed(() => this.lookup()?.thread ?? null);
  readonly role = computed(() => this.lookup()?.role ?? 'none');
  readonly tabs: readonly NhAssistantAgentThreadTab[] = ['proposals', 'runs', 'consultations', 'memory', 'artifacts'];

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      this.proposalSubscription?.unsubscribe();
      this.startSubscription?.unsubscribe();
    });
    effect(() => {
      this.agentId();
      this.subjectType();
      this.subjectId();
      untracked(() => this.load());
    });
    nhAssistantReloadOnAgentWork(change => this.concerns(change), () => this.refresh());
  }

  load(): void {
    this.fetch(false);
  }

  loadProposals(): void {
    this.fetchProposals(false);
  }

  onMessage(event: Event): void {
    this.message.set((event.target as HTMLTextAreaElement).value);
  }

  start(): void {
    this.starting.set(true);
    this.error.set(null);
    this.receipt.set(null);
    this.startSubscription?.unsubscribe();
    this.startSubscription = this.api
      .startAgent({
        agentId: this.agentId(),
        subjectType: this.subjectType(),
        subjectId: this.subjectId(),
        message: this.message().trim() || null
      })
      .subscribe({
        next: receipt => {
          this.starting.set(false);
          this.receipt.set(receipt);
          this.message.set('');
          this.load();
        },
        error: failure => {
          this.starting.set(false);
          this.error.set(toNhAssistantError(failure));
        }
      });
  }

  /**
   * Whether a live update is about this view: its own thread, or, before the subject has a thread,
   * a run that may have started it.
   */
  private concerns(change: LiveAgentWorkChanged): boolean {
    const thread = this.thread();
    return thread ? change.conversationId === thread.conversationId : change.reason === 'run';
  }

  /**
   * Reloads after a live update. Once the thread is shown it reloads quietly: no loading state, and
   * a failure keeps what is shown, including an open feedback question. While a decision of the
   * proposal list is in flight only the thread reloads, because the decision reloads the proposals
   * when it ends.
   */
  private refresh(): void {
    const withProposals = this.proposalList()?.busy() !== true;
    this.fetch(this.lookup() !== null && !this.loading() && !this.loadFailed(), withProposals);
  }

  private fetch(quiet: boolean, withProposals = true): void {
    if (!quiet) {
      this.loading.set(true);
      this.loadFailed.set(false);
    }

    this.subscription?.unsubscribe();
    this.subscription = this.api.getThread(this.agentId(), this.subjectType(), this.subjectId()).subscribe({
      next: lookup => {
        this.lookup.set(lookup);
        if (!lookup.thread?.activeRun && this.receipt()?.runId) {
          // The run the receipt announced has ended; the run timeline shows how.
          this.receipt.set(null);
        }
        this.loading.set(false);
        this.loadFailed.set(false);
        if (withProposals) {
          this.fetchProposals(quiet);
        }
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

  private fetchProposals(quiet: boolean): void {
    const thread = this.thread();
    this.proposalSubscription?.unsubscribe();
    if (!thread) {
      this.proposals.set([]);
      return;
    }

    this.proposalSubscription = this.api.getProposals(thread.conversationId).subscribe({
      next: proposals => this.proposals.set(proposals),
      error: () => {
        if (!quiet) {
          this.proposals.set([]);
        }
      }
    });
  }
}
