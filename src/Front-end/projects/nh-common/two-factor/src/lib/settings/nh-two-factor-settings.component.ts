import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  OnInit,
  output,
  signal,
  viewChild
} from '@angular/core';
import {DatePipe} from '@angular/common';
import {TranslatePipe} from '@ngx-translate/core';
import {
  NhAuthenticatorSetup,
  NhAuthService,
  NhPasskey,
  NhTwoFactorChange,
  NhTwoFactorEmailCodeSent,
  NhTwoFactorMethods,
  NhTwoFactorReauthentication,
  NhTwoFactorStatus,
  TaskResult
} from '@newheap/platform-common';
import {NhTwoFactorTranslationMerger} from '../i18n/nh-two-factor-translations';
import {nhTwoFactorErrorKeys, nhTwoFactorInputValue} from '../nh-two-factor-errors';
import {NhTwoFactorRecoveryCodesComponent} from '../recovery-codes/nh-two-factor-recovery-codes.component';

/** A change on the settings page; some need reauthentication before they run. */
export type NhTwoFactorSettingsAction =
  | { kind: 'authenticator' }
  | { kind: 'email' }
  | { kind: 'recovery-codes' }
  | { kind: 'forget-devices' }
  | { kind: 'disable' }
  | { kind: 'add-passkey' }
  | { kind: 'remove-passkey', passkeyId: string };

const passwordReauthentication = 'password';

let nextId = 0;

/**
 * Two-factor settings of the signed-in user: authenticator app, e-mail codes, passkeys,
 * recovery codes, remembered devices and turning two-factor authentication off. Sensitive
 * changes ask for the password or a code first.
 */
