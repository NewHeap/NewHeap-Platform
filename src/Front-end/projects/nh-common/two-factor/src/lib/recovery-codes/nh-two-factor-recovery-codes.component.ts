import {ChangeDetectionStrategy, Component, inject, input, output, signal} from '@angular/core';
import {TranslatePipe} from '@ngx-translate/core';
import {NhTwoFactorTranslationMerger} from '../i18n/nh-two-factor-translations';
import {nhTwoFactorInputChecked} from '../nh-two-factor-errors';

let nextId = 0;

/**
 * Shows recovery codes once, with copy and download actions. The user confirms that the
 * codes were saved before continuing.
 */
@Component({
  selector: 'nh-two-factor-recovery-codes',
  standalone: true,
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-two-factor-recovery-codes.component.html',
  styleUrl: '../nh-two-factor.scss'
})
export class NhTwoFactorRecoveryCodesComponent {
  readonly codes = input.required<readonly string[]>();
  /** File name of the downloaded codes. */
  readonly fileName = input('recovery-codes.txt');
  readonly acknowledged = output<void>();

  readonly id = `nh-two-factor-recovery-codes-${nextId++}`;
  readonly saved = signal(false);
  readonly copied = signal(false);
  readonly checked = nhTwoFactorInputChecked;

  constructor() {
    inject(NhTwoFactorTranslationMerger).start();
  }

  async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.codes().join('\n'));
      this.copied.set(true);
    } catch {
      // Clipboard access can be denied; the codes stay visible for manual copying.
      this.copied.set(false);
    }
  }

  download(): void {
    const url = URL.createObjectURL(new Blob([this.codes().join('\r\n') + '\r\n'], {type: 'text/plain'}));
    const link = document.createElement('a');
    link.href = url;
    link.download = this.fileName();
    link.click();
    URL.revokeObjectURL(url);
  }
}
