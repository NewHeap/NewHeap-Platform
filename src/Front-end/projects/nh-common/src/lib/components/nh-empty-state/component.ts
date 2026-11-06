import {ChangeDetectionStrategy, Component, computed, input} from '@angular/core';
import {NhCommonTextPipe} from '../../i18n/nh-common-text.pipe';
import {NhCommonIconComponent, NhCommonIconName} from '../nh-common-icon/component';

/**
 * Why a list is empty: it has no data yet, the current search or filter matches nothing, or it
 * could not be loaded.
 */
export type NhEmptyStateVariant = 'no-data' | 'no-results' | 'error';

/** Heading levels that `nh-empty-state` can expose for its heading. */
export type NhEmptyStateHeadingLevel = 2 | 3 | 4 | 5 | 6;

const variantIcons: Record<NhEmptyStateVariant, NhCommonIconName> = {
  'no-data': 'empty',
  'no-results': 'no-results',
  error: 'error'
};

/**
 * The state of a list without items. `variant` picks the default icon and texts for "no data",
 * "no results for this filter" and "could not load"; `heading` and `description` replace those
 * texts. Project an icon with the `nhEmptyStateIcon` attribute and actions, such as "Clear filters"
 * or "Try again", as other content. The texts are announced politely, an error assertively.
 */
@Component({
  selector: 'nh-empty-state',
  standalone: true,
  imports: [NhCommonTextPipe, NhCommonIconComponent],
  templateUrl: './component.html',
  styleUrl: './component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NhEmptyStateComponent {
  /** The reason the list is empty. */
  readonly variant = input<NhEmptyStateVariant>('no-data');
  /** The heading. Defaults to `nh-common.empty-state.<variant>.title`. */
  readonly heading = input<string | undefined>(undefined);
  /** The explanation. Defaults to `nh-common.empty-state.<variant>.description`; an empty string hides it. */
  readonly description = input<string | undefined>(undefined);
  /** Exposes the heading as a heading of this level; without it the heading is a paragraph. */
  readonly headingLevel = input<NhEmptyStateHeadingLevel | undefined>(undefined);

  readonly iconName = computed(() => variantIcons[this.variant()] ?? 'empty');
  readonly headingKey = computed(() => `nh-common.empty-state.${this.variant()}.title`);
  readonly descriptionKey = computed(() => `nh-common.empty-state.${this.variant()}.description`);
  readonly liveRole = computed(() => this.variant() === 'error' ? 'alert' : 'status');
}
