import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { ɵNhAssistantIconComponent as NhAssistantIconComponent } from '@newheap/platform-ai-chat';
import { PackageLockView } from './package-parts';

/**
 * The lock next to the label of a field that an agent package locks (internal): the field shows
 * read-only, and the mark names the package and its version, as a tooltip and for screen readers.
 */
@Component({
  selector: 'nh-assistant-package-locked-mark',
  standalone: true,
  imports: [TranslatePipe, NhAssistantIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (lock(); as current) {
      <span
        class="locked-mark"
        data-locked-mark
        [attr.title]="'nh-assistant.admin.packages.lock.field' | translate: { package: current.packageId, version: current.packageVersion }">
        <nh-assistant-icon name="lock" />
        <span class="visually-hidden">{{ 'nh-assistant.admin.packages.lock.field' | translate: { package: current.packageId, version: current.packageVersion } }}</span>
      </span>
    }
  `,
  styles: [`
    :host { display: inline; }
    .locked-mark { display: inline-flex; align-items: center; margin-left: 6px; color: var(--nha-text-muted); vertical-align: middle; }
    .visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
  `]
})
export class NhAssistantPackageLockedMarkComponent {
  /** The lock of the field's part; nothing shows without one. */
  readonly lock = input<PackageLockView | null>(null);
}
