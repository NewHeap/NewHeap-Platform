import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { QuestionPart, QuestionStatus } from '../../models/assistant-api.models';
import { NhAssistantIconComponent, NhAssistantIconName } from '../../internal/nh-assistant-icon.component';
import { formatNhAssistantCountdown } from '../approval-card/nh-assistant-approval-card.component';

let nextId = 0;

const statusIcons: Record<QuestionStatus, NhAssistantIconName> = {
  pending: 'clock',
  answered: 'check',
  expired: 'x',
  cancelled: 'stop'
};

/**
 * A question the agent asked with `ask_question`: the question with one button per choice for the
 * person it is addressed to (`canAnswer`), and read-only for everyone else, who sees whom it waits
 * for. It counts down to the end of its wait. Once answered it marks the chosen answer; an expired or
 * cancelled question says so. An answer is emitted at most once until the host reports that it is no
 * longer answering.
 */
@Component({
  selector: 'nh-assistant-question-card',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-question-card.component.html',
  styleUrl: './nh-assistant-question-card.component.scss',
  host: {
    '[attr.data-status]': 'effectiveStatus()'
  }
})
export class NhAssistantQuestionCardComponent {
  readonly question = input.required<QuestionPart>();
  /** True when the current user is the addressee and may answer, for example `NhAssistantStore.canAnswer`. */
  readonly canAnswer = input(false);
  /** Name of the person the question is addressed to, shown to everyone else; `null` when unknown. */
  readonly addresseeName = input<string | null>(null);
  /** True while the host submits an answer; disables the choices. */
  readonly answering = input(false);
  /** The zero-based index of the chosen answer. */
  readonly answer = output<number>();

  readonly questionId = `nh-assistant-question-${nextId++}`;
  private readonly now = signal(Date.now());
  private readonly submitted = signal(false);

  readonly remainingMs = computed(() => Date.parse(this.question().expiresAt) - this.now());
  /** A pending question whose wait ended reads as expired before the server says so. */
  readonly effectiveStatus = computed<QuestionStatus>(() =>
    this.question().status === 'pending' && this.remainingMs() <= 0 ? 'expired' : this.question().status);
  readonly actionable = computed(() => this.effectiveStatus() === 'pending');
  readonly statusIcon = computed(() => statusIcons[this.effectiveStatus()]);
  readonly disabled = computed(() => !this.actionable() || !this.canAnswer() || this.answering() || this.submitted());
  readonly chosen = computed(() => this.question().answerIndex ?? null);
  readonly chosenText = computed(() => {
    const index = this.chosen();
    return index === null ? null : this.question().choices[index] ?? null;
  });
  readonly countdown = computed(() => formatNhAssistantCountdown(this.remainingMs()));

  constructor() {
    // Tick only while the question waits.
    effect(onCleanup => {
      if (this.question().status !== 'pending') {
        return;
      }

      const expiresAt = Date.parse(this.question().expiresAt);
      const timer = setInterval(() => {
        this.now.set(Date.now());
        if (Date.now() >= expiresAt) {
          clearInterval(timer);
        }
      }, 1000);
      onCleanup(() => clearInterval(timer));
    });

    // Allow another attempt once the host finished and the question still waits.
    effect(() => {
      if (!this.answering() && this.question().status === 'pending') {
        this.submitted.set(false);
      }
    });
  }

  /** Emits the chosen answer once. */
  choose(index: number): void {
    if (this.disabled() || index < 0 || index >= this.question().choices.length) {
      return;
    }

    this.submitted.set(true);
    this.answer.emit(index);
  }
}
