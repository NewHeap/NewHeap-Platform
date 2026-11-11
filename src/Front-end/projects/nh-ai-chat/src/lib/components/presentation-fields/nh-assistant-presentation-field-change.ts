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
