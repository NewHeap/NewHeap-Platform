import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ApprovalPresentationField } from '../../models/assistant-api.models';
import {
  NhAssistantPresentationFieldsMode,
  NhAssistantPresentedField,
  nhAssistantFieldChange
} from './nh-assistant-presentation-field-change';
import { NhAssistantTokensDirective } from '../../internal/nh-assistant-tokens';

/**
 * The description list of `nh-assistant-presentation-fields` (internal), which loads it on first use,
 * and of the default result card's view, which shows it at once. A field of a change shows what it
 * holds now and what it gets, each with its own label, and a badge that says in words how it changes.
 */
@Component({
  selector: 'nh-assistant-presentation-fields-view',
  standalone: true,
  hostDirectives: [NhAssistantTokensDirective],
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-assistant-presentation-fields-view.component.html',
  styleUrl: './nh-assistant-presentation-fields-view.component.scss'
})
export class NhAssistantPresentationFieldsViewComponent {
  readonly fields = input.required<readonly ApprovalPresentationField[]>();
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
