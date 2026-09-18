import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  output,
  signal
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalDecision, ApprovalPart } from '../../models/assistant-api.models';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';

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
 * the host reports that it is no longer deciding.
 */
@Component({
  selector: 'nh-assistant-approval-card',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
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
  readonly decide = output<ApprovalDecision>();

  private readonly now = signal(Date.now());
  private readonly submitted = signal(false);

  readonly remainingMs = computed(() => Date.parse(this.approval().expiresAt) - this.now());
  readonly expired = computed(() => this.approval().status === 'expired' ||
    (this.approval().status === 'pending' && this.remainingMs() <= 0));
  readonly effectiveStatus = computed(() => this.expired() ? 'expired' : this.approval().status);
  readonly actionable = computed(() => this.effectiveStatus() === 'pending');
  readonly disabled = computed(() => !this.actionable() || this.deciding() || this.submitted());
  readonly countdown = computed(() => formatNhAssistantCountdown(this.remainingMs()));
  readonly presentedSummary = computed(() => this.approval().presentation?.summary ?? null);

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
}
