import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** The glyphs the controls draw themselves, so they need no icon library (internal). */
export type NhSelectIconName = 'chevron-down' | 'check' | 'x' | 'plus' | 'warning';

const paths: Record<NhSelectIconName, string> = {
  'chevron-down': 'M6 9l6 6 6-6',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  x: 'M7 7l10 10M17 7L7 17',
  plus: 'M12 5v14M5 12h14',
  warning: 'M12 4L2.5 20h19zM12 10v4M12 17h.01'
};

/** A small outline glyph that follows the text colour; always decorative (internal). */
@Component({
  selector: 'nh-select-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true' },
  template: `
    <svg viewBox="0 0 24 24" focusable="false">
      <path [attr.d]="path()" />
    </svg>
  `,
  styles: [`
    :host {
      display: inline-flex;
      flex: none;
      width: var(--nh-select-icon-size, 1em);
      height: var(--nh-select-icon-size, 1em);
    }

    svg {
      width: 100%;
      height: 100%;
      fill: none;
      stroke: currentColor;
      stroke-width: 2;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
  `]
})
export class NhSelectIconComponent {
  readonly name = input.required<NhSelectIconName>();
  readonly path = computed(() => paths[this.name()]);
}
