import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  output,
  signal,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalDecision, ApprovalPart } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import type { NhAssistantApprovalWaitingFor } from '../../services/nh-assistant.store';
import { NhAssistantProposalFeedbackComponent } from '../proposal-feedback/nh-assistant-proposal-feedback.component';

let nextId = 0;

/** Formats remaining milliseconds as `m:ss`, or `h:mm:ss` above one hour. */
export function formatNhAssistantCountdown(milliseconds: number): string {
  const totalSeconds = Math.max(0, Math.ceil(milliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const paddedSeconds = seconds.toString().padStart(2, '0');

  return hours > 0
    ? `${hours}:${minutes.toString().padStart(2, '0')}:${paddedSeconds}`
    : `${minutes}:${paddedSeconds}`;
}

/**
 * Asks the user to approve or reject a proposed mutation. Shows the summary, targets,
 * proposed arguments and a countdown to expiry. A decision is emitted at most once until
 * the host reports that it is no longer deciding. After the user rejected an agent's proposal
 * (`canGiveFeedback`), a low-key action asks what was wrong. A proposal that closed because it no
 * longer fits shows why, such as "Already done", with the application's explanation.
 */
@Component({
  selector: 'nh-assistant-approval-card',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent, NhAssistantProposalFeedbackComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-approval-card.component.html',
  styleUrl: './nh-assistant-approval-card.component.scss',
  host: {
    '[attr.data-status]': 'effectiveStatus()'
  }
})
export class NhAssistantApprovalCardComponent {
  readonly approval = input.required<ApprovalPart>();
  /** True while the host submits a decision; disables both buttons. */
  readonly deciding = input(false);
  /**
   * False when the current user may not decide: in a chat the approval belongs to another
   * participant's turn, in an agent thread the user is no approver of the proposal.
   */
  readonly canDecide = input(true);
  /** Name of the participant who decides a `turn` approval, shown when the current user cannot. */
  readonly decisionOwner = input<string | null>(null);
  /**
   * Who decides when the current user cannot: the author of the turn (`turn`, the default), an
   * approver of the agent thread's subject (`approver`), or another approver because the current
   * user started the work (`other-approver`).
   */
  readonly waitingFor = input<NhAssistantApprovalWaitingFor>('turn');
  /**
   * True when the current user rejected this agent proposal and may say what was wrong, for example
   * from `NhAssistantStore.approvalRights`. Chat approvals never offer feedback.
   */
  readonly canGiveFeedback = input(false);
  readonly decide = output<ApprovalDecision>();

  private readonly now = signal(Date.now());
  private readonly submitted = signal(false);
  /** The feedback question is expanded. */
  readonly feedbackOpen = signal(false);
  /** The feedback was stored; the question thanks the person and the action disappears. */
  readonly feedbackSaved = signal(false);
  private readonly feedbackToggle = viewChild<ElementRef<HTMLButtonElement>>('feedbackToggle');

  readonly remainingMs = computed(() => Date.parse(this.approval().expiresAt) - this.now());
  readonly expired = computed(() => this.approval().status === 'expired' ||
    (this.approval().status === 'pending' && this.remainingMs() <= 0));
  readonly effectiveStatus = computed(() => this.expired() ? 'expired' : this.approval().status);
  /** Translation key of the status: the kind of a stale proposal, otherwise the status itself. */
  readonly statusKey = computed(() => {
    const kind = this.approval().staleKind;
    return this.effectiveStatus() === 'stale' && kind
      ? `nh-assistant.approval.stale-kind.${kind}`
      : `nh-assistant.approval.status.${this.effectiveStatus()}`;
  });
  /** The application's explanation of a stale proposal, such as who already did the work. */
  readonly staleExplanation = computed(() =>
    this.effectiveStatus() === 'stale' ? this.approval().staleExplanation ?? null : null);
  readonly actionable = computed(() => this.effectiveStatus() === 'pending');
  readonly succeeded = computed(() =>
    ['approved', 'executing', 'executed', 'resolved'].includes(this.effectiveStatus()));
  readonly disabled = computed(() => !this.actionable() || !this.canDecide() || this.deciding() || this.submitted());
  readonly countdown = computed(() => formatNhAssistantCountdown(this.remainingMs()));
  readonly presentedSummary = computed(() => this.approval().presentation?.summary ?? null);
  readonly feedbackAvailable = computed(() => this.canGiveFeedback() && this.effectiveStatus() === 'rejected');
  readonly feedbackIds = computed(() => [this.approval().approvalId]);
  readonly feedbackPanelId = `nh-assistant-approval-feedback-${nextId++}`;
  /** Translation key of the text shown instead of the buttons when the current user cannot decide. */
  readonly waitingKey = computed(() => {
    switch (this.waitingFor()) {
      case 'approver':
        return 'nh-assistant.approval.waiting-for-approver';
      case 'other-approver':
        return 'nh-assistant.approval.waiting-for-other-approver';
      default:
        return this.decisionOwner() ? 'nh-assistant.approval.waiting-for' : 'nh-assistant.approval.waiting-for-participant';
    }
  });

  constructor() {
    // Tick only while the approval is pending and not yet expired.
    effect(onCleanup => {
      if (this.approval().status !== 'pending') {
        return;
      }

      const expiresAt = Date.parse(this.approval().expiresAt);
      const timer = setInterval(() => {
        this.now.set(Date.now());
        if (Date.now() >= expiresAt) {
          clearInterval(timer);
        }
      }, 1000);
      onCleanup(() => clearInterval(timer));
    });

    // Allow a new attempt once the host finished deciding and the approval is still pending.
    effect(() => {
      if (!this.deciding() && this.approval().status === 'pending') {
        this.submitted.set(false);
      }
    });
  }

  choose(decision: ApprovalDecision): void {
    if (this.disabled()) {
      return;
    }

    this.submitted.set(true);
    this.decide.emit(decision);
  }

  toggleFeedback(): void {
    this.feedbackOpen.update(open => !open);
  }

  onFeedbackSaved(): void {
    this.feedbackSaved.set(true);
  }

  /** Collapses the question and returns focus to the action that opened it. */
  onFeedbackDismissed(): void {
    this.feedbackOpen.set(false);
    this.feedbackToggle()?.nativeElement.focus();
  }
}
