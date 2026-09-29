import {ChangeDetectionStrategy, Component, inject, input, output, signal} from '@angular/core';
import {TranslatePipe} from '@ngx-translate/core';
import {INhAuthorization, NhAuthenticationStep, NhAuthService} from '@newheap/platform-common';
import {NhTwoFactorTranslationMerger} from '../i18n/nh-two-factor-translations';
import {nhTwoFactorErrorKeys} from '../nh-two-factor-errors';

let nextId = 0;

/**
 * Signs in with a discoverable passkey, without a username or password. Renders nothing in
 * browsers without passkey support.
 */
@Component({
  selector: 'nh-passkey-login-button',
  standalone: true,
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-passkey-login-button.component.html',
  styleUrl: '../nh-two-factor.scss'
})
export class NhPasskeyLoginButtonComponent {
  private readonly auth = inject(NhAuthService);

  /** Whether the button shows failures itself. */
  readonly showErrors = input(true);

  readonly authenticated = output<INhAuthorization>();
  /** The policy asked for more: a second-factor challenge or an enrollment. */
  readonly pending = output<NhAuthenticationStep>();
  /** Translation keys of a failed sign-in. */
  readonly failed = output<string[]>();

  readonly id = `nh-passkey-login-${nextId++}`;
  readonly supported = this.auth.isPasskeySupported();
  readonly busy = signal(false);
  readonly errorKeys = signal<string[]>([]);

  constructor() {
    inject(NhTwoFactorTranslationMerger).start();
  }

  async signIn(): Promise<void> {
    if (this.busy()) {
      return;
    }

    this.busy.set(true);
    this.errorKeys.set([]);
    const result = await this.auth.signInWithPasskey();
    this.busy.set(false);

    if (!result.isSuccess || !result.data) {
      const keys = nhTwoFactorErrorKeys(result);
      this.errorKeys.set(keys);
      this.failed.emit(keys);
      return;
    }

    if (result.data.status === 'authenticated' && result.data.authorization) {
      this.authenticated.emit(result.data.authorization);
      return;
    }

    this.pending.emit(result.data);
  }
}
