import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalDecision, ApprovalPart } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantStore, type NhAssistantApprovalWaitingFor } from '../../services/nh-assistant.store';
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
 * longer fits shows why, such as "Already done", with the application's explanation. A proposal of
 * another agent the assistant consulted (`consultation`) names that agent and the subject, offers the
 * agent's answer and says that approving runs it with the user's own rights; with a
 * `consultation.leaveRoute` a third action leaves it for the subject's approvers (`leave`), after which
 * the card says that it waits for them.
 */
@Component({
  selector: 'nh-assistant-approval-card',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent, NhAssistantProposalFeedbackComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-approval-card.component.html',
  styleUrl: './nh-assistant-approval-card.component.scss',
  host: {
    '[attr.data-status]': 'shownStatus()'
  }
})
export class NhAssistantApprovalCardComponent {
  /** Names the consulted agent as the status does; the card also works outside `provideNhAssistant`. */
  private readonly store = inject(NhAssistantStore, { optional: true });

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
  /** The decision this card sent last, so a proposal left for the approvers reads as such at once. */
  private readonly chosen = signal<ApprovalDecision | null>(null);
  /** The feedback question is expanded. */
  readonly feedbackOpen = signal(false);
  /** The feedback was stored; the question thanks the person and the action disappears. */
  readonly feedbackSaved = signal(false);
  private readonly feedbackToggle = viewChild<ElementRef<HTMLButtonElement>>('feedbackToggle');

  readonly remainingMs = computed(() => Date.parse(this.approval().expiresAt) - this.now());
  readonly expired = computed(() => this.approval().status === 'expired' ||
    (this.approval().status === 'pending' && this.remainingMs() <= 0));
  readonly effectiveStatus = computed(() => this.expired() ? 'expired' : this.approval().status);
  /** Where a consulted agent's proposal comes from; `null` for the conversation's own proposals. */
  readonly consultation = computed(() => this.approval().consultation ?? null);
  /**
   * The translation keys of the consulted agent's name: the name the assistant's status gives it,
   * then the convention `nh-assistant.agents.{id}.name`; the id when neither translates.
   */
  readonly consultedAgentKeys = computed(() => {
    const consultation = this.consultation();
    if (!consultation) {
      return [];
    }

    const convention = `nh-assistant.agents.${consultation.agentId}.name`;
    const known = this.store?.agents().find(agent => agent.id === consultation.agentId)?.displayNameKey;
    return known && known !== convention ? [known, convention] : [convention];
  });
  /** The subject the consulted agent was asked about, by name or else by id. */
  readonly consultedSubject = computed(() => {
    const consultation = this.consultation();
    return consultation ? consultation.subjectDisplayName ?? consultation.subjectId : null;
  });
  /**
   * The user left the consulted agent's proposal for the subject's approvers: the server names the
   * proposal that waits there, or this card just sent the decision and it was accepted.
   */
  readonly leftForApprovers = computed(() => {
    const consultation = this.consultation();
    return consultation !== null
      && (!!consultation.movedToApprovalId || (this.chosen() === 'leave' && this.approval().status !== 'pending'));
  });
  /** The status the card shows: `moved` for a proposal left for the subject's approvers. */
  readonly shownStatus = computed(() => this.leftForApprovers() ? 'moved' : this.effectiveStatus());
  /**
   * Translation key of the status: the kind of a stale proposal, "replaced" for a superseded one,
   * "left for approvers" for a consulted agent's proposal the user left, otherwise the status itself.
   */
  readonly statusKey = computed(() => {
    if (this.leftForApprovers()) {
      return 'nh-assistant.approval.consultation.moved-status';
    }

    const kind = this.approval().staleKind;
    if (this.effectiveStatus() === 'stale' && kind) {
      return `nh-assistant.approval.stale-kind.${kind}`;
    }

    return this.effectiveStatus() === 'withdrawn' && this.approval().supersededByApprovalId
      ? 'nh-assistant.approval.superseded'
      : `nh-assistant.approval.status.${this.effectiveStatus()}`;
  });
  /** Why the agent's rules proposed it; model proposals have none. */
  readonly rationale = computed(() => this.approval().rationale ?? null);
  /** The application's explanation of a stale proposal, such as who already did the work. */
  readonly staleExplanation = computed(() =>
    this.effectiveStatus() === 'stale' ? this.approval().staleExplanation ?? null : null);
  readonly actionable = computed(() => this.effectiveStatus() === 'pending');
  readonly succeeded = computed(() =>
    ['approved', 'executing', 'executed', 'resolved'].includes(this.effectiveStatus()));
  readonly disabled = computed(() => !this.actionable() || !this.canDecide() || this.deciding() || this.submitted());
  /** The route at the subject the pending proposal may be left for; `null` when it can only be approved or rejected. */
  readonly leaveRoute = computed(() => this.actionable() ? this.consultation()?.leaveRoute ?? null : null);
  readonly leaveHintId = `nh-assistant-approval-leave-${nextId++}`;
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
      case 'asker':
        return 'nh-assistant.approval.waiting-for-asker';
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

  /** Emits a decision; `leave` only for a consulted agent's proposal with a route at the subject. */
  choose(decision: ApprovalDecision): void {
    if (this.disabled() || (decision === 'leave' && this.leaveRoute() === null)) {
      return;
    }

    this.submitted.set(true);
    this.chosen.set(decision);
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
