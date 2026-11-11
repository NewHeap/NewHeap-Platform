import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalPresentationField, PresentationFieldChange } from '../../models/assistant-api.models';

/**
 * Whether a field shows what a proposal would change (`proposal`: now and proposed) or what a call
 * changed (`result`: before and after).
 */
export type NhAssistantPresentationFieldsMode = 'proposal' | 'result';

/** A field as the list shows it, with how it changes; `null` for a plain fact. */
export interface NhAssistantPresentedField {
  label: string;
  value: string;
  currentValue: string | null;
  change: PresentationFieldChange | null;
}

/**
 * How a field changes: the server's `change`, or, from a server that sends only the current value,
 * the change read from both values; `null` for a plain fact.
 */
export function nhAssistantFieldChange(field: ApprovalPresentationField): PresentationFieldChange | null {
  if (field.change) {
    return field.change;
  }
  if (field.currentValue === undefined || field.currentValue === null) {
    return null;
  }

  const had = field.currentValue.trim().length > 0;
  const has = field.value.trim().length > 0;
  if (had && has) {
    return field.currentValue === field.value ? 'unchanged' : 'changed';
  }

  return has ? 'added' : 'cleared';
}

/**
 * The named facts of an approval or result card as a description list. A field of a change shows what
 * it holds now and what it gets, each with its own label ("Now" and "Proposed", or "Before" and
 * "After" for a result), and a badge that says in words whether it changes, gets a new value, is cleared
 * or stays unchanged, so the change never depends on color or strike-through alone. Plain facts show
 * their value as before. Applications may use it in their own result renderers.
 */
@Component({
  selector: 'nh-assistant-presentation-fields',
  standalone: true,
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-presentation-fields.component.html',
  styleUrl: './nh-assistant-presentation-fields.component.scss'
})
export class NhAssistantPresentationFieldsComponent {
  readonly fields = input.required<readonly ApprovalPresentationField[]>();
  /** `proposal` (default) for an approval, `result` for what a call changed. */
  readonly mode = input<NhAssistantPresentationFieldsMode>('proposal');

  readonly presented = computed<NhAssistantPresentedField[]>(() => this.fields().map(field => ({
    label: field.label,
    value: field.value,
    currentValue: field.currentValue ?? null,
    change: nhAssistantFieldChange(field)
  })));

  /** The translation key prefix of the change badges of the mode. */
  readonly changeKeys = computed(() => this.mode() === 'result'
    ? 'nh-assistant.presentation.change-result.'
    : 'nh-assistant.presentation.change.');
  readonly currentKey = computed(() => this.mode() === 'result'
    ? 'nh-assistant.presentation.before'
    : 'nh-assistant.presentation.current');
  readonly proposedKey = computed(() => this.mode() === 'result'
    ? 'nh-assistant.presentation.after'
    : 'nh-assistant.presentation.proposed');
}
