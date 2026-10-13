import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import {
  AgentProposal,
  AgentThreadLookup,
  AgentTriggerReceipt,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
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
 * subject.
 */
@Component({
  selector: 'nh-assistant-agent-thread-view',
  standalone: true,
  imports: [
    TranslatePipe,
    NhAssistantTranslatePipe,
    NhAssistantIconComponent,
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
  private subscription?: Subscription;
  private proposalSubscription?: Subscription;
  private startSubscription?: Subscription;

  readonly agentId = input.required<string>();
  readonly subjectType = input.required<string>();
  readonly subjectId = input.required<string>();
  /** Emits the thread's conversation id, so the host can open it in the assistant panel. */
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
  }

  load(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.subscription?.unsubscribe();
    this.subscription = this.api.getThread(this.agentId(), this.subjectType(), this.subjectId()).subscribe({
      next: lookup => {
        this.lookup.set(lookup);
        if (!lookup.thread?.activeRun && this.receipt()?.runId) {
          // The run the receipt announced has ended; the run timeline shows how.
          this.receipt.set(null);
        }
        this.loading.set(false);
        this.loadProposals();
      },
      error: () => {
        this.loading.set(false);
        this.loadFailed.set(true);
      }
    });
  }

  loadProposals(): void {
    const thread = this.thread();
    this.proposalSubscription?.unsubscribe();
    if (!thread) {
      this.proposals.set([]);
      return;
    }
    this.proposalSubscription = this.api.getProposals(thread.conversationId).subscribe({
      next: proposals => this.proposals.set(proposals),
      error: () => this.proposals.set([])
    });
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
}