@Component({
  selector: 'nh-two-factor-settings',
  standalone: true,
  imports: [TranslatePipe, DatePipe, NhTwoFactorRecoveryCodesComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-two-factor-settings.component.html',
  styleUrl: '../nh-two-factor.scss'
})
export class NhTwoFactorSettingsComponent implements OnInit {
  private readonly auth = inject(NhAuthService);
  private readonly injector = inject(Injector);
  private readonly reauthenticationInput = viewChild<ElementRef<HTMLInputElement>>('reauthenticationInput');
  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('codeInput');

  /** Emits the reloaded status after every saved change. */
  readonly changed = output<NhTwoFactorStatus>();

  readonly id = `nh-two-factor-settings-${nextId++}`;
  readonly passkeySupported = this.auth.isPasskeySupported();
  readonly status = signal<NhTwoFactorStatus | null>(null);
  readonly passkeys = signal<NhPasskey[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly errorKeys = signal<string[]>([]);
  readonly noticeKey = signal<string | null>(null);
  readonly setupPanel = signal<'authenticator' | 'email' | null>(null);
  readonly setup = signal<NhAuthenticatorSetup | null>(null);
  readonly emailSent = signal<NhTwoFactorEmailCodeSent | null>(null);
  readonly code = signal('');
  readonly recoveryCodes = signal<string[] | null>(null);
  readonly pendingAction = signal<NhTwoFactorSettingsAction | null>(null);
  readonly reauthenticationMethod = signal(passwordReauthentication);
  readonly reauthenticationSecret = signal('');
  readonly newPasskeyName = signal('');
  readonly renamingId = signal<string | null>(null);
  readonly renameValue = signal('');

  readonly enabled = computed(() => this.status()?.enabled === true);
  readonly required = computed(() => this.status()?.required === true);
  readonly hasAuthenticator = computed(() => this.hasMethod(NhTwoFactorMethods.authenticator));
  readonly hasEmail = computed(() => this.hasMethod(NhTwoFactorMethods.email));
  readonly authenticatorAvailable = computed(() => this.isAvailable(NhTwoFactorMethods.authenticator));
  readonly emailAvailable = computed(() => this.isAvailable(NhTwoFactorMethods.email));
  readonly passkeysAvailable = computed(() => this.isAvailable(NhTwoFactorMethods.passkey));
  readonly recoveryCodesAvailable = computed(() => this.isAvailable(NhTwoFactorMethods.recoveryCode));
  readonly reauthenticationMethods = computed(() => [
    passwordReauthentication,
    ...(this.status()?.methods ?? []).filter(method =>
      method === NhTwoFactorMethods.authenticator || method === NhTwoFactorMethods.recoveryCode)
  ]);

  readonly value = nhTwoFactorInputValue;

  constructor() {
    inject(NhTwoFactorTranslationMerger).start();
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    const status = await this.auth.getTwoFactorStatus();

    if (!status.isSuccess || !status.data) {
      this.errorKeys.set(nhTwoFactorErrorKeys(status));
      this.loading.set(false);
      return;
    }

    this.status.set(status.data);

    if (status.data.availableMethods.includes(NhTwoFactorMethods.passkey)) {
      const passkeys = await this.auth.getPasskeys();
      this.passkeys.set(passkeys.data ?? []);
    }

    this.loading.set(false);
  }

  /** Starts a change, asking for reauthentication first when the change needs it. */
  request(action: NhTwoFactorSettingsAction): void {
    this.errorKeys.set([]);
    this.noticeKey.set(null);

    if (!this.needsReauthentication(action)) {
      void this.perform(action);
      return;
    }

    this.pendingAction.set(action);
    this.reauthenticationMethod.set(passwordReauthentication);
    this.reauthenticationSecret.set('');
    afterNextRender(() => this.reauthenticationInput()?.nativeElement.focus(), {injector: this.injector});
  }

  async confirmReauthentication(): Promise<void> {
    const action = this.pendingAction();
    const secret = this.reauthenticationSecret().trim();
    if (!action || this.busy()) {
      return;
    }

    if (!secret) {
      this.errorKeys.set(['nh-two-factor.reauthentication-required']);
      return;
    }

    const method = this.reauthenticationMethod();
    const reauthentication = method === passwordReauthentication
      ? new NhTwoFactorReauthentication({password: secret})
      : new NhTwoFactorReauthentication({method: method, code: secret});

    this.pendingAction.set(null);
    this.reauthenticationSecret.set('');
    await this.perform(action, reauthentication);
  }

  cancelReauthentication(): void {
    this.pendingAction.set(null);
    this.reauthenticationSecret.set('');
    this.errorKeys.set([]);
  }

  async confirmSetup(): Promise<void> {
    const code = this.code().trim();
    if (!code) {
      this.errorKeys.set(['nh-two-factor.invalid-code']);
      return;
    }

    const change = await this.run(() => this.setupPanel() === 'email'
      ? this.auth.confirmEmailSetup(code)
      : this.auth.confirmAuthenticator(code));

    this.code.set('');
    if (change) {
      this.closeSetup();
      await this.applyChange(change);
    }
  }

  closeSetup(): void {
    this.setupPanel.set(null);
    this.setup.set(null);
    this.emailSent.set(null);
    this.code.set('');
  }

  async resendEmailCode(): Promise<void> {
    const sent = await this.run(() => this.auth.beginEmailSetup());
    if (sent) {
      this.emailSent.set(sent);
    }
  }

  startRename(passkey: NhPasskey): void {
    this.renamingId.set(passkey.id);
    this.renameValue.set(passkey.name);
  }

  cancelRename(): void {
    this.renamingId.set(null);
    this.renameValue.set('');
  }

  async saveRename(passkey: NhPasskey): Promise<void> {
    const name = this.renameValue().trim();
    if (!name || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.errorKeys.set([]);
    const result = await this.auth.renamePasskey(passkey.id, name);
    this.busy.set(false);

    if (!result.isSuccess) {
      this.errorKeys.set(nhTwoFactorErrorKeys(result));
      return;
    }

    this.cancelRename();
    this.passkeys.update(passkeys => passkeys.map(item => item.id === passkey.id ? new NhPasskey({...item, name: name}) : item));
    this.noticeKey.set('nh-two-factor.ui.change-saved');
  }

  private needsReauthentication(action: NhTwoFactorSettingsAction): boolean {
    switch (action.kind) {
      case 'authenticator':
      case 'email':
      case 'add-passkey':
        return this.enabled();
      default:
        return true;
    }
  }

  private async perform(action: NhTwoFactorSettingsAction, reauthentication?: NhTwoFactorReauthentication): Promise<void> {
    switch (action.kind) {
      case 'authenticator': {
        const setup = await this.run(() => this.auth.beginAuthenticatorSetup(reauthentication));
        if (setup) {
          this.openSetup('authenticator');
          this.setup.set(setup);
        }
        return;
      }
      case 'email': {
        const sent = await this.run(() => this.auth.beginEmailSetup(reauthentication));
        if (sent) {
          this.openSetup('email');
          this.emailSent.set(sent);
        }
        return;
      }
      case 'recovery-codes':
        await this.applyChange(await this.run(() => this.auth.regenerateRecoveryCodes(reauthentication!)));
        return;
      case 'forget-devices':
        await this.applyChange(
          await this.run(() => this.auth.forgetTwoFactorDevices(reauthentication!)),
          'nh-two-factor.ui.devices-forgotten');
        return;
      case 'disable':
        await this.applyChange(await this.run(() => this.auth.disableTwoFactor(reauthentication!)));
        return;
      case 'add-passkey': {
        const name = this.newPasskeyName().trim() || undefined;
        const change = await this.run(() => this.auth.registerPasskey(name, reauthentication));
        if (change) {
          this.newPasskeyName.set('');
        }
        await this.applyChange(change);
        return;
      }
      case 'remove-passkey':
        await this.applyChange(await this.run(() => this.auth.removePasskey(action.passkeyId, reauthentication!)));
        return;
    }
  }

  private openSetup(panel: 'authenticator' | 'email'): void {
    this.closeSetup();
    this.setupPanel.set(panel);
    afterNextRender(() => this.codeInput()?.nativeElement.focus(), {injector: this.injector});
  }

  private async applyChange(change: NhTwoFactorChange | undefined, noticeKey?: string): Promise<void> {
    if (!change) {
      return;
    }

    this.recoveryCodes.set(change.recoveryCodes?.length ? change.recoveryCodes : null);
    this.noticeKey.set(noticeKey ?? (change.sessionRenewed ? 'nh-two-factor.ui.session-renewed' : 'nh-two-factor.ui.change-saved'));

    await this.load();
    const status = this.status();
    if (status) {
      this.changed.emit(status);
    }
  }

  private async run<T>(action: () => Promise<TaskResult<T>>): Promise<T | undefined> {
    this.busy.set(true);
    this.errorKeys.set([]);
    const result = await action();
    this.busy.set(false);

    if (!result.isSuccess) {
      this.errorKeys.set(nhTwoFactorErrorKeys(result));
      return undefined;
    }

    return result.data;
  }

  private hasMethod(method: string): boolean {
    return this.status()?.methods.includes(method) === true;
  }

  private isAvailable(method: string): boolean {
    return this.status()?.availableMethods.includes(method) === true;
  }
}
