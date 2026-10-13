import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  viewChild
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, forkJoin, map } from 'rxjs';
import {
  AgentFeedbackOutcome,
  AgentProposalFeedback,
  AgentProposalFeedbackCategory,
  NH_ASSISTANT_FEEDBACK_CATEGORIES,
  NH_ASSISTANT_MAX_BULK_FEEDBACK,
  NH_ASSISTANT_MAX_FEEDBACK_TEXT
} from '../../models/assistant-agent-run.models';
import { nhAssistantErrorKeys, toNhAssistantError } from '../../internal/nh-assistant-errors';
import { NhAssistantIconComponent } from '../../internal/nh-assistant-icon.component';
import { NhAssistantTranslatePipe } from '../../internal/nh-assistant-translate.pipe';
import { NhAssistantAgentRunApiService } from '../../services/nh-assistant-agent-run-api.service';
import { nhAssistantErrorMessageKey } from '../../services/nh-assistant-transport';
import type { NhAssistantError } from '../../services/nh-assistant.store';

/** What `nh-assistant-proposal-feedback` stored. */
export interface NhAssistantProposalFeedbackSaved {
  /** The proposals whose feedback the server stored. */
  approvalIds: string[];
  category: AgentProposalFeedbackCategory;
  text: string | null;
}

const notAcceptedCode = 'assistant-feedback-not-accepted';

let nextId = 0;

/**
 * Asks, after a rejection, what was wrong with one or more agent proposals: a category from a fixed
 * set and an optional text. It is optional and never blocks the decision; people skip it with one
 * action. One proposal is sent with `PUT approvals/{id}/feedback`, several rejected together with
 * `POST approvals/feedback` as bulk feedback. With `initial` it changes the caller's own feedback.
 * After saving it thanks the person in place of the form; hosts remove it after `dismissed`.
 */
@Component({
  selector: 'nh-assistant-proposal-feedback',
  standalone: true,
  imports: [TranslatePipe, NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-proposal-feedback.component.html',
  styleUrl: './nh-assistant-proposal-feedback.component.scss'
})
export class NhAssistantProposalFeedbackComponent {
  private readonly api = inject(NhAssistantAgentRunApiService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly thanks = viewChild<ElementRef<HTMLElement>>('thanks');
  private subscription?: Subscription;

  /** The proposals the feedback is about; several when they were rejected together. */
  readonly approvalIds = input.required<readonly string[]>();
  /** The caller's current feedback, to change it; `null` for new feedback. */
  readonly initial = input<AgentProposalFeedback | null>(null);
  /** Emits after the server stored the feedback. */
  readonly saved = output<NhAssistantProposalFeedbackSaved>();
  /** Emits when the person skips the question. */
  readonly dismissed = output<void>();

  readonly id = `nh-assistant-proposal-feedback-${nextId++}`;
  readonly categories = NH_ASSISTANT_FEEDBACK_CATEGORIES;
  readonly maxText = NH_ASSISTANT_MAX_FEEDBACK_TEXT;

  /** A new question when the host passes other proposals or other initial feedback. */
  private readonly question = computed(() => ({ approvalIds: this.approvalIds(), initial: this.initial() }));

  readonly category = linkedSignal<AgentProposalFeedbackCategory | null>(() => this.question().initial?.category ?? null);
  readonly text = linkedSignal(() => this.question().initial?.text ?? '');
  readonly busy = signal(false);
  readonly done = linkedSignal({ source: this.question, computation: () => false });
  /** Set when a bulk request stored the feedback for only part of the proposals. */
  readonly partial = linkedSignal<unknown, { stored: number; total: number } | null>({
    source: this.question,
    computation: () => null
  });
  readonly error = linkedSignal<unknown, NhAssistantError | null>({ source: this.question, computation: () => null });
  readonly errorKeys = computed(() => nhAssistantErrorKeys(this.error()));
  readonly count = computed(() => new Set(this.approvalIds()).size);
  readonly textLength = computed(() => this.text().length);
  readonly tooLong = computed(() => this.textLength() > this.maxText);
  readonly canSend = computed(() => this.category() !== null && this.count() > 0 && !this.busy() && !this.tooLong());

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscription?.unsubscribe());
  }

  choose(category: AgentProposalFeedbackCategory): void {
    this.category.set(category);
    this.error.set(null);
  }

  onText(event: Event): void {
    this.text.set((event.target as HTMLTextAreaElement).value);
  }

  dismiss(): void {
    this.subscription?.unsubscribe();
    this.busy.set(false);
    this.dismissed.emit();
  }

  send(): void {
    const category = this.category();
    if (!this.canSend() || category === null) {
      return;
    }

    const approvalIds = [...new Set(this.approvalIds())];
    const text = this.text().trim() || null;
    this.busy.set(true);
    this.error.set(null);
    this.subscription?.unsubscribe();
    this.subscription = this.request(approvalIds, category, text).subscribe({
      next: outcomes => {
        this.busy.set(false);
        const stored = outcomes.filter(outcome => outcome.succeeded).map(outcome => outcome.approvalId);
        if (stored.length === 0) {
          const code = outcomes.find(outcome => outcome.code)?.code ?? notAcceptedCode;
          this.error.set({ code, messageKey: nhAssistantErrorMessageKey(code) });
          return;
        }

        // The form disappears; keep keyboard focus on the confirmation instead of losing it.
        const focused = this.host.nativeElement.contains(this.document.activeElement);
        this.partial.set(stored.length < approvalIds.length ? { stored: stored.length, total: approvalIds.length } : null);
        this.done.set(true);
        if (focused) {
          afterNextRender(() => this.thanks()?.nativeElement.focus(), { injector: this.injector });
        }
        this.saved.emit({ approvalIds: stored, category, text });
      },
      error: failure => {
        this.busy.set(false);
        this.error.set(toNhAssistantError(failure));
      }
    });
  }

  /** One proposal through its own endpoint; several in requests of at most the bulk limit. */
  private request(
    approvalIds: string[],
    category: AgentProposalFeedbackCategory,
    text: string | null
  ): Observable<AgentFeedbackOutcome[]> {
    if (approvalIds.length === 1) {
      const [approvalId] = approvalIds;
      return this.api.giveFeedback(approvalId, category, text).pipe(
        map(() => [{ approvalId, succeeded: true, code: null }])
      );
    }

    const chunks: string[][] = [];
    for (let index = 0; index < approvalIds.length; index += NH_ASSISTANT_MAX_BULK_FEEDBACK) {
      chunks.push(approvalIds.slice(index, index + NH_ASSISTANT_MAX_BULK_FEEDBACK));
    }
    return forkJoin(chunks.map(chunk => this.api.giveFeedbackMany(chunk, category, text))).pipe(
      map(responses => responses.flatMap(response => response.results))
    );
  }
}
