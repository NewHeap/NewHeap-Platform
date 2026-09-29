import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  input,
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
  NhTwoFactorChallenge,
  NhTwoFactorEmailCodeSent,
  NhTwoFactorEnrollmentResult,
  NhTwoFactorMethods,
  TaskResult
} from '@newheap/platform-common';
import {NhTwoFactorTranslationMerger} from '../i18n/nh-two-factor-translations';
import {nhTwoFactorErrorKeys, nhTwoFactorInputValue, nhTwoFactorStepEnded} from '../nh-two-factor-errors';
import {NhTwoFactorRecoveryCodesComponent} from '../recovery-codes/nh-two-factor-recovery-codes.component';

let nextId = 0;

/**
 * Enrolls a second factor for a user whom the policy requires to use one, during sign-in.
 * The session is stored by the auth service; `completed` emits after the user saved the
 * recovery codes.
 */
@Component({
  selector: 'nh-two-factor-enrollment',
  standalone: true,
  imports: [TranslatePipe, DatePipe, NhTwoFactorRecoveryCodesComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nh-two-factor-enrollment.component.html',
  styleUrl: '../nh-two-factor.scss'
})
export class NhTwoFactorEnrollmentComponent implements OnInit {
  private readonly auth = inject(NhAuthService);
  private readonly injector = inject(Injector);
  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('codeInput');

  /** The enrollment step from the login response (status `enrollment-required`). */
  readonly enrollment = input.required<NhTwoFactorChallenge>();

  readonly completed = output<NhTwoFactorEnrollmentResult>();
  /** The enrollment expired or the account was locked; the user has to sign in again. */
  readonly expired = output<void>();
  readonly cancelled = output<void>();

  readonly id = `nh-two-factor-enrollment-${nextId++}`;
  readonly passkeySupported = this.auth.isPasskeySupported();
  readonly selectedMethod = signal<string | null>(null);
  readonly setup = signal<NhAuthenticatorSetup | null>(null);
  readonly emailSent = signal<NhTwoFactorEmailCodeSent | null>(null);
  readonly code = signal('');
  readonly passkeyName = signal('');
  readonly busy = signal(false);
  readonly errorKeys = signal<string[]>([]);
  readonly result = signal<NhTwoFactorEnrollmentResult | null>(null);

  readonly methods = computed(() => this.enrollment().methods
    .filter(method => method !== NhTwoFactorMethods.passkey || this.passkeySupported));
  readonly method = computed(() => this.selectedMethod() ?? this.methods()[0] ?? NhTwoFactorMethods.authenticator);

  readonly value = nhTwoFactorInputValue;

  constructor() {
    inject(NhTwoFactorTranslationMerger).start();
  }

  ngOnInit(): void {
    if (this.method() === NhTwoFactorMethods.authenticator) {
      void this.startAuthenticator();
    }
  }

  selectMethod(method: string): void {
    this.selectedMethod.set(method);
    this.code.set('');
    this.errorKeys.set([]);

    if (method === NhTwoFactorMethods.authenticator && !this.setup()) {
      void this.startAuthenticator();
    }
  }

  async startAuthenticator(): Promise<void> {
    const setup = await this.run(() => this.auth.beginEnrollmentAuthenticatorSetup(this.token()));
    if (setup) {
      this.setup.set(setup);
      this.focusCode();
    }
  }

  async sendEmailCode(): Promise<void> {
    const sent = await this.run(() => this.auth.sendEnrollmentEmailCode(this.token()));
    if (sent) {
      this.emailSent.set(sent);
      this.focusCode();
    }
  }

  async confirm(): Promise<void> {
    if (this.busy()) {
      return;
    }

    const method = this.method();
    if (method === NhTwoFactorMethods.passkey) {
      this.complete(await this.run(() => this.auth.enrollPasskey(this.token(), this.passkeyName().trim() || undefined)));
      return;
    }

    const code = this.code().trim();
    if (!code) {
      this.errorKeys.set(['nh-two-factor.invalid-code']);
      return;
    }

    const confirmation = method === NhTwoFactorMethods.email
      ? () => this.auth.confirmEnrollmentEmail(this.token(), code)
      : () => this.auth.confirmEnrollmentAuthenticator(this.token(), code);

    this.complete(await this.run(confirmation));
  }

  /** Emits the result after the user saved the recovery codes. */
  finish(): void {
    const result = this.result();
    if (result) {
      this.completed.emit(result);
    }
  }

  private complete(result: NhTwoFactorEnrollmentResult | undefined): void {
    if (!result) {
      this.code.set('');
      return;
    }

    if (result.recoveryCodes?.length) {
      this.result.set(result);
      return;
    }

    this.completed.emit(result);
  }

  private async run<T>(action: () => Promise<TaskResult<T>>): Promise<T | undefined> {
    this.busy.set(true);
    this.errorKeys.set([]);
    const result = await action();
    this.busy.set(false);

    if (!result.isSuccess) {
      this.errorKeys.set(nhTwoFactorErrorKeys(result));
      if (nhTwoFactorStepEnded(result)) {
        this.expired.emit();
      }

      return undefined;
    }

    return result.data;
  }

  private token(): string {
    return this.enrollment().challengeToken;
  }

  private focusCode(): void {
    afterNextRender(() => this.codeInput()?.nativeElement.focus(), {injector: this.injector});
  }
}
