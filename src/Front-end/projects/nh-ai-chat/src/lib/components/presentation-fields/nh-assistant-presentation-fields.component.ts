import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ApprovalPresentationField } from '../../models/assistant-api.models';
import { NhAssistantCardPlaceholderComponent, nhAssistantFieldsHeight } from '../../internal/nh-assistant-card-placeholder.component';
import type { NhAssistantPresentationFieldsMode } from './nh-assistant-presentation-field-change';
import { NhAssistantPresentationFieldsViewComponent } from './nh-assistant-presentation-fields-view.component';

export type { NhAssistantPresentationFieldsMode, NhAssistantPresentedField } from './nh-assistant-presentation-field-change';
export { nhAssistantFieldChange } from './nh-assistant-presentation-field-change';

/**
 * The named facts of an approval or result card as a description list. A field of a change shows what
 * it holds now and what it gets, each with its own label ("Now" and "Proposed", or "Before" and
 * "After" for a result), and a badge that says in words whether it changes, gets a new value, is cleared
 * or stays unchanged, so the change never depends on color or strike-through alone. Plain facts show
 * their value as before. Applications may use it in their own result renderers.
 *
 * The list loads on first use, in a chunk of its own. Until it has arrived, and on the server, an empty
 * box of about its height holds its place.
 */
@Component({
  selector: 'nh-assistant-presentation-fields',
  standalone: true,
  imports: [NhAssistantCardPlaceholderComponent, NhAssistantPresentationFieldsViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @defer (on immediate) {
      <nh-assistant-presentation-fields-view [fields]="fields()" [mode]="mode()" />
    } @placeholder {
      <nh-assistant-card-placeholder [height]="placeholderHeight()" [outlined]="false" />
    }
  `,
  styles: `
    :host {
      display: block;
    }
  `
})
export class NhAssistantPresentationFieldsComponent {
  readonly fields = input.required<readonly ApprovalPresentationField[]>();
  /** `proposal` (default) for an approval, `result` for what a call changed. */
  readonly mode = input<NhAssistantPresentationFieldsMode>('proposal');

  readonly placeholderHeight = computed(() => nhAssistantFieldsHeight(this.fields()));
}
