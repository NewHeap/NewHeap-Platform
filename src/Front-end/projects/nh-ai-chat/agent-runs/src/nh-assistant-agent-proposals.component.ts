import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription } from 'rxjs';
import {
  AgentExecutionResolution,
  AgentProposal,
  AgentProposalDecision,
  NhAssistantAgentRunApiService,
  NhAssistantError,
  ɵnhAssistantErrorKeys as nhAssistantErrorKeys,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe,
  ɵtoNhAssistantError as toNhAssistantError
} from '@newheap/platform-ai-chat';

/**
 * A list of agent proposals with their decision and execution state. People who may decide see
 * approve and reject on pending proposals and the resolution of an execution with an unknown
 * outcome; the server checks every decision again. With `selectable`, pending proposals can be
 * approved or rejected together.
 */
@Component({
  selector: 'nh-assistant-agent-proposals',
  standalone: true,
  imports: [DatePipe, TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-agent-proposals.component.html',
  styleUrl: './nh-assistant-agent-runs.scss'
})
export class NhAssistantAgentProposalsComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private subscription?: Subscription;

  readonly proposals = input.required<AgentProposal[]>();
  /** Whether the caller may decide: an approver of the subjects or the inbox owner. */
  readonly canDecide = input(false);
  readonly selectable = input(false);
  /** Emits after a proposal changed, so the host reloads its list. */
  readonly changed = output<void>();

  readonly busy = signal(false);
  readonly reason = signal('');
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly error = signal<NhAssistantError | null>(null);
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly pending = computed(() => this.proposals().filter(proposal => proposal.status === 'pending'));
  readonly allSelected = computed(() =>
    this.pending().length > 0 && this.pending().every(proposal => this.selected().has(proposal.approvalId)));

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  onReason(event: Event): void {
    this.reason.set((event.target as HTMLInputElement).value);
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
    this.run(this.api.decide(proposal, decision, this.reason()));
  }

  decideSelected(decision: AgentProposalDecision): void {
    const ids = [...this.selected()];
    if (ids.length > 0) {
      this.run(this.api.decideMany(ids, decision, this.reason()), () => this.selected.set(new Set()));
    }
  }

  resolve(proposal: AgentProposal, action: AgentExecutionResolution): void {
    this.run(this.api.resolve(proposal.approvalId, action, this.reason()));
  }

  private run(request: Observable<unknown>, done?: () => void): void {
    this.busy.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = request.subscribe({
      next: () => {
        this.busy.set(false);
        this.reason.set('');
        done?.();
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
