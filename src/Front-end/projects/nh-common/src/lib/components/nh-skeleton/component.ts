import {
  ChangeDetectionStrategy,
  Component,
  afterNextRender,
  booleanAttribute,
  computed,
  input,
  numberAttribute,
  signal
} from '@angular/core';
import {NhCommonTextPipe} from '../../i18n/nh-common-text.pipe';

/**
 * The placeholder shape: a text `line`, a `block` such as a card or control, a round `avatar`, or a
 * list `row` of an avatar with two lines.
 */
export type NhSkeletonShape = 'line' | 'block' | 'avatar' | 'row';

/**
 * A loading placeholder for a list or card. The shapes are hidden from assistive technology; a
 * separate polite status says "Loading…" once (`label` replaces the text, `[announce]="false"`
 * leaves the announcement to another element). The shimmer only runs without a reduced-motion
 * preference.
 */
@Component({
  selector: 'nh-skeleton',
  standalone: true,
  imports: [NhCommonTextPipe],
  templateUrl: './component.html',
  styleUrl: './component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NhSkeletonComponent {
  /** The placeholder shape. */
  readonly shape = input<NhSkeletonShape>('line');
  /** How many lines, blocks, avatars or rows to show. */
  readonly count = input(1, {transform: numberAttribute});
  /** A CSS width for lines, blocks and avatars, such as `60%` or `12rem`. */
  readonly width = input<string | undefined>(undefined);
  /** A CSS height for lines, blocks and avatars, such as `2.5rem`. */
  readonly height = input<string | undefined>(undefined);
  /** The announced text. Defaults to `nh-common.skeleton.loading` ("Loading…"). */
  readonly label = input<string | undefined>(undefined);
  /** Announces the loading state; switch it off when several skeletons load one region. */
  readonly announce = input(true, {transform: booleanAttribute});

  readonly indexes = computed(() => {
    const count = Number.isFinite(this.count()) ? Math.min(Math.max(1, Math.floor(this.count())), 50) : 1;
    return Array.from({length: count}, (_, index) => index);
  });

  /**
   * The status text is added after the first render: a live region that is inserted with its text
   * already in it is not announced by every screen reader.
   */
  readonly announced = signal(false);

  constructor() {
    afterNextRender(() => this.announced.set(true));
  }
}
