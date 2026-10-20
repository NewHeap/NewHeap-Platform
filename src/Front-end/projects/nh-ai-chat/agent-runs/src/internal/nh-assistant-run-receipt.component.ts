import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import {
  AgentTriggerOutcome,
  AgentTriggerReceipt,
  ɵNhAssistantIconComponent as NhAssistantIconComponent,
  ɵNhAssistantTranslatePipe as NhAssistantTranslatePipe
} from '@newheap/platform-ai-chat';

/**
 * How a start by hand reads: `success` when a run started or will start after the current one,
 * `warning` while the agent or its skill is paused (`waiting`), and `info` when nothing started yet
 * for another reason (`deferred` until the application's change is saved, `ignored`).
 */
export type NhAssistantRunReceiptTone = 'success' | 'warning' | 'info';

/** The tone of a receipt's outcome (internal). */
export function nhAssistantRunReceiptTone(outcome: AgentTriggerOutcome): NhAssistantRunReceiptTone {
  switch (outcome) {
    case 'waiting':
      return 'warning';
    case 'deferred':
    case 'ignored':
      return 'info';
    default:
      return 'success';
  }
}

/**
 * The receipt of a start by hand (internal): what happens now, in the presentation its outcome
 * needs. A paused agent or skill reads as a warning with the reason the server named, never as a
 * started run.
 */
@Component({
  selector: 'nh-assistant-run-receipt',
  standalone: true,
  imports: [NhAssistantTranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p
      class="message"
      role="status"
      [class.success]="tone() === 'success'"
      [class.warning]="tone() === 'warning'"
      [class.info]="tone() === 'info'"
      [attr.data-outcome]="receipt().outcome"
      [attr.data-receipt-focus]="focusable() ? '' : null"
      [attr.tabindex]="focusable() ? -1 : null">
      <nh-assistant-icon [name]="icon()" />
      <span>{{ keys() | nhAssistantTranslate: receipt().outcome }}</span>
    </p>
  `,
  styleUrl: './nh-assistant-run-receipt.component.scss'
})
export class NhAssistantRunReceiptComponent {
  readonly receipt = input.required<AgentTriggerReceipt>();
  /** The receipt takes the focus after the start, as in the run-now dialog. */
  readonly focusable = input(false);

  readonly tone = computed(() => nhAssistantRunReceiptTone(this.receipt().outcome));
  readonly icon = computed(() => {
    switch (this.tone()) {
      case 'warning':
        return 'warning' as const;
      case 'info':
        return 'clock' as const;
      default:
        return 'check' as const;
    }
  });
  /** The text of the reason a waiting start names first, then the text of its outcome. */
  readonly keys = computed(() => {
    const receipt = this.receipt();
    const outcomeKey = `nh-assistant.agent-runs.thread.outcome.${receipt.outcome}`;
    return receipt.outcome === 'waiting' && receipt.reasonCode
      ? [`nh-assistant.agent-runs.thread.waiting.${receipt.reasonCode}`, outcomeKey]
      : [outcomeKey];
  });
}
