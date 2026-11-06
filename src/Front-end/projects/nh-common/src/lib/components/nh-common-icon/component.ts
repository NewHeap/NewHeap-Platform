import {ChangeDetectionStrategy, Component, InjectionToken, computed, inject, input} from '@angular/core';

/** The icons that the list building blocks of `@newheap/platform-common` show. */
export type NhCommonIconName =
  | 'previous'
  | 'next'
  | 'empty'
  | 'no-results'
  | 'error'
  | 'background-operations';

/**
 * Optional icon classes of the application's icon library for the list building blocks, for
 * example `{previous: 'ph ph-caret-left', next: 'ph ph-caret-right'}`. Icons without a class use
 * the library's small built-in outline glyphs. Provide it once in the application root.
 */
export const NH_COMMON_ICONS = new InjectionToken<Partial<Record<NhCommonIconName, string>>>('NH_COMMON_ICONS');

const builtInPaths: Record<NhCommonIconName, string> = {
  previous: 'M15 6l-6 6 6 6',
  next: 'M9 6l6 6-6 6',
  empty: 'M4 13l2.5-7h11L20 13v6H4zM4 13h5l1 2h4l1-2h5',
  'no-results': 'M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 1 0 0-13zM15.5 15.5L20 20M8.5 8.5l4 4M12.5 8.5l-4 4',
  error: 'M12 4L2.5 20h19zM12 10v4M12 17h.01',
  'background-operations': 'M3 12h4l3-7 4 14 3-7h4'
};

/** Decorative icon of a list building block. The surrounding control provides the accessible name. */
@Component({
  selector: 'nh-common-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (iconClass(); as iconClass) {
      <i [class]="iconClass" aria-hidden="true"></i>
    } @else {
      <svg viewBox="0 0 24 24" width="1em" height="1em" aria-hidden="true" focusable="false">
        <path [attr.d]="path()" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    }
  `,
  styles: [`
    :host { display: inline-flex; align-items: center; justify-content: center; line-height: 1; }
    svg { display: block; }
  `]
})
export class NhCommonIconComponent {
  private readonly icons = inject(NH_COMMON_ICONS, {optional: true});

  readonly name = input.required<NhCommonIconName>();
  readonly iconClass = computed(() => this.icons?.[this.name()] ?? null);
  readonly path = computed(() => builtInPaths[this.name()]);
}
