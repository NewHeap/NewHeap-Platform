import { ChangeDetectionStrategy, Component, InjectionToken, computed, inject, input } from '@angular/core';

export type NhAssistantIconName =
  | 'assistant'
  | 'close'
  | 'send'
  | 'stop'
  | 'plus'
  | 'list'
  | 'trash'
  | 'tool'
  | 'check'
  | 'x'
  | 'warning'
  | 'chevron-down'
  | 'clock'
  | 'settings'
  | 'admin'
  | 'edit'
  | 'refresh'
  | 'link';

/**
 * Optional icon classes of the host's icon library, for example `{ close: 'ph ph-x' }`.
 * Icons without a class use the library's small built-in outline glyphs.
 */
export const NH_ASSISTANT_ICONS = new InjectionToken<Partial<Record<NhAssistantIconName, string>>>('NH_ASSISTANT_ICONS');

const builtInPaths: Record<NhAssistantIconName, string> = {
  assistant: 'M12 3l1.8 4.7 4.7 1.8-4.7 1.8L12 16l-1.8-4.7-4.7-1.8 4.7-1.8zM18.5 15l.8 2 2 .8-2 .8-.8 2-.8-2-2-.8 2-.8z',
  close: 'M6 6l12 12M18 6L6 18',
  send: 'M4 12l16-8-6 16-2.5-6.5zM11.5 13.5L20 4',
  stop: 'M7 7h10v10H7z',
  plus: 'M12 5v14M5 12h14',
  list: 'M9 6h11M9 12h11M9 18h11M4.5 6h.01M4.5 12h.01M4.5 18h.01',
  trash: 'M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3',
  tool: 'M14.7 6.3a4 4 0 0 0-5.4 5.4L4 17l3 3 5.3-5.3a4 4 0 0 0 5.4-5.4l-2.5 2.5-2.1-.4-.4-2.1z',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  x: 'M7 7l10 10M17 7L7 17',
  warning: 'M12 4L2.5 20h19zM12 10v4M12 17h.01',
  'chevron-down': 'M6 9l6 6 6-6',
  clock: 'M12 3a9 9 0 1 0 0 18 9 9 0 1 0 0-18zM12 7v5l3 2',
  settings: 'M4 7h9M17 7h3M4 17h3M11 17h9M15 5v4M9 15v4',
  admin: 'M12 3l7 3v5c0 4.5-3 8.2-7 10-4-1.8-7-5.5-7-10V6zM9.5 12l2 2 3.5-3.5',
  edit: 'M4 20h4L19 9l-4-4L4 16zM13.5 6.5l4 4',
  refresh: 'M20 11a8 8 0 0 0-14.3-4.9L4 8M4 4v4h4M4 13a8 8 0 0 0 14.3 4.9L20 16M20 20v-4h-4',
  link: 'M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1'
};

/** Decorative icon. The surrounding control provides the accessible name. */
@Component({
  selector: 'nh-assistant-icon',
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
    :host { display: inline-flex; align-items: center; justify-content: center; line-height: 1; font-size: 1.15em; }
    svg { display: block; }
  `]
})
export class NhAssistantIconComponent {
  private readonly icons = inject(NH_ASSISTANT_ICONS, { optional: true });

  readonly name = input.required<NhAssistantIconName>();
  readonly iconClass = computed(() => this.icons?.[this.name()] ?? null);
  readonly path = computed(() => builtInPaths[this.name()]);
}
