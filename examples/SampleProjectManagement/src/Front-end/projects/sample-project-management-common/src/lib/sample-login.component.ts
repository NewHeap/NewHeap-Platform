import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import {
  AuthenticateModel,
  NhAuthenticationStep,
  NhAuthenticationStepStatuses,
  NhTwoFactorChallenge
} from '@newheap/platform-common';
import {
  NhPasskeyLoginButtonComponent,
  NhTwoFactorChallengeComponent,
  NhTwoFactorEnrollmentComponent
} from '@newheap/platform-common/two-factor';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import {
  LOGIN_DEMO_ACCOUNTS,
  LoginDemoAccount,
  TWO_FACTOR_DEMO_ACCOUNT
} from './authorization-sample.models';
import { SampleAuthService } from './sample-auth.service';

@Component({
  selector: 'app-sample-login',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    TranslateModule,
    NhTwoFactorChallengeComponent,
    NhTwoFactorEnrollmentComponent,
    NhPasskeyLoginButtonComponent
  ],
  templateUrl: './sample-login.component.html',
  styleUrl: './sample-login.component.scss'
})
export class SampleLoginComponent {
  private readonly authService = inject(SampleAuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly translate = inject(TranslateService);

  username = 'sample@example.test';
  password = 'Sample123!';
  readonly demoAccounts = LOGIN_DEMO_ACCOUNTS;
  readonly errorMessage = signal('');
  readonly isSubmitting = signal(false);
  readonly sessionExpired = this.route.snapshot.queryParamMap.get('reason') === 'session-expired';

  /**
   * The second-factor challenge or required enrollment of this sign-in. A challenge that an
   * external sign-in passed back, or one pending in this tab, resumes after a reload.
   */
  readonly pendingStep = signal<NhTwoFactorChallenge | null>(
    this.authService.consumeExternalTwoFactorChallenge()
    ?? this.authService.getPendingTwoFactorChallenge()
    ?? null);
  readonly enrollmentRequired = computed(() =>
    this.pendingStep()?.status === NhAuthenticationStepStatuses.enrollmentRequired);
  readonly demoAuthenticatorKey = signal<string | null>(null);

  selectAccount(account: LoginDemoAccount): void {
    this.username = account.email;
    this.password = 'Sample123!';
  }

  async login(): Promise<void> {
    if (this.isSubmitting()) {
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set('');

    try {
      const step = await this.authService.authenticateInteractive(new AuthenticateModel({
        username: this.username,
        password: this.password
      }));

      if (!step.isSuccess) {
        this.errorMessage.set(this.getErrorMessage(step));
        return;
      }

      if (step.data?.status !== 'authenticated') {
        this.showPendingStep(step.data);
        return;
      }

      await this.completeSignIn();
    } catch {
      this.errorMessage.set(this.loginFailedMessage());
    } finally {
      this.isSubmitting.set(false);
    }
  }

  showPendingStep(step: NhAuthenticationStep | undefined): void {
    this.demoAuthenticatorKey.set(
      this.username.toLowerCase() === TWO_FACTOR_DEMO_ACCOUNT.email ? TWO_FACTOR_DEMO_ACCOUNT.authenticatorKey : null);
    this.pendingStep.set(step?.challenge ?? null);
  }

  /** Loads the profile of the stored session and leaves the login page. */
  async completeSignIn(): Promise<void> {
    this.isSubmitting.set(true);

    try {
      const profileResult = await this.authService.reloadAuthorizationProfile();
      if (!profileResult.isSuccess) {
        this.pendingStep.set(null);
        this.errorMessage.set(this.getErrorMessage(profileResult));
        return;
      }

      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
      await this.router.navigateByUrl(this.isLocalReturnUrl(returnUrl) ? returnUrl : '/');
    } finally {
      this.isSubmitting.set(false);
    }
  }

  /** Returns to the password form, for example after the challenge expired. */
  leavePendingStep(messageKey?: string): void {
    this.authService.clearPendingTwoFactorChallenge();
    this.pendingStep.set(null);
    this.errorMessage.set(messageKey ? this.translate.instant(messageKey) : '');
  }

  private getErrorMessage(result: { items?: Array<{ errorMessages?: string[] }> }): string {
    const message = result.items
      ?.flatMap(item => item.errorMessages ?? [])
      .map(item => item.trim())
      .find(item => item.length > 0 && item.length <= 240 && !/[<>]/.test(item));

    if (message?.startsWith('nh-two-factor.')) {
      // Two-factor failures carry a translation key instead of display text.
      return this.translate.instant(message);
    }

    return message ?? this.loginFailedMessage();
  }

  private loginFailedMessage(): string {
    return this.translate.instant('project.login-failed');
  }

  private isLocalReturnUrl(url: string | null): url is string {
    return url?.startsWith('/') === true && !url.startsWith('//');
  }
}
