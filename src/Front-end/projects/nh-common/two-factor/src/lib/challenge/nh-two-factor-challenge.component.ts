import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  input,
  output,
  signal,
  viewChild
} from '@angular/core';
import {DatePipe} from '@angular/common';
import {TranslatePipe} from '@ngx-translate/core';
import {
  INhAuthorization,
  NhAuthService,
  NhTwoFactorChallenge,
  NhTwoFactorEmailCodeSent,
  NhTwoFactorMethods,
  NhTwoFactorVerifyModel,
  TaskResult
} from '@newheap/platform-common';
import {NhTwoFactorTranslationMerger} from '../i18n/nh-two-factor-translations';
import {nhTwoFactorErrorKeys, nhTwoFactorInputChecked, nhTwoFactorInputValue, nhTwoFactorStepEnded} from '../nh-two-factor-errors';

let nextId = 0;

/**
 * Completes a second-factor challenge with a code, an e-mailed code, a recovery code or a
 * passkey. The session is stored by the auth service before `authenticated` emits.
 */
@Component({
  selector: 'nh-two-factor-challenge',
  standalone: true,
  imports: [TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-two-factor-challenge.component.html',
  styleUrl: '../nh-two-factor.scss'
})
export class NhTwoFactorChallengeComponent {
  private readonly auth = inject(NhAuthService);
  private readonly injector = inject(Injector);
  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('codeInput');

  /** The pending challenge from the login response. */
  readonly challenge = input.required<NhTwoFactorChallenge>();
  /** Whether to offer "remember this device". The server still decides whether it may. */
  readonly allowRememberDevice = input(true);

  readonly authenticated = output<INhAuthorization>();
  /** The challenge expired or the account was locked; the user has to sign in again. */
  readonly expired = output<void>();
  readonly cancelled = output<void>();

  readonly id = `nh-two-factor-challenge-${nextId++}`;
  readonly passkeySupported = this.auth.isPasskeySupported();
  readonly selectedMethod = signal<string | null>(null);
  readonly code = signal('');
  readonly rememberDevice = signal(false);
  readonly busy = signal(false);
  readonly errorKeys = signal<string[]>([]);
  readonly emailSent = signal<NhTwoFactorEmailCodeSent | null>(null);

  readonly methods = computed(() => this.challenge().methods
    .filter(method => method !== NhTwoFactorMethods.passkey || this.passkeySupported));
  readonly method = computed(() => this.selectedMethod() ?? this.defaultMethod());
  readonly usesCode = computed(() => this.method() !== NhTwoFactorMethods.passkey);
  readonly numericCode = computed(() => this.method() !== NhTwoFactorMethods.recoveryCode);

  readonly value = nhTwoFactorInputValue;
  readonly checked = nhTwoFactorInputChecked;

  constructor() {
    inject(NhTwoFactorTranslationMerger).start();
  }

  selectMethod(method: string): void {
    this.selectedMethod.set(method);
    this.code.set('');
    this.errorKeys.set([]);
    afterNextRender(() => this.codeInput()?.nativeElement.focus(), {injector: this.injector});
  }

  async verify(): Promise<void> {
    if (this.busy()) {
      return;
    }

    if (!this.usesCode()) {
      await this.run(() => this.auth.verifyTwoFactorWithPasskey(this.challenge().challengeToken, this.rememberDevice()));
      return;
    }

    const code = this.code().trim();
    if (!code) {
      this.errorKeys.set(['nh-two-factor.invalid-code']);
      return;
    }

    await this.run(() => this.auth.verifyTwoFactor(new NhTwoFactorVerifyModel({
      challengeToken: this.challenge().challengeToken,
      method: this.method(),
      code: code,
      rememberDevice: this.rememberDevice()
    })));
  }

  async sendEmailCode(): Promise<void> {
    if (this.busy()) {
      return;
    }

    this.busy.set(true);
    this.errorKeys.set([]);
    const result = await this.auth.sendTwoFactorEmailCode(this.challenge().challengeToken);
    this.busy.set(false);

    if (!result.isSuccess) {
      this.fail(result);
      return;
    }

    this.emailSent.set(result.data ?? null);
    afterNextRender(() => this.codeInput()?.nativeElement.focus(), {injector: this.injector});
  }

  private async run(verification: () => Promise<TaskResult<INhAuthorization>>): Promise<void> {
    this.busy.set(true);
    this.errorKeys.set([]);
    const result = await verification();
    this.busy.set(false);

    if (!result.isSuccess || !result.data) {
      this.code.set('');
      this.fail(result);
      return;
    }

    this.authenticated.emit(result.data);
  }

  private fail(result: TaskResult<unknown>): void {
    this.errorKeys.set(nhTwoFactorErrorKeys(result));
    if (nhTwoFactorStepEnded(result)) {
      this.expired.emit();
    }
  }

  private defaultMethod(): string {
    const methods = this.methods();
    return methods.find(method => method !== NhTwoFactorMethods.recoveryCode)
      ?? methods[0]
      ?? NhTwoFactorMethods.authenticator;
  }
}
