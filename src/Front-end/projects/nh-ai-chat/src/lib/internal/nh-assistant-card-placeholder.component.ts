import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { ApprovalPresentationField } from '../models/assistant-api.models';
import { nhAssistantFieldChange } from '../components/presentation-fields/nh-assistant-presentation-field-change';

/**
 * Holds the place of a card whose view loads on first use (internal): an empty box of about the card's
 * height, hidden from assistive technology, so the thread does not jump when the view arrives. On the
 * server, and until the view has loaded in the browser, it is what such a card renders. `outlined`
 * draws the card's border; a list inside another card, such as its fields, leaves it out.
 */
@Component({
  selector: 'nh-assistant-card-placeholder',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
  styles: `
    :host {
      display: block;
      box-sizing: border-box;
      border-radius: var(--nha-radius-control, 12px);
    }

    :host(.outlined) {
      border: 1px solid var(--nha-border, transparent);
    }
  `,
  host: {
    'aria-hidden': 'true',
    '[class.outlined]': 'outlined()',
    '[style.min-height.px]': 'height()'
  }
})
export class NhAssistantCardPlaceholderComponent {
  /** The estimated height of the card, in pixels. */
  readonly height = input(0);
  /** Whether the box shows the card's border. */
  readonly outlined = input(true);
}

/**
 * The height of presentation fields as a list, for a placeholder: a line for a plain or unchanged
 * field, three for a change (its badge, now and proposed), and the gap between fields.
 */
export function nhAssistantFieldsHeight(fields: readonly ApprovalPresentationField[]): number {
  if (fields.length === 0) {
    return 0;
  }

  const lines = fields.reduce((height, field) => {
    const change = nhAssistantFieldChange(field);
    return height + (change === null || change === 'unchanged' ? 20 : 66);
  }, 0);
  return lines + 6 * (fields.length - 1);
}
